#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

#if STEAMWORKS_NET
#define STEAMWORKS_NET_PACKAGE
#endif

using PurrNet.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
#if STEAMWORKS_NET_PACKAGE && !DISABLESTEAMWORKS
using Steamworks;
#endif
using UnityEngine;
using UnityEngine.Events;

namespace PurrLobby.Providers
{
    public class SteamLobbyProvider : MonoBehaviour
#if STEAMWORKS_NET_PACKAGE && !DISABLESTEAMWORKS
        , ILobbyProvider
#endif
    {
        public enum LobbyType
        {
            Private,
            FriendsOnly,
            Public,
        }

#if STEAMWORKS_NET_PACKAGE && !DISABLESTEAMWORKS
        public LobbyType lobbyType = LobbyType.Public;
        public int maxLobbiesToFind = 10;

        public event UnityAction<string> OnLobbyJoinFailed;
        public event UnityAction OnLobbyLeft;
        public event UnityAction<Lobby> OnLobbyUpdated;
#pragma warning disable CS0067
        public event UnityAction<List<LobbyUser>> OnLobbyPlayerListUpdated;
        public event UnityAction<List<FriendUser>> OnFriendListPulled;
#pragma warning restore CS0067
        public event UnityAction<string> OnError;
        
        [SerializeField] private bool handleSteamInit = false;
        // Steam's CallResult<T> has no built-in timeout — without this, a dropped connection or an
        // unresponsive Steam backend at exactly the wrong moment left CreateLobbyAsync awaiting forever,
        // with no exception and no way to recover short of restarting.
        [SerializeField] private float createLobbyTimeoutSeconds = 15f;

        // Static, not instance — RunSteamCallbacks()'s loop is a free-floating async operation that outlives
        // this component's own Unity lifecycle entirely (see that method's own doc comment), so this needs
        // to be reachable from code that never held a live reference to this instance (e.g. a quit-time
        // singleton that may run after this component's Lobby scene has long since unloaded).
        private static volatile bool _stopSteamCallbackLoop;

        // Guards ShutdownSteamApiOnce() below — see its own doc comment. _steamInitialized additionally
        // guards against ever calling SteamAPI.Shutdown() when Init() never actually succeeded (e.g.
        // handleSteamInit's own early-return above, Steam not running).
        private static bool _steamInitialized;
        private static bool _steamShutdownCalled;

        // The correct way for any other code (e.g. CharacterRegistry.OnApplicationQuit()) to trigger a real
        // SteamAPI.Shutdown(). Signals the loop to stop (so if it's still alive and its next Task.Delay
        // continuation DOES get a chance to run, it exits cleanly without calling RunCallbacks() again) AND
        // calls the real shutdown directly, right here, synchronously — NOT deferred to the loop noticing
        // the flag on its own next continuation (2026-08-12 fix: confirmed live, exiting Play Mode in the
        // Editor stops Unity from pumping UnitySynchronizationContext almost immediately after
        // OnApplicationQuit() returns, so that pending `await Task.Delay(16)` frequently never resumes at
        // all — the flag got set but SteamAPI.Shutdown() was never actually reached, leaving the Steam
        // overlay/rich presence stuck "active" indefinitely after Stop). Calling Shutdown() here is safe
        // even though RunSteamCallbacks()'s own loop might still be alive: Unity's message dispatch and
        // SynchronizationContext-marshaled continuations are never nested inside each other on the single
        // main thread, so this can't run WHILE a SteamAPI.RunCallbacks() call further up some other call
        // stack is still executing — the original native crash this design replaced (RunCallbacks() called
        // again immediately after a separate direct Shutdown() call) is prevented here by setting the stop
        // flag FIRST, so if the loop's continuation does still fire later it sees "already stopping" and
        // goes straight to its own (now-idempotent, see ShutdownSteamApiOnce) shutdown call instead of
        // looping back into RunCallbacks().
        public static void RequestSteamShutdown()
        {
            _stopSteamCallbackLoop = true;
            ShutdownSteamApiOnce();
        }

        // Actual SteamAPI.Shutdown() call site — idempotent (both RequestSteamShutdown() and
        // RunSteamCallbacks()'s own loop tail call this, see each's own doc comment for why both paths
        // exist) and a no-op if Init() never succeeded in the first place.
        private static void ShutdownSteamApiOnce()
        {
            if (!_steamInitialized || _steamShutdownCalled) return;
            _steamShutdownCalled = true;
            SteamAPI.Shutdown();
        }

        private Steamworks.CallResult<Steamworks.LobbyCreated_t> _LobbyCreated;
        private Steamworks.CallResult<Steamworks.LobbyEnter_t> _LobbyEnter;
        private Steamworks.CallResult<Steamworks.LobbyMatchList_t> _LobbyMatchList;

        private Steamworks.CSteamID _currentLobby = Steamworks.CSteamID.Nil;

#pragma warning disable IDE0052 // Remove unread private members
        private Steamworks.Callback<Steamworks.LobbyDataUpdate_t> _lobbyDataUpdateCallback;
        private Steamworks.Callback<Steamworks.AvatarImageLoaded_t> _avatarImageLoadedCallback;
        private Steamworks.Callback<Steamworks.LobbyChatUpdate_t> _lobbyChatUpdateCallback;
        private Steamworks.Callback<Steamworks.GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCallback;
#pragma warning restore IDE0052 // Remove unread private members

        public bool IsSteamClientAvailable
        {
            get
            {
                try
                {
                    Steamworks.InteropHelp.TestIfAvailableClient();
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public async Task<Lobby> CreateLobbyAsync(int maxPlayers, Dictionary<string, string> lobbyProperties = null)
        {
            if (!IsSteamClientAvailable)
                return default;

            _LobbyCreated ??= Steamworks.CallResult<Steamworks.LobbyCreated_t>.Create();

            var tcs = new TaskCompletionSource<bool>();
            Steamworks.CSteamID lobbyId = Steamworks.CSteamID.Nil;
            var lobbyName = $"{Steamworks.SteamFriends.GetPersonaName()}'s Lobby";

            var handle = Steamworks.SteamMatchmaking.CreateLobby((Steamworks.ELobbyType)lobbyType, maxPlayers);
            _LobbyCreated.Set(handle, (result, ioError) =>
            {
                if(!ioError && result.m_eResult == Steamworks.EResult.k_EResultOK)
                {
                    lobbyId = new Steamworks.CSteamID(result.m_ulSteamIDLobby);
                    // Set here, not after the await below resumes — this callback can still fire AFTER
                    // CreateLobbyAsync has already given up on a timeout and returned (Steam responding late
                    // doesn't stop the lobby from actually existing). The reactive chain that gets the
                    // player into the lobby (OnLobbyDataUpdate -> OnLobbyUpdated -> LobbyManager.OnRoomJoined)
                    // runs independently of this method's own return value and only needs _currentLobby to
                    // already match by the time the SetLobbyData calls below trigger a real LobbyDataUpdate_t
                    // — setting it late would silently drop a real, successfully-created lobby. Also closes
                    // the same-shaped race in OnLobbyDataUpdate's own _currentLobby comparison.
                    _currentLobby = lobbyId;
                    tcs.TrySetResult(true);
                    Steamworks.SteamMatchmaking.SetLobbyData(lobbyId, "Name", lobbyName);
                    Steamworks.SteamMatchmaking.SetLobbyData(lobbyId, "Started", "False");
                }
                else
                    tcs.TrySetResult(false);
            });

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(createLobbyTimeoutSeconds));
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completed == timeoutTask)
            {
                PurrLogger.LogWarning($"CreateLobbyAsync timed out after {createLobbyTimeoutSeconds}s — Steam never responded to CreateLobby.");
                OnError?.Invoke("Failed to create lobby: request timed out.");
                return new Lobby { IsValid = false };
            }
            if (!await tcs.Task)
            {
                OnError?.Invoke("Failed to create lobby: Steam rejected the request.");
                return new Lobby { IsValid = false };
            }

            // _currentLobby is already set above (inside the success callback) — not reassigning here again.

            if (lobbyProperties != null)
            {
                foreach (var prop in lobbyProperties)
                {
                    Steamworks.SteamMatchmaking.SetLobbyData(lobbyId, prop.Key, prop.Value);
                }
            }

            return LobbyFactory.Create(
                lobbyName,
                lobbyId.m_SteamID.ToString(),
                maxPlayers,
                true,
                GetLobbyUsers(lobbyId),
                lobbyProperties
            );
        }

        public Task<List<FriendUser>> GetFriendsAsync(LobbyManager.FriendFilter filter)
        {
            // Was `return default;` — for a Task<T> that's a genuinely null Task object, not just an empty
            // result, unlike every other IsSteamClientAvailable-guarded method in this file (which all
            // correctly return Task.FromResult(new List<...>())). FriendsList.Update() polls this every
            // frame and awaits the result directly, so hitting this path threw a live
            // NullReferenceException ("Object reference not set to an instance of an object") the moment
            // Steam wasn't available for even one frame (e.g. mid re-init after a real SteamAPI.Shutdown()).
            if (!IsSteamClientAvailable)
                return Task.FromResult(new List<FriendUser>());

            var friends = new List<FriendUser>();
            int friendCount = Steamworks.SteamFriends.GetFriendCount(Steamworks.EFriendFlags.k_EFriendFlagImmediate);

            for (int i = 0; i < friendCount; i++)
            {
                var steamID = Steamworks.SteamFriends.GetFriendByIndex(i, Steamworks.EFriendFlags.k_EFriendFlagImmediate);
                bool shouldAdd = filter switch
                {
                    LobbyManager.FriendFilter.InThisGame => Steamworks.SteamFriends.GetFriendGamePlayed(steamID, out Steamworks.FriendGameInfo_t gameInfo) &&
                                                            gameInfo.m_gameID.AppID() == Steamworks.SteamUtils.GetAppID(),
                    LobbyManager.FriendFilter.Online => Steamworks.SteamFriends.GetFriendPersonaState(steamID) == Steamworks.EPersonaState.k_EPersonaStateOnline,
                    LobbyManager.FriendFilter.All => true,
                    _ => false
                };

                if (shouldAdd)
                    friends.Add(CreateFriendUser(steamID));
            }

            return Task.FromResult(friends);
        }

        public Task<string> GetLobbyDataAsync(string key)
        {
            if (!IsSteamClientAvailable)
                return Task.FromResult(string.Empty);

            return Task.FromResult(Steamworks.SteamMatchmaking.GetLobbyData(_currentLobby, key));
        }

        public Task<List<LobbyUser>> GetLobbyMembersAsync()
        {
            if (!IsSteamClientAvailable)
                return Task.FromResult(new List<LobbyUser>());

            // Was passing the LOCAL PLAYER's own Steam ID here instead of the lobby's — GetLobbyUsers
            // expects a lobby handle, not a member handle, so this returned garbage/empty results for
            // every caller. Unused anywhere until LobbyManager's ready-state poll (see its own comment)
            // started relying on this as a live, callback-independent snapshot pull.
            return Task.FromResult(GetLobbyUsers(_currentLobby));
        }

        public Task<string> GetLocalUserIdAsync()
        {
            if (!IsSteamClientAvailable)
                return Task.FromResult(string.Empty);

            return Task.FromResult(Steamworks.SteamUser.GetSteamID().m_SteamID.ToString());
        }

        public Task InitializeAsync()
        {
            _avatarImageLoadedCallback = Steamworks.Callback<Steamworks.AvatarImageLoaded_t>.Create(OnAvatarImageLoaded);
            _lobbyDataUpdateCallback = Steamworks.Callback<Steamworks.LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
            _lobbyChatUpdateCallback = Steamworks.Callback<Steamworks.LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _gameLobbyJoinRequestedCallback = Steamworks.Callback<Steamworks.GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);

            if (handleSteamInit)
                HandleSteamInit();

            return Task.CompletedTask;
        }

        private void HandleSteamInit()
        {
            if (handleSteamInit)
            {
                if (!SteamAPI.Init())
                {
                    PurrLogger.LogError("SteamAPI initialization failed.");
                    OnError?.Invoke("SteamAPI initialization failed.");
                    return;
                }
                // Reset — these are static, so they can survive across separate Editor Play sessions when
                // Domain Reload is disabled. A fresh successful Init() here means a genuinely NEW Steam
                // session, so any leftover stop/shutdown state a PREVIOUS session left behind must not block
                // (or skip) this new session's own eventual shutdown.
                _steamInitialized = true;
                _steamShutdownCalled = false;
                _stopSteamCallbackLoop = false;
                RunSteamCallbacks();
            }
        }

        // Deliberately no stop condition tied to this component's own Lobby-scene-scoped lifecycle
        // (Shutdown()/OnDestroy() fire on EVERY Lobby scene unload, including the completely normal
        // transition into a match) — this loop is the ONLY thing in the whole project that pumps Steam's
        // callback dispatcher at all (confirmed: nothing under Assets/PurrNet calls SteamAPI.RunCallbacks()
        // itself), so it needs to keep running for the game's entire process lifetime, not just while the
        // Lobby scene happens to be loaded. Stops via RequestSteamShutdown() setting _stopSteamCallbackLoop —
        // that same call already handles the real ShutdownSteamApiOnce() call synchronously and directly
        // (see its own doc comment for why this loop's own tail call below can no longer be relied on alone),
        // this loop's tail is just a backstop for the case where this loop's own continuation DOES get a
        // chance to run (a real build's normal quit sequence, not the Editor-stop case that motivated moving
        // the primary call site) — ShutdownSteamApiOnce()'s own guard makes it a safe no-op either way.
        private async void RunSteamCallbacks()
        {
            while (!_stopSteamCallbackLoop)
            {
                try
                {
                    SteamAPI.RunCallbacks();
                }
                catch (InvalidOperationException)
                {
                    // Belt and suspenders — shouldn't be reachable now that shutdown only ever happens after
                    // this loop has already committed to stopping, but keeping this in case something else
                    // ever calls SteamAPI.Shutdown() directly again in the future. Same "ignore, we're
                    // already exiting" reasoning SteamServer.Stop()/SteamClient.Disconnect() already use for
                    // their own teardown-time Steam calls.
                    return;
                }
                await Task.Delay(16);
            }
            ShutdownSteamApiOnce();
        }

        public Task InviteFriendAsync(FriendUser user)
        {
            if (IsSteamClientAvailable && !string.IsNullOrEmpty(user.Id) && ulong.TryParse(user.Id, out var id))
            {
                var steamID = new Steamworks.CSteamID(id);
                Steamworks.SteamMatchmaking.InviteUserToLobby(_currentLobby, steamID);
            }

            return Task.FromResult(Task.CompletedTask);
        }

        public async Task<Lobby> JoinLobbyAsync(string lobbyId)
        {
            if (!IsSteamClientAvailable || string.IsNullOrEmpty(lobbyId))
                return default;

            _LobbyEnter ??= Steamworks.CallResult<Steamworks.LobbyEnter_t>.Create();

            var tcs = new TaskCompletionSource<bool>();
            var cLobbyId = new Steamworks.CSteamID(ulong.Parse(lobbyId));
            var handle = Steamworks.SteamMatchmaking.JoinLobby(cLobbyId);

            _LobbyEnter.Set(handle, (result, ioError) =>
            {
                if (result.m_EChatRoomEnterResponse == (uint)Steamworks.EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                {
                    _currentLobby = new Steamworks.CSteamID(result.m_ulSteamIDLobby);
                    tcs.TrySetResult(true);
                }
                else
                {
                    tcs.TrySetResult(false);
                }
            });

            if (!await tcs.Task)
            {
                OnLobbyJoinFailed?.Invoke($"Failed to join lobby {lobbyId}.");
                return new Lobby { IsValid = false };
            }

            var lobby = LobbyFactory.Create(
                Steamworks.SteamMatchmaking.GetLobbyData(_currentLobby, "Name"),
                lobbyId,
                Steamworks.SteamMatchmaking.GetLobbyMemberLimit(_currentLobby),
                false,
                GetLobbyUsers(cLobbyId),
                GetLobbyProperties(_currentLobby)
            );

            OnLobbyUpdated?.Invoke(lobby);
            return lobby;
        }

        public Task LeaveLobbyAsync()
        {
            if (!IsSteamClientAvailable || _currentLobby == Steamworks.CSteamID.Nil) 
                return Task.CompletedTask;

            Steamworks.SteamMatchmaking.LeaveLobby(_currentLobby);
            _currentLobby = default;
            OnLobbyLeft?.Invoke();
            return Task.CompletedTask;
        }

        public Task LeaveLobbyAsync(string lobbyId)
        {
            if (IsSteamClientAvailable && !string.IsNullOrEmpty(lobbyId) && ulong.TryParse(lobbyId, out var id))
            {
                var cLobbyId = new Steamworks.CSteamID(ulong.Parse(lobbyId));
                Steamworks.SteamMatchmaking.LeaveLobby(cLobbyId);
            }

            return Task.CompletedTask;
        }

        public async Task<List<Lobby>> SearchLobbiesAsync(int maxRoomsToFind = 10, Dictionary<string, string> filters = null)
        {
            if (!IsSteamClientAvailable)
                return new List<Lobby>();

            var tcs = new TaskCompletionSource<List<Lobby>>();
            var results = new List<Lobby>();

            if (filters != null)
            {
                foreach (var filter in filters)
                {
                    Steamworks.SteamMatchmaking.AddRequestLobbyListStringFilter(filter.Key, filter.Value, Steamworks.ELobbyComparison.k_ELobbyComparisonEqual);
                }
            }

            Steamworks.SteamMatchmaking.AddRequestLobbyListStringFilter("Started", "False", Steamworks.ELobbyComparison.k_ELobbyComparisonEqual);
            Steamworks.SteamMatchmaking.AddRequestLobbyListResultCountFilter(maxLobbiesToFind);

            _LobbyMatchList ??= Steamworks.CallResult<Steamworks.LobbyMatchList_t>.Create();
            _LobbyMatchList.Set(Steamworks.SteamMatchmaking.RequestLobbyList(), (result, ioError) =>
            {
                int totalLobbies = (int)result.m_nLobbiesMatching;

                for (int i = 0; i < totalLobbies; i++)
                {
                    var lobbyId = Steamworks.SteamMatchmaking.GetLobbyByIndex(i);
                    var lobbyProperties = GetLobbyProperties(lobbyId);
                    int maxPlayers = Steamworks.SteamMatchmaking.GetLobbyMemberLimit(lobbyId);

                    results.Add(new Lobby
                    {
                        Name = Steamworks.SteamMatchmaking.GetLobbyData(lobbyId, "Name"),
                        IsValid = true,
                        LobbyId = lobbyId.m_SteamID.ToString(),
                        MaxPlayers = maxPlayers,
                        Properties = lobbyProperties,
                        Members = GetLobbyUsers(lobbyId)
                    });
                }

                tcs.TrySetResult(results);
            });

            return await tcs.Task;
        }

        public void RestoreCurrentLobby(string lobbyId)
        {
#if STEAMWORKS_NET_PACKAGE && !DISABLESTEAMWORKS
            if (!string.IsNullOrEmpty(lobbyId) && ulong.TryParse(lobbyId, out ulong id))
                _currentLobby = new Steamworks.CSteamID(id);
#endif
        }

        public Task SetIsReadyAsync(string userId, bool isReady)
        {
            //You can only set the ready state for your own user
            if (IsSteamClientAvailable && !string.IsNullOrEmpty(userId) && ulong.TryParse(userId, out var id)
                && Steamworks.SteamUser.GetSteamID().m_SteamID == id)
            {
                Steamworks.SteamMatchmaking.SetLobbyMemberData(_currentLobby, "IsReady", isReady.ToString());
                // Nudges Steam into firing a LobbyDataUpdate_t callback sooner than it reliably would from
                // the member-data write alone — a responsiveness optimization ONLY. These are two
                // independent, unordered writes to Steam's backend with no guaranteed relative delivery
                // order to other peers, so a peer's callback COULD read "IsReady" before it's actually
                // propagated. That used to be load-bearing (the ready-check only ever ran reactively inside
                // that callback) — it no longer is: LobbyManager's periodic ready-state poll
                // (GetLobbyMembersAsync, a fresh pull straight from each client's own local Steam cache, not
                // dependent on this or any callback having fired at all) is what actually guarantees
                // eventual correctness now, within one poll interval regardless of callback timing.
                Steamworks.SteamMatchmaking.SetLobbyData(_currentLobby, "UpdateTrigger", DateTime.UtcNow.Ticks.ToString());
            }

            return Task.FromResult(Task.CompletedTask);
        }

        public Task SetLobbyDataAsync(string key, string value)
        {
            if (IsSteamClientAvailable)
                Steamworks.SteamMatchmaking.SetLobbyData(_currentLobby, key, value);

            return Task.FromResult(Task.CompletedTask);
        }

        public Task SetLobbyStartedAsync()
        {
            if (IsSteamClientAvailable)
            {
                Steamworks.SteamMatchmaking.SetLobbyGameServer(_currentLobby, 0, 0, Steamworks.SteamUser.GetSteamID());
                Steamworks.SteamMatchmaking.SetLobbyData(_currentLobby, "Started", "True");
            }

            return Task.FromResult(Task.CompletedTask);
        }

        public void SetLobbyStarted(Steamworks.CSteamID serverId)
        {
            if (IsSteamClientAvailable)
            {
                Steamworks.SteamMatchmaking.SetLobbyGameServer(_currentLobby, 0, 0, serverId);
                Steamworks.SteamMatchmaking.SetLobbyData(_currentLobby, "Started", "True");
            }
        }

        public void SetLobbyStarted(string address, short port)
        {
            if (IsSteamClientAvailable)
            {
                var ipAddress = System.Net.IPAddress.Parse(address);
                var ipBytes = ipAddress.GetAddressBytes();
                var ip = (uint)ipBytes[0] << 24;
                ip += (uint)ipBytes[1] << 16;
                ip += (uint)ipBytes[2] << 8;
                ip += (uint)ipBytes[3];
                Steamworks.SteamMatchmaking.SetLobbyGameServer(_currentLobby, ip, (ushort)port, Steamworks.CSteamID.Nil);
                Steamworks.SteamMatchmaking.SetLobbyData(_currentLobby, "Started", "True");
            }
        }

        public void SetLobbyStarted(string address, short port, Steamworks.CSteamID serverId)
        {
            if (IsSteamClientAvailable)
            {
                var ipAddress = System.Net.IPAddress.Parse(address);
                var ipBytes = ipAddress.GetAddressBytes();
                var ip = (uint)ipBytes[0] << 24;
                ip += (uint)ipBytes[1] << 16;
                ip += (uint)ipBytes[2] << 8;
                ip += (uint)ipBytes[3];
                Steamworks.SteamMatchmaking.SetLobbyGameServer(_currentLobby, ip, (ushort)port, Steamworks.CSteamID.Nil);
                Steamworks.SteamMatchmaking.SetLobbyData(_currentLobby, "Started", "True");
            }
        }

        public void Shutdown()
        {
            // Steamworks.Callback<T> registers itself into a static, process-wide dispatch table
            // (CallbackDispatcher.m_registeredCallbacks) the moment it's created — that registration is
            // completely independent of this MonoBehaviour's Unity lifecycle, so Destroy()ing this GameObject
            // (e.g. a scene-unload on match start) does NOT stop these from firing. Only Dispose() removes
            // the entry. Without this, OnAvatarImageLoaded could fire at any arbitrary point for the rest of
            // the process (including mid-match), since Steam avatar fetches queued while in the lobby can
            // resolve whenever Steam gets around to it.
            _avatarImageLoadedCallback?.Dispose();
            _avatarImageLoadedCallback = null;

            _lobbyDataUpdateCallback?.Dispose();
            _lobbyDataUpdateCallback = null;

            _lobbyChatUpdateCallback?.Dispose();
            _lobbyChatUpdateCallback = null;

            _gameLobbyJoinRequestedCallback?.Dispose();
            _gameLobbyJoinRequestedCallback = null;
        }

        // Belt and suspenders: nothing in this project currently calls Shutdown() explicitly at the
        // lobby-to-gameplay transition, so this is what actually triggers cleanup today — Unity calls
        // OnDestroy() automatically when the Lobby scene unloads (LoadSceneMode.Single). Safe to call
        // Shutdown() twice (explicit call now, this on eventual destroy) since Callback<T>.Dispose() is
        // itself idempotent.
        private void OnDestroy()
        {
            Shutdown();
        }

        private List<LobbyUser> GetLobbyUsers(Steamworks.CSteamID lobbyId)
        {
            var users = new List<LobbyUser>();
            int memberCount = Steamworks.SteamMatchmaking.GetNumLobbyMembers(lobbyId);

            for (int i = 0; i < memberCount; i++)
            {
                var steamId = Steamworks.SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i);
                users.Add(CreateLobbyUser(steamId, lobbyId));
            }

            return users;
        }

        private LobbyUser CreateLobbyUser(Steamworks.CSteamID steamId, Steamworks.CSteamID lobbyId)
        {
            _avatarImageLoadedCallback ??= Steamworks.Callback<Steamworks.AvatarImageLoaded_t>.Create(OnAvatarImageLoaded);

            var displayName = Steamworks.SteamFriends.GetFriendPersonaName(steamId);
            var isReadyString = Steamworks.SteamMatchmaking.GetLobbyMemberData(lobbyId, steamId, "IsReady");
            var isReady = !string.IsNullOrEmpty(isReadyString) && isReadyString == "True";

            var avatarHandle = Steamworks.SteamFriends.GetLargeFriendAvatar(steamId);
            Texture2D avatar = null;

            if (avatarHandle != -1 && Steamworks.SteamUtils.GetImageSize(avatarHandle, out uint width, out uint height))
            {
                byte[] imageBuffer = new byte[width * height * 4];
                if (Steamworks.SteamUtils.GetImageRGBA(avatarHandle, imageBuffer, imageBuffer.Length))
                {
                    FlipImageBufferVertically(imageBuffer, (int)width, (int)height);
                    avatar = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false);
                    avatar.LoadRawTextureData(imageBuffer);
                    avatar.Apply();
                }
            }

            return new LobbyUser
            {
                Id = steamId.m_SteamID.ToString(),
                DisplayName = displayName,
                IsReady = isReady,
                Avatar = avatar
            };
        }

