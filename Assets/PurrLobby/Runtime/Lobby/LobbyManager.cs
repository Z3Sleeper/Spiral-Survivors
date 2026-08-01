using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PurrLobby.Providers;
using PurrNet;
using PurrNet.Logging;
using UnityEngine;
using UnityEngine.Events;

namespace PurrLobby
{
    public class LobbyManager : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour currentProvider;
        private ILobbyProvider _currentProvider;

        private readonly Queue<Action> _delayedActions = new Queue<Action>();
        private int _taskLock;

        public CreateRoomArgs createRoomArgs = new();
        public SerializableDictionary<string, string> searchRoomArgs = new();

        // Events exposed by the manager
        public UnityEvent<Lobby> OnRoomJoined = new UnityEvent<Lobby>();
        public UnityEvent<string> OnRoomJoinFailed = new UnityEvent<string>();
        public UnityEvent OnRoomLeft = new UnityEvent();
        public UnityEvent<Lobby> OnRoomUpdated = new UnityEvent<Lobby>();
        public UnityEvent<List<LobbyUser>> OnPlayerListUpdated = new UnityEvent<List<LobbyUser>>();
        public UnityEvent<List<Lobby>> OnRoomSearchResults = new UnityEvent<List<Lobby>>();
        public UnityEvent<List<FriendUser>> OnFriendListPulled = new UnityEvent<List<FriendUser>>();
        public UnityEvent OnAllReady = new UnityEvent();
        public UnityEvent<string> OnError = new UnityEvent<string>();

        public UnityEvent onInitialized = new UnityEvent();
        public UnityEvent onShutdown = new UnityEvent();

        public ILobbyProvider CurrentProvider => currentProvider as ILobbyProvider;

        private Lobby _currentLobby
        {
            get
            {
                if (!_lobbyDataHolder)
                    return default;
                return _lobbyDataHolder.CurrentLobby;
            }
            set
            {
                _lobbyDataHolder.SetCurrentLobby(value);
            }
        }

        private Lobby _lastKnownState;
        public Lobby CurrentLobby => _currentLobby;
        private LobbyDataHolder _lobbyDataHolder;

        private bool IsStarting = false;

        // Guaranteed backstop for the ready-check — see PollReadyState's own doc comment for why this
        // exists (Steam's, or any provider's, lobby-data callbacks aren't ordering-guaranteed across
        // peers). The reactive check inside SubscribeToProviderEvents' OnLobbyUpdated handler is kept too,
        // purely as a responsiveness optimization for the common case — this poll is what makes the
        // ready-check actually CORRECT, not just usually-fast.
        [SerializeField] private float readyPollInterval = 1f;
        private float _readyPollTimer;

        // Stored delegate references — SubscribeToProviderEvents/UnsubscribeFromProviderEvents previously
        // used brand-new inline lambdas on each call, which C# delegate removal treats as unrelated
        // instances (not equal-by-reference to whatever was originally added), so the "unsubscribe" calls
        // were silent no-ops. Currently latent (SetProvider only ever runs once, at Awake), but a real bug
        // waiting for the first runtime provider switch — storing the actual subscribed delegate fixes it.
        private UnityAction<string> _onLobbyJoinFailedHandler;
        private UnityAction _onLobbyLeftHandler;
        private UnityAction<Lobby> _onLobbyUpdatedHandler;
        private UnityAction<Lobby> _onLobbyJoinedRelayHandler;
        private UnityAction<List<LobbyUser>> _onLobbyPlayerListUpdatedHandler;
        private UnityAction<string> _onErrorHandler;

        private void Awake()
        {
            _lastKnownState = new Lobby { IsValid = false };

            if (CurrentProvider != null)
                SetProvider(CurrentProvider);
            else
                PurrLogger.LogWarning("No lobby provider assigned to LobbyManager.");

            SetupDataHolder();
        }

        private void SetupDataHolder()
        {
            _lobbyDataHolder = FindFirstObjectByType<LobbyDataHolder>();
            if (!_lobbyDataHolder)
            {
                var newObject = new GameObject("LobbyDataHolder");
                _lobbyDataHolder = newObject.AddComponent<LobbyDataHolder>();
                return;
            }

            if (_lobbyDataHolder.CurrentLobby.IsValid && !LobbyFlags.IsReturningFromGame)
            {
                LeaveLobby(_lobbyDataHolder.CurrentLobby.LobbyId);
                _lobbyDataHolder.SetCurrentLobby(default);
            }
            LobbyFlags.IsReturningFromGame = false;

            // Restore provider's internal lobby ID — it resets to nil on scene reload
            if (_lobbyDataHolder.CurrentLobby.IsValid && _currentProvider is SteamLobbyProvider steamProvider)
                steamProvider.RestoreCurrentLobby(_lobbyDataHolder.CurrentLobby.LobbyId);
        }

        private void Update()
        {
            while (_delayedActions.Count > 0)
            {
                _delayedActions.Dequeue()?.Invoke();
            }

            _readyPollTimer -= Time.deltaTime;
            if (_readyPollTimer > 0f) return;
            _readyPollTimer = readyPollInterval;

            PollReadyState();
        }

        // Guaranteed replacement for "trust the next OnLobbyUpdated callback to have a fully consistent
        // snapshot" — a lobby provider's ready-flag writes (e.g. SteamLobbyProvider.SetIsReadyAsync writing
        // "IsReady" and a separate "UpdateTrigger" nudge as two independent, unordered backend writes) have
        // no guaranteed delivery order to other peers, so a single reactive callback can observe a stale
        // "not ready" and never get corrected if no further lobby-data event happens to fire. This instead
        // actively pulls a FRESH member list straight from the provider's own local cache
        // (GetLobbyMembersAsync — not dependent on any callback having fired at all, ever) once every
        // readyPollInterval seconds, so "everyone's actually ready" is detected within a bounded worst-case
        // delay no matter what order/whether callbacks arrived. This is what makes the ready-check
        // GUARANTEED rather than best-effort — the reactive OnLobbyUpdated check is still there too, purely
        // so the common case still feels instant instead of interval-bounded.
        private void PollReadyState()
        {
            if (IsStarting || _currentProvider == null || !_currentLobby.IsValid) return;

            RunTask(async () =>
            {
                var members = await _currentProvider.GetLobbyMembersAsync();
                if (members == null || members.Count == 0) return;
                // Re-check after the await gap — IsStarting/the lobby itself could have changed while this
                // was in flight (e.g. we left the lobby, or another path already triggered the start).
                if (IsStarting || !_currentLobby.IsValid) return;

                if (members.TrueForAll(x => x.IsReady))
                {
                    IsStarting = true;
                    // Deferred via InvokeDelayed, not called directly — CallOnAllReady() awaits
                    // WaitForAllTasksAsync(), which spins until _taskLock reaches 0. This lambda is still
                    // running INSIDE a RunTask() (which is what's holding _taskLock elevated right now), so
                    // calling it synchronously here would deadlock-spin forever. Deferring to a later
                    // Update() dequeue runs it after this RunTask's own finally block has already
                    // decremented _taskLock, same as every other CallOnAllReady() call site in this class.
                    InvokeDelayed(CallOnAllReady);
                }
            });
        }

        private void InvokeDelayed(Action action)
        {
            try
            {
                _delayedActions.Enqueue(action);
            }
            catch (Exception ex)
            {
                PurrLogger.LogError($"Error in InvokeDelayed: {ex.Message}");
            }
        }

        /// <summary>
        /// Set or switch the current provider
        /// </summary>
        /// <param name="provider"></param>
        public void SetProvider(ILobbyProvider provider)
        {
            if (_currentProvider != null)
            {
                UnsubscribeFromProviderEvents();
                _currentProvider.Shutdown();
            }

            _currentProvider = provider;

            if (_currentProvider != null)
            {
                SubscribeToProviderEvents();
                RunTask(async () =>
                {
                    await _currentProvider.InitializeAsync();
                    InvokeDelayed(() => onInitialized?.Invoke());
                });
            }
        }

        // Subscribe to provider events
        private void SubscribeToProviderEvents()
        {
            _onLobbyJoinFailedHandler = message => InvokeDelayed(() => OnRoomJoinFailed.Invoke(message));
            _currentProvider.OnLobbyJoinFailed += _onLobbyJoinFailedHandler;

            _onLobbyLeftHandler = () => InvokeDelayed(() =>
            {
                _currentLobby = default;
                // Was never reset here — once a ready-check successfully passed (CallOnAllReady's success
                // path doesn't reset it either, by design, since that lobby is now genuinely starting), a
                // player who abandons that connect attempt and leaves would carry IsStarting=true into any
                // later lobby joined in the same scene session, permanently blocking the !IsStarting guard
                // below from ever firing OnAllReady again — the Ready button would silently stop working.
                IsStarting = false;
                OnRoomLeft?.Invoke();
            });
            _currentProvider.OnLobbyLeft += _onLobbyLeftHandler;

            _onLobbyUpdatedHandler = room => InvokeDelayed(() =>
            {
                if(!_lastKnownState.HasChanged(room) || room.Members.Count <= 0 || !room.IsValid) return;

                _lastKnownState = room;
                _currentLobby = room;
                OnRoomUpdated?.Invoke(room);

                if (!IsStarting && room.Members.TrueForAll(x => x.IsReady))
                {
                    IsStarting = true; //Prevent calling ready again if lobby is updated after all ready
                    CallOnAllReady();
                }
            });
            _currentProvider.OnLobbyUpdated += _onLobbyUpdatedHandler;

            _onLobbyPlayerListUpdatedHandler = players => InvokeDelayed(() => OnPlayerListUpdated.Invoke(players));
            _currentProvider.OnLobbyPlayerListUpdated += _onLobbyPlayerListUpdatedHandler;

            _onErrorHandler = error => InvokeDelayed(() => OnError.Invoke(error));
            _currentProvider.OnError += _onErrorHandler;

            _onLobbyJoinedRelayHandler = room =>
            {
                if (room.IsValid)
                {
                    InvokeDelayed(() => OnRoomJoined?.Invoke(room));
                }
            };
            _currentProvider.OnLobbyUpdated += _onLobbyJoinedRelayHandler;
        }