        // Flips a raw RGBA32 buffer in place, row by row, before it's ever uploaded to a Texture2D — avoids
        // Texture2D.GetPixels()/SetPixels(), which allocate a full Color[] (16 bytes/pixel) and do an extra
        // full texture readback+reupload. The only allocation here is one row-sized scratch buffer, reused
        // across the whole flip.
        private static void FlipImageBufferVertically(byte[] buffer, int width, int height)
        {
            int stride = width * 4;
            var rowBuffer = new byte[stride];

            for (int y = 0; y < height / 2; y++)
            {
                int topOffset = y * stride;
                int bottomOffset = (height - 1 - y) * stride;

                Array.Copy(buffer, topOffset, rowBuffer, 0, stride);
                Array.Copy(buffer, bottomOffset, buffer, topOffset, stride);
                Array.Copy(rowBuffer, 0, buffer, bottomOffset, stride);
            }
        }

        private FriendUser CreateFriendUser(Steamworks.CSteamID steamId)
        {
            var displayName = Steamworks.SteamFriends.GetFriendPersonaName(steamId);

            var avatarHandle = Steamworks.SteamFriends.GetLargeFriendAvatar(steamId);
            Texture2D avatar = null;

            if (avatarHandle != -1 && Steamworks.SteamUtils.GetImageSize(avatarHandle, out uint width, out uint height))
            {
                byte[] imageBuffer = new byte[width * height * 4];
                if (Steamworks.SteamUtils.GetImageRGBA(avatarHandle, imageBuffer, imageBuffer.Length))
                {
                    FlipImageBufferVertically(imageBuffer, (int)width, (int)height);
                    avatar = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false);
                    avatar.LoadRawTextureData(imageBuffer);
                    avatar.Apply();
                }
            }