        // Unsubscribe from provider events — must use the SAME delegate instances stored above; a
        // syntactically-identical new lambda is a different instance and `-=` against it is a silent no-op.
        private void UnsubscribeFromProviderEvents()
        {
            if (_onLobbyJoinFailedHandler != null) _currentProvider.OnLobbyJoinFailed -= _onLobbyJoinFailedHandler;
            if (_onLobbyLeftHandler != null) _currentProvider.OnLobbyLeft -= _onLobbyLeftHandler;
            if (_onLobbyUpdatedHandler != null) _currentProvider.OnLobbyUpdated -= _onLobbyUpdatedHandler;
            if (_onLobbyPlayerListUpdatedHandler != null) _currentProvider.OnLobbyPlayerListUpdated -= _onLobbyPlayerListUpdatedHandler;
            if (_onErrorHandler != null) _currentProvider.OnError -= _onErrorHandler;
            if (_onLobbyJoinedRelayHandler != null) _currentProvider.OnLobbyUpdated -= _onLobbyJoinedRelayHandler;
        }

        /// <summary>
        /// Shuts down and clears the current provider
        /// </summary>
        public void Shutdown()
        {
            EnsureProviderSet();
            _currentProvider.Shutdown();
            onShutdown?.Invoke();
        }

        /// <summary>
        /// Prompts the provider to pull friends from the platform's friend list.
        /// </summary>
        public void PullFriends(FriendFilter filter)
        {
            RunTask(async () =>
            {
                EnsureProviderSet();
                var friends = await _currentProvider.GetFriendsAsync(filter);
                OnFriendListPulled?.Invoke(friends);
            });
        }

        /// <summary>
        /// Invite the given user to the current lobby.
        /// </summary>
        /// <param name="user"></param>
        public void InviteFriend(FriendUser user)
        {
            RunTask(async () =>
            {
                EnsureProviderSet();
                await _currentProvider.InviteFriendAsync(user);
            });
        }

        /// <summary>
        /// Creates a room using the inspector CreateRoomArgs values.
        /// </summary>
        public void CreateRoom()
        {
            CreateRoom(createRoomArgs.maxPlayers, createRoomArgs.roomProperties.ToDictionary());
        }
        
        /// <summary>
        /// Creates a room using custom settings set through code
        /// </summary>
        public void CreateRoom(int maxPlayers, Dictionary<string, string> roomProperties = null)
        {
            RunTask(async () =>
            {
                EnsureProviderSet();
                var room = await _currentProvider.CreateLobbyAsync(maxPlayers, roomProperties);
                // A failed/timed-out CreateLobbyAsync returns an invalid Lobby (see its own OnError-then-return
                // path) — must not fire OnRoomUpdated with it, same guard the reactive _onLobbyUpdatedHandler
                // above already applies (!room.IsValid) — listeners assume a successful room's data is populated.
                if (!room.IsValid) return;
                _currentLobby = room;
                OnRoomUpdated?.Invoke(room);
            });
        }

        /// <summary>
        /// Leave the lobby
        /// </summary>
        public void LeaveLobby()
        {
            RunTask(async () =>
            {
                EnsureProviderSet();
                await _currentProvider.LeaveLobbyAsync();
                OnRoomLeft?.Invoke();
            });
        }

        /// <summary>
        /// Leave a specific lobby
        /// </summary>
        public void LeaveLobby(string lobbyId)
        {
            RunTask(async () =>
            {
                EnsureProviderSet();
                await _currentProvider.LeaveLobbyAsync(lobbyId);
                OnRoomLeft?.Invoke();
            });
        }

        /// <summary>
        /// Join the lobby with the given ID
        /// </summary>
        /// <param name="roomId">ID of the lobby to join</param>
        public void JoinLobby(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                OnRoomJoinFailed?.Invoke("Null or empty room ID.");
                return;
            }
            
            RunTask(async () =>
            {
                EnsureProviderSet();
                var room = await _currentProvider.JoinLobbyAsync(roomId);
                if (room.IsValid)
                {
                    OnRoomJoined?.Invoke(room);
                }
                else
                {
                    OnRoomJoinFailed?.Invoke($"Failed to join room {roomId}");
                }
            });
        }

        /// <summary>
        /// Prompts the provider to search lobbies with given filters
        /// </summary>
        /// <param name="maxRoomsToFind">Max amount of rooms to find</param>
        /// <param name="filters">Filters to use for search - only works if the provider supports it</param>
        public void SearchLobbies(int maxRoomsToFind = 10, Dictionary<string, string> filters = null)
        {
            if(filters == null)
                filters = searchRoomArgs.ToDictionary();
            
            RunTask(async () =>
            {
                EnsureProviderSet();
                var rooms = await _currentProvider.SearchLobbiesAsync(maxRoomsToFind, filters);
                OnRoomSearchResults?.Invoke(rooms);
            });
        }
        
        /// <summary>
        /// Set's the given User to Ready
        /// </summary>
        /// <param name="userId">User ID of player</param>
        /// <param name="isReady">Ready state to set</param>
        public void SetIsReady(string userId, bool isReady)
        {
            RunTask(async () =>
            {
                EnsureProviderSet();
                await _currentProvider.SetIsReadyAsync(userId, isReady);
            });
        }
        
        /// <summary>
        /// Sets meta data on the current lobby we're in
        /// </summary>
        /// <param name="key">Key/Identifier of the meta data</param>
        /// <param name="value">The value of the meta data to be stored</param>
        public void SetLobbyData(string key, string value)
        {
            RunTask(async () =>
            {
                EnsureProviderSet();
                await _currentProvider.SetLobbyDataAsync(key, value);
            });
        }
        
        /// <summary>
        /// Gets meta data from the current lobby we're in
        /// </summary>
        /// <param name="key">Key/Identifier of the meta data we want</param>
        /// <returns>Value of the meta data we get</returns>
        public async Task<String> GetLobbyData(string key) 
        {
            EnsureProviderSet();
            return await _currentProvider.GetLobbyDataAsync(key);
        }

        /// <summary>
        /// Toggles the local users ready state automatically
        /// </summary>
        public void ToggleLocalReady()
        {
            if (!_currentLobby.IsValid)
            {
                PurrLogger.LogError($"Can't toggle ready state, current lobby is invalid.");
                return;
            }
            
            var localUserId = _currentProvider.GetLocalUserIdAsync().Result;
            if (string.IsNullOrEmpty(localUserId))
            {
                PurrLogger.LogError($"Can't toggle ready state, local user ID is null or empty.");
                return;
            }
            
            var localLobbyUser = _currentLobby.Members.Find(x => x.Id == localUserId);
            SetIsReady(localUserId, !localLobbyUser.IsReady);
        }

        private void OnDestroy()
        {
            _currentProvider = null;
            _lobbyDataHolder = null;
        }

        private async void CallOnAllReady()
        {
            await WaitForAllTasksAsync();
            bool allReady = _currentLobby.IsValid && _currentLobby.Members.TrueForAll(x => x.IsReady);
            if (allReady)
            {
                try
                {
                    await _currentProvider.SetAllReadyAsync();
                    OnAllReady?.Invoke();
                }
                catch (Exception ex)
                {
                    PurrLogger.LogError($"SetAllReadyAsync threw: {ex.Message}");
                    IsStarting = false;
                }
            }
            else
            {
                IsStarting = false;
            }
        }
        
        public async Task WaitForAllTasksAsync()
        {
            while (_taskLock > 0)
            {
                await Task.Yield();
            }
        }

        private async void RunTask(Func<Task> taskFunc)
        {
            if (taskFunc == null || _currentProvider == null) return;

            _taskLock++;
            try
            {
                await taskFunc();
            }
            catch (Exception ex)
            {
                PurrLogger.LogError($"Task Error: {ex.Message}");
            }
            finally
            {
                _taskLock--;
                if (_taskLock < 0)
                    _taskLock = 0;
            }
        }

        private void EnsureProviderSet()
        {
            if (_currentProvider == null)
                throw new InvalidOperationException("No lobby provider has been set.");
        }

        public void SetLobbyStarted()
        {
            _currentProvider.SetLobbyStartedAsync();
        }

        [System.Serializable]
        public class CreateRoomArgs
        {
            public int maxPlayers = 5;
            public SerializableDictionary<string, string> roomProperties = null;
        }
        
        [System.Serializable]
        public enum FriendFilter
        {
            InThisGame,
            Online,
            All
        }
    }
}