            return new FriendUser()
            {
                Id = steamId.m_SteamID.ToString(),
                DisplayName = displayName,
                Avatar = avatar
            };
        }

        private void OnAvatarImageLoaded(Steamworks.AvatarImageLoaded_t callback)
        {
            var steamId = callback.m_steamID;
            if (callback.m_iImage == -1)
            {
                PurrLogger.LogWarning($"Failed to load avatar for user {steamId}");
                return;
            }

            if (Steamworks.SteamUtils.GetImageSize(callback.m_iImage, out uint width, out uint height))
            {
                byte[] imageBuffer = new byte[width * height * 4];
                if (Steamworks.SteamUtils.GetImageRGBA(callback.m_iImage, imageBuffer, imageBuffer.Length))
                {
                    FlipImageBufferVertically(imageBuffer, (int)width, (int)height);
                    Texture2D avatar = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false);
                    avatar.LoadRawTextureData(imageBuffer);
                    avatar.Apply();

                    UpdateUserAvatar(steamId, avatar);
                }
            }
        }

        private void UpdateUserAvatar(Steamworks.CSteamID steamId, Texture2D avatar)
        {
            if (!_currentLobby.IsValid())
                return;
            var updatedMembers = GetLobbyUsers(_currentLobby);
            if (updatedMembers == null || updatedMembers.Count <= 0)
                return;

            for (int i = 0; i < updatedMembers.Count; i++)
            {
                if (updatedMembers[i].Id == steamId.m_SteamID.ToString())
                {
                    var updatedUser = updatedMembers[i];
                    updatedUser.Avatar = avatar;
                    updatedMembers[i] = updatedUser;
                    break;
                }
            }

            var updatedLobby = new Lobby
            {
                Name = Steamworks.SteamMatchmaking.GetLobbyData(_currentLobby, "Name"),
                IsValid = true,
                LobbyId = _currentLobby.m_SteamID.ToString(),
                MaxPlayers = Steamworks.SteamMatchmaking.GetLobbyMemberLimit(_currentLobby),
                Properties = new Dictionary<string, string>(), // Use existing properties if needed
                Members = updatedMembers
            };

            OnLobbyUpdated?.Invoke(updatedLobby);
        }

        private void OnLobbyDataUpdate(Steamworks.LobbyDataUpdate_t callback)
        {
            // Guards against acting on a data update for a lobby we're not (or no longer) tracking as
            // _currentLobby — e.g. a stale callback for a lobby we've since left. CreateLobbyAsync sets
            // _currentLobby synchronously inside its own success callback (see that method's own comment on
            // why), so a real just-created lobby's first SetLobbyData-triggered update here always matches.
            if (_currentLobby.m_SteamID != callback.m_ulSteamIDLobby)
                return;

            var ownerId = Steamworks.SteamMatchmaking.GetLobbyOwner(_currentLobby).m_SteamID.ToString();
            var localId = Steamworks.SteamUser.GetSteamID().m_SteamID.ToString();
            var isOwner = localId == ownerId;

            var updatedLobbyUsers = GetLobbyUsers(_currentLobby);
            var updatedLobby = LobbyFactory.Create(
                Steamworks.SteamMatchmaking.GetLobbyData(_currentLobby, "Name"),
                _currentLobby.m_SteamID.ToString(),
                Steamworks.SteamMatchmaking.GetLobbyMemberLimit(_currentLobby),
                isOwner,
                updatedLobbyUsers,
                GetLobbyProperties(_currentLobby)
            );

            OnLobbyUpdated?.Invoke(updatedLobby);
        }

        private Dictionary<string, string> GetLobbyProperties(Steamworks.CSteamID lobbyId)
        {
            var properties = new Dictionary<string, string>();
            int propertyCount = Steamworks.SteamMatchmaking.GetLobbyDataCount(lobbyId);

            for (int i = 0; i < propertyCount; i++)
            {
                string key = string.Empty;
                string value = string.Empty;
                int keySize = 256;
                int valueSize = 256;

                bool success = Steamworks.SteamMatchmaking.GetLobbyDataByIndex(
                    lobbyId,
                    i,
                    out key,
                    keySize,
                    out value,
                    valueSize
                );

                if (success)
                {
                    key = key.TrimEnd('\0');
                    value = value.TrimEnd('\0');
                    properties[key] = value;
                }
            }

            return properties;
        }

        private void OnLobbyChatUpdate(Steamworks.LobbyChatUpdate_t callback)
        {
            if (_currentLobby.m_SteamID != callback.m_ulSteamIDLobby)
                return;

            var stateChange = (Steamworks.EChatMemberStateChange)callback.m_rgfChatMemberStateChange;

            if (stateChange.HasFlag(Steamworks.EChatMemberStateChange.k_EChatMemberStateChangeEntered))
            {
                //PurrLogger.Log($"User {callback.m_ulSteamIDUserChanged} joined the lobby.");
            }

            if (stateChange.HasFlag(Steamworks.EChatMemberStateChange.k_EChatMemberStateChangeLeft) ||
                stateChange.HasFlag(Steamworks.EChatMemberStateChange.k_EChatMemberStateChangeDisconnected))
            {
                //PurrLogger.Log($"User {callback.m_ulSteamIDUserChanged} left the lobby.");
            }

            var ownerId = Steamworks.SteamMatchmaking.GetLobbyOwner(_currentLobby).m_SteamID.ToString();
            var localId = Steamworks.SteamUser.GetSteamID().m_SteamID.ToString();
            var isOwner = localId == ownerId;

            var data = Steamworks.SteamMatchmaking.GetLobbyData(_currentLobby, "Name");
            var properties = GetLobbyProperties(_currentLobby);
            var updatedLobbyUsers = GetLobbyUsers(_currentLobby);

            var updatedLobby = LobbyFactory.Create(
                data,
                _currentLobby.m_SteamID.ToString(),
                Steamworks.SteamMatchmaking.GetLobbyMemberLimit(_currentLobby),
                isOwner,
                updatedLobbyUsers,
                properties
            );

            OnLobbyUpdated?.Invoke(updatedLobby);
        }

        private void OnGameLobbyJoinRequested(Steamworks.GameLobbyJoinRequested_t callback)
        {
            var lobbyId = callback.m_steamIDLobby;
            //PurrLogger.Log($"Invite accepted. Joining lobby {lobbyId.m_SteamID}");

            _ = JoinLobbyAsync(lobbyId.m_SteamID.ToString());
        }

        public Task SetAllReadyAsync()
        {
            return Task.FromResult(Task.CompletedTask);
        }
#endif
    }
}
