using System.Collections;
using System.Collections.Generic;
using TMPro;
using PurrLobby;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Steam;
using PurrNet.Transports;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LobbySceneController : MonoBehaviour
{
    [SerializeField] private LobbyManager lobbyManager;
    [SerializeField] private NetworkManager networkManager;
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject purrLobbyPanel;
    [SerializeField] private GameObject characterSelectPanel;
    [SerializeField] private GameObject settingsPanel;
    [SerializeField] private GameObject upgradePanel;
    [SerializeField] private GameObject creditsPanel;
    [SerializeField] private GameObject encyclopediaPanel;
    [SerializeField] private GameObject[] upgradeButtons;
    // The "Upgrade Shop" button's own text (possibly more than one copy across scenes/panels, same reason
    // upgradeButtons above is an array) — turns green whenever any base stat is both unlocked and
    // affordable, as a "you can buy something" nudge. Original color cached per-entry at Start() and
    // restored when nothing's affordable, rather than assuming a hardcoded "normal" color.
    [SerializeField] private TextMeshProUGUI[] upgradeButtonTexts;
    [SerializeField] private Color affordableUpgradeColor = Color.green;
    private Color[] _upgradeButtonDefaultColors;
    [SerializeField] private ViewManager viewManager;

    [Header("Unlock Popup")]
    [SerializeField] private GameObject unlockPopup;
    [SerializeField] private TextMeshProUGUI unlockPopupText;
    [SerializeField] private AudioClip unlockSound;

    // Off only for testing reconnection — see OnAllReady's own comment on the one call this gates.
    [SerializeField] private bool lockLobbyOnMatchStart = true;

    private int _expectedPlayerCount;
    // Deduplicated by PlayerID rather than a raw counter — a flaky connection can retry and register a
    // brand-new PlayerID with the server for what is physically the same reconnecting client before the
    // original attempt's own connection fully resolves, which mints a SECOND distinct PlayerID alongside the
    // first (PurrNet's cookie-based reconnection only recognizes a truly NEW connection attempt as "the same
    // player returning" — it doesn't dedupe two live, simultaneously-registering attempts from one client),
    // so a plain counter could cross _expectedPlayerCount purely from reconnect noise without every distinct
    // player actually being present.
    private readonly HashSet<PlayerID> _joinedPlayerIds = new();
    // Snapshotted (host-only) the instant OnAllReady fires — the exact set of Steam IDs the host's own
    // local lobby view actually contained at that moment. This is the host's own visual truth: whoever
    // ISN'T in this set gets rejected in OnPurrNetPlayerJoined below, even if their own client thinks
    // they're already part of the match — the host should never have to "guess" who's really in their
    // lobby, or let a connection through that wasn't part of what it visibly saw when it started.
    private readonly HashSet<ulong> _expectedSteamIds = new();
    private bool _initializedForLobby;
    private bool _connectingToHost;
    private Coroutine _connectRoutine;

    // Normal match-start (ready confirmed -> actually loaded into the Game scene) takes well under 5s —
    // 20s is a generous ceiling, not a tuned "typical" wait. A successful transition unloads this whole
    // scene (killing this coroutine along with it) well before this fires, so reaching the timeout only
    // ever means the attempt is genuinely stuck, not just slow.
    [SerializeField] private float matchStartTimeoutSeconds = 20f;
    private Coroutine _matchStartWatchdog;

    private void OnEnable()  => UnlockManager.OnUnlockQueued += OnUnlockQueuedWhileInLobby;
    private void OnDisable() => UnlockManager.OnUnlockQueued -= OnUnlockQueuedWhileInLobby;

    private void OnUnlockQueuedWhileInLobby()
    {
        if (unlockPopup != null && !unlockPopup.activeSelf)
            ShowNextUnlock();
    }

    private void Start()
    {
        // PausePD.UpdateView() drives Time.timeScale (and/or predictionManager.time.timeScale) to 0 every
        // frame while paused, and only ReturnToLobbyManager.ReturnToLobby() ever resets it back to 1 — a
        // disconnect-triggered kick back to this scene bypasses that entirely, so if the game happened to
        // be paused at the moment of disconnect, timeScale stays stuck at 0 forever (the Game scene's
        // PausePD instance is simply gone, nothing left to set it back). That silently hangs every
        // WaitForSeconds-based coroutine in this scene, including ConnectToHostWithRetry's own retry loop,
        // with no error — the Lobby scene should never run at anything but normal timescale regardless of
        // how we got here, so assert it unconditionally on load.
        Time.timeScale = 1f;

        if (!networkManager)
            networkManager = NetworkManager.main;

        // Emulate a genuinely fresh app launch every single time we're in the Lobby scene — a first-ever
        // match start (StartServer()/StartClient() on a NetworkManager that's never connected before) is
        // the ONE case that's been reliable across every live test this session; every failure so far has
        // involved a connection that survived from a PREVIOUS match cycle into a new one (duplicate
        // StartClient races, PromoteToServer() edge cases, an unexplained round-2-only connect hang with no
        // error at all). Rather than continuing to chase every individual way a carried-over connection can
        // go stale, just don't carry one over — unconditionally tear down client/server the moment we're
        // back here (covers a fresh launch, ReturnToLobbyManager's networked scene load back from a match,
        // and a disconnect-triggered kick back to this scene), so every match start begins from the exact
        // same known-good state regardless of history. No-ops harmlessly if already disconnected.
        // TEMP diagnostic — confirming Start() actually re-runs on every Lobby scene (re)load and what
        // state gets read at that exact moment. Remove once the reset is confirmed reliable.
        Debug.Log($"[LobbySceneController] Start(): pre-reset serverState={networkManager.serverState}, clientState={networkManager.clientState}, GetInstanceID={networkManager.GetInstanceID()}.");
        // Guarded by serverState/clientState (NOT the plain isServer/isClient booleans, and NOT
        // unconditional either) — this is the exact same check GenericTransport.StartServer/StartClient use
        // internally before registering modules. isServer/isClient only reflect ConnectionState.Connected
        // specifically, so a guard on those could skip Stop*() during a Connecting/Disconnecting window and
        // leave a connection alive underneath. But calling StopServer()/StopClient() UNCONDITIONALLY is
        // actively unsafe, not just wasted effort — confirmed live: GenericTransport.StopServer() always
        // calls networkManager.InternalUnregisterServerModules() with no guard of its own, so calling it on
        // a NetworkManager that was NEVER started (modules never registered) corrupted that bookkeeping and
        // broke the very next real StartServer() call ("[SteamServer] Failed to create listen socket.").
        // serverState/clientState (Disconnected vs Connecting/Connected/Disconnecting) is the correct,
        // PurrNet-idiomatic "is there actually anything to stop" check.
        if (networkManager.serverState != ConnectionState.Disconnected) networkManager.StopServer();
        if (networkManager.clientState != ConnectionState.Disconnected) networkManager.StopClient();
        // TEMP diagnostic — same-frame readback right after the Stop calls.
        Debug.Log($"[LobbySceneController] Start(): post-reset (same frame) serverState={networkManager.serverState}, clientState={networkManager.clientState}.");
        StartCoroutine(LogNetworkStateDelayed("Start()"));

        bool shopUnlocked = ProgressionManager.IsShopUnlocked;
        foreach (var btn in upgradeButtons)
            if (btn != null) btn.SetActive(shopUnlocked);

        if (upgradeButtonTexts != null)
        {
            _upgradeButtonDefaultColors = new Color[upgradeButtonTexts.Length];
            for (int i = 0; i < upgradeButtonTexts.Length; i++)
                if (upgradeButtonTexts[i] != null) _upgradeButtonDefaultColors[i] = upgradeButtonTexts[i].color;
        }
        RefreshUpgradeButtonAffordability();

        if (unlockPopup != null)
        {
            if (UnlockManager.HasPending)
                ShowNextUnlock();
            else
                unlockPopup.SetActive(false);
        }

        lobbyManager.OnRoomJoined.AddListener(OnRoomJoined);
        lobbyManager.OnRoomLeft.AddListener(OnRoomLeft);
        lobbyManager.OnRoomUpdated.AddListener(OnRoomUpdated);
        lobbyManager.OnAllReady.AddListener(OnAllReady);
        lobbyManager.OnError.AddListener(OnLobbyError);
        networkManager.onClientConnectionState += OnClientConnectionStateChanged;

        if (lobbyManager.CurrentLobby.IsValid)
        {
            var lobby = lobbyManager.CurrentLobby;
            if (lobby.IsOwner && ulong.TryParse(lobby.LobbyId, out ulong returnLobbyId))
            {
                var steamId = new CSteamID(returnLobbyId);
                SteamMatchmaking.SetLobbyJoinable(steamId, true);
                SteamMatchmaking.SetLobbyData(steamId, "Started", "False");
            }
            InitializeForLobby(lobby);
        }
        else
        {
            mainMenuPanel.SetActive(true);
            purrLobbyPanel.SetActive(false);
            characterSelectPanel.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        lobbyManager.OnRoomJoined.RemoveListener(OnRoomJoined);
        lobbyManager.OnRoomLeft.RemoveListener(OnRoomLeft);
        lobbyManager.OnRoomUpdated.RemoveListener(OnRoomUpdated);
        lobbyManager.OnAllReady.RemoveListener(OnAllReady);
        lobbyManager.OnError.RemoveListener(OnLobbyError);
        if (networkManager != null)
            networkManager.onClientConnectionState -= OnClientConnectionStateChanged;
    }

    // Fires for ANY client-side connection state change — we only care about landing back at Disconnected
    // while we still thought we were part of a match (either mid-connect via ConnectToHostWithRetry, or
    // already fully joined and then kicked/dropped). Nothing in the project previously listened for this
    // at all, so a lost connection — for any reason, not just OnPurrNetPlayerJoined's own reject-below —
    // used to leave the client permanently frozen (e.g. still on the loading screen) with no recovery path.
    private void OnClientConnectionStateChanged(ConnectionState state)
    {
        if (state != ConnectionState.Disconnected) return;
        // The lobby OWNER's own listen-server client bouncing through Disconnected during normal
        // StartServer()/StartClientDelayed() setup is expected and not a "lost connection" in the sense
        // this guards against — only a non-owner losing its connection to someone else's server matters here.
        if (lobbyManager.CurrentLobby.IsOwner) return;
        if (!_connectingToHost && !networkManager.isClient) return;

        AbortStuckMatchStart("Lost connection to host (or was rejected)");
    }

    private void StartMatchStartWatchdog()
    {
        if (_matchStartWatchdog != null) StopCoroutine(_matchStartWatchdog);
        _matchStartWatchdog = StartCoroutine(MatchStartWatchdogRoutine());
    }

    private IEnumerator MatchStartWatchdogRoutine()
    {
        yield return new WaitForSeconds(matchStartTimeoutSeconds);
        // Still running after the timeout means the match never actually started — a genuinely successful
        // transition into the Game scene unloads this whole scene (killing this coroutine along with it)
        // well before this point, since a normal match-start takes well under 5s.
        AbortStuckMatchStart($"Match start timed out after {matchStartTimeoutSeconds}s");
    }

    // Shared bail-out for both a detected lost connection and the watchdog timeout — forces a clean return
    // to the main menu instead of leaving a stuck client (frozen "connected but nothing's happening") or a
    // stuck host (never satisfies TryLoadGameScene's player-count gate) sitting on the loading screen
    // indefinitely. Covers both roles: a stuck HOST also gets its own StopServer()/StopClient() here, not
    // just a stuck client.
    private void AbortStuckMatchStart(string reason)
    {
        Debug.LogWarning($"[LobbySceneController] {reason} — aborting and returning to main menu.");

        if (_matchStartWatchdog != null)
        {
            StopCoroutine(_matchStartWatchdog);
            _matchStartWatchdog = null;
        }
        if (_connectRoutine != null)
        {
            StopCoroutine(_connectRoutine);
            _connectRoutine = null;
        }
        _connectingToHost = false;

        if (networkManager != null)
        {
            networkManager.onPlayerJoined -= OnPurrNetPlayerJoined;
            networkManager.onPlayerLeft -= OnPurrNetPlayerLeft;
            if (networkManager.isClient) networkManager.StopClient();
            if (networkManager.isServer) networkManager.StopServer();
        }

        if (LoadingScreen.instance != null) LoadingScreen.instance.Hide();

        // Actually leaves the Steam lobby too, not just resets local UI/PurrNet state — without this, the
        // player stayed a real member of the OLD (broken) Steam lobby underneath the main menu, which
        // silently collided with a subsequent "Create Lobby" attempt (LobbyManager.CreateRoom() has no
        // guard against being called while already in a lobby — live-confirmed this exact collision).
        // Routes through the SAME LeaveLobby() path the real "Leave Lobby" button uses, which fires
        // OnRoomLeft() — that handler already resets _expectedPlayerCount/_joinedPlayerIds/
        // _expectedSteamIds and toggles the panels, so none of that is duplicated here.
        // TODO: replace this bare kick-to-main-menu with a proper "match start failed" popup/message once
        // the UI for it exists — right now this is silent from the player's perspective beyond suddenly
        // landing back on the main menu, which is a real UX gap, just no longer an indefinite frozen
        // loading screen.
        lobbyManager.LeaveLobby();
    }

    // e.g. CreateLobbyAsync timing out or Steam rejecting the request — bail back to the main PurrLobby
    // menu instead of leaving whatever loading overlay (CreatingRoomView, etc.) stuck on screen forever.
    private void OnLobbyError(string message)
    {
        Debug.LogWarning($"[LobbySceneController] Lobby error: {message}");
        if (viewManager != null) viewManager.ShowView<MainMenuView>();
    }

    private void OnApplicationQuit()
    {
        lobbyManager.LeaveLobby();
    }

    public void OnMultiplayerClicked()
    {
        mainMenuPanel.SetActive(false);
        purrLobbyPanel.SetActive(true);
        viewManager.OnLeaveBrowseClicked();
    }

    public void OnBackToMainMenuClicked()
    {
        purrLobbyPanel.SetActive(false);
        mainMenuPanel.SetActive(true);
    }

    private void ShowNextUnlock()
    {
        string msg = UnlockManager.Dequeue();
        if (unlockPopupText != null) unlockPopupText.text = $"{msg} Unlocked!";
        unlockPopup.SetActive(true);
        SFXManager.instance?.Play(unlockSound, Vector3.zero);
    }

    public void OnUnlockPopupContinueClicked()
    {
        if (UnlockManager.HasPending)
            ShowNextUnlock();
        else
            unlockPopup.SetActive(false);
    }

    public void OnSettingsClicked()
    {
        if (characterSelectPanel.activeInHierarchy)
            lobbyManager.SetIsReady(GameStartData.LocalUserId, false);
        settingsPanel.SetActive(true);
    }

    public void OnSettingsBackClicked()
    {
        settingsPanel.SetActive(false);
    }

    public void OnUpgradeClicked()
    {
        if (characterSelectPanel.activeInHierarchy)
            lobbyManager.SetIsReady(GameStartData.LocalUserId, false);
        upgradePanel.SetActive(true);
    }

    public void OnUpgradeBackClicked()
    {
        upgradePanel.SetActive(false);
        if (ulong.TryParse(lobbyManager.CurrentLobby.LobbyId, out ulong lobbyIdRaw))
            SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "power", ProgressionManager.SpentPoints.ToString());
        RefreshUpgradeButtonAffordability();
    }

    // Checked once on scene load and once again whenever the shop closes (rather than continuously) — the
    // only things that can change affordability are purchases made INSIDE the shop, so re-checking anywhere
    // else would just be redundant work.
    private void RefreshUpgradeButtonAffordability()
    {
        if (upgradeButtonTexts == null) return;
        bool affordable = ProgressionManager.AnyBaseStatAffordable();
        for (int i = 0; i < upgradeButtonTexts.Length; i++)
        {
            if (upgradeButtonTexts[i] == null) continue;
            upgradeButtonTexts[i].color = affordable ? affordableUpgradeColor : _upgradeButtonDefaultColors[i];
        }
    }

    public void OnEncyclopediaClicked()
    {
        if (characterSelectPanel.activeInHierarchy)
            lobbyManager.SetIsReady(GameStartData.LocalUserId, false);
        encyclopediaPanel.SetActive(true);
    }

    public void OnEncyclopediaBackClicked()
    {
        encyclopediaPanel.SetActive(false);
    }

    public void OnCreditsClicked()
    {
        mainMenuPanel.SetActive(false);
        creditsPanel.SetActive(true);
    }

    public void OnCreditsBackClicked()
    {
        creditsPanel.SetActive(false);
        mainMenuPanel.SetActive(true);
    }

    public void OnExitClicked()
    {
        Application.Quit();
    }

    private void InitializeForLobby(Lobby lobby)
    {
        _initializedForLobby = true;

        GameStartData.LocalUserId = lobbyManager.CurrentProvider.GetLocalUserIdAsync().Result;
        GameStartData.LocalCharacterIndex = 0;
        lobbyManager.SetIsReady(GameStartData.LocalUserId, false);
        // Keeps CharacterSelectUI's own local state (ready flag, _isRandom, cached other-members'
        // selections) in sync with the lobby-side reset above — belt and suspenders alongside the
        // OnRoomLeft() reset, so a fresh lobby always starts genuinely clean regardless of which path got
        // us here.
        CharacterSelectUI.instance?.ResetLocalLobbyState();
        _connectingToHost = false;

        // Color is no longer self-assigned here — the host is the single source of truth for color
        // assignment now (see CharacterSelectUI.ResolveColorAssignments/ColorAssignmentsKey). This starts
        // at whatever GameStartData.LocalColorIndex already held (0 by default) as a harmless placeholder;
        // the RefreshFromLobby call below resolves the real assignment — instantly for the host itself (no
        // network round-trip to read back its own just-written data), or within one lobby-update round-trip
        // for a joining client waiting on the host's answer.
        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
        {
            var steamLobby = new CSteamID(lobbyIdRaw);
            SteamMatchmaking.SetLobbyMemberData(steamLobby, "char", "0");
            SteamMatchmaking.SetLobbyMemberData(steamLobby, "power", ProgressionManager.SpentPoints.ToString());
        }
        CharacterSelectUI.instance?.SetLocalDefault(GameStartData.LocalUserId, 0, GameStartData.LocalColorIndex);
        CharacterSelectUI.instance?.RefreshCharacterDetails(0);
        CharacterSelectUI.instance?.RefreshFromLobby(lobby);
        ShowCharacterSelect();
    }

    private void OnRoomJoined(Lobby lobby)
    {
        if (_initializedForLobby) return;
        InitializeForLobby(lobby);
    }

    private void OnRoomLeft()
    {
        _initializedForLobby = false;
        if (_matchStartWatchdog != null)
        {
            StopCoroutine(_matchStartWatchdog);
            _matchStartWatchdog = null;
        }
        // Was previously never reset here — CharacterSelectUI.ResetReadyState() (now ResetLocalLobbyState())
        // existed but nothing called it, so local UI state could carry over stale into a later lobby session
        // (e.g. join lobby A, ready up, leave, join lobby B — InitializeForLobby already resets the
        // LOBBY-side ready flag on join, but the local _localIsReady bool would still read true from lobby
        // A, making the next Ready click send "not ready" instead of "ready").
        CharacterSelectUI.instance?.ResetLocalLobbyState();
        // Same "never reset" gap — once set true (starting a connect attempt for THIS lobby's game start),
        // nothing ever cleared it back to false. Left stuck true, it would permanently block
        // OnRoomUpdated's own auto-connect check (!_connectingToHost) for every future lobby this session.
        _connectingToHost = false;
        // Defensive cleanup in case a game-start sequence was still in flight when we left (e.g. the host
        // started loading but this client bailed out before TryLoadGameScene's own normal unsubscribe ran)
        // — an un-cleared subscription would keep firing OnPurrNetPlayerJoined for a lobby we're no longer
        // part of.
        if (networkManager != null)
        {
            networkManager.onPlayerJoined -= OnPurrNetPlayerJoined;
            networkManager.onPlayerLeft -= OnPurrNetPlayerLeft;
            // TEMP diagnostic — see Start()'s own matching comment.
            Debug.Log($"[LobbySceneController] OnRoomLeft(): pre-reset serverState={networkManager.serverState}, clientState={networkManager.clientState}, GetInstanceID={networkManager.GetInstanceID()}.");
            // Same clean-slate reset as Start()'s own, same serverState/clientState guard (see Start()'s own
            // comment for why unconditional Stop*() calls are actively unsafe, not just wasted effort) —
            // covers leaving WITHOUT a scene reload (e.g. Leave Lobby, then Join a different lobby by code,
            // all still inside this one Lobby scene load) where Start() itself won't run again to catch it.
            if (networkManager.serverState != ConnectionState.Disconnected) networkManager.StopServer();
            if (networkManager.clientState != ConnectionState.Disconnected) networkManager.StopClient();
            // TEMP diagnostic — same-frame readback, see Start()'s own matching comment.
            Debug.Log($"[LobbySceneController] OnRoomLeft(): post-reset (same frame) serverState={networkManager.serverState}, clientState={networkManager.clientState}.");
            StartCoroutine(LogNetworkStateDelayed("OnRoomLeft()"));
        }
        _expectedPlayerCount = 0;
        _joinedPlayerIds.Clear();
        _expectedSteamIds.Clear();
        GameStartData.HostColorAssignments.Clear();
        characterSelectPanel.SetActive(false);
        purrLobbyPanel.SetActive(false);
        mainMenuPanel.SetActive(true);
    }

    private void OnRoomUpdated(Lobby lobby)
    {
        if (characterSelectPanel.activeInHierarchy)
            CharacterSelectUI.instance?.RefreshFromLobby(lobby);

        // Once we're waiting on players to finish connecting (_expectedPlayerCount was captured in
        // OnAllReady), a member leaving the Steam lobby before their PurrNet connection ever completed
        // would otherwise leave everyone else waiting forever for a join event that can now never come —
        // this was the "stuck until the struggling player disconnected" bug: shrinking the target the
        // moment Steam confirms they're gone, and re-checking the gate immediately, is what actually
        // unblocks the remaining players instead of relying on some other event to coincidentally do it.
        if (_expectedPlayerCount > 0 && lobby.Members.Count < _expectedPlayerCount)
        {
            _expectedPlayerCount = lobby.Members.Count;
            TryLoadGameScene();
        }

        // Sole trigger for a client to connect — see OnAllReady's own comment for why clients no longer act
        // on their own independent "everyone's ready" conclusion. This reacts to the real Steam lobby data
        // the host wrote via SetLobbyStarted(), so every client starts from the SAME single fact regardless
        // of what each client's own local view happened to look like at any given moment — this is also the
        // pre-existing reconnect path (a dropped player rejoining an already-started match), now doing
        // double duty as the normal-case trigger too, not just the reconnect-only case it used to be.
        if (!lobby.IsOwner && !networkManager.isClient && !_connectingToHost)
        {
            if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
            {
                var steamLobbyId = new CSteamID(lobbyIdRaw);
                string started = SteamMatchmaking.GetLobbyData(steamLobbyId, "Started");
                if (started == "True")
                {
                    if (LoadingScreen.instance != null) LoadingScreen.instance.Show();
                    // Defensive — guards against a still-running coroutine from an earlier attempt (e.g. a
                    // prior lobby session) that somehow wasn't cleared, same "don't let two
                    // ConnectToHostWithRetry coroutines hammer StartClient at once" reasoning the old
                    // OnAllReady client branch used to apply here.
                    if (_connectRoutine != null) StopCoroutine(_connectRoutine);
                    _connectingToHost = true;
                    StartMatchStartWatchdog();
                    if (networkManager.transport is SteamTransport steamTransport)
                    {
                        var hostSteamId = SteamMatchmaking.GetLobbyOwner(steamLobbyId);
                        steamTransport.address = hostSteamId.m_SteamID.ToString();
                    }
                    _connectRoutine = StartCoroutine(ConnectToHostWithRetry());
                }
            }
        }
    }

    private void OnAllReady()
    {
        if (!networkManager)
            networkManager = NetworkManager.main;

        var lobby = lobbyManager.CurrentLobby;

        // Only the HOST acts on its own local "everyone's ready" conclusion. A client's local Steam lobby
        // view can lag behind reality (e.g. a just-joined member not yet visible on THIS specific machine) —
        // a client independently reaching "everyone's ready" from its own possibly-stale view and connecting
        // on its own was a real split-brain race: each machine could act on a DIFFERENT snapshot of "who's
        // ready", at different, uncoordinated moments (confirmed live: the host started a "solo" 1-player
        // match while the real second player was still trying to join, because each side's own local check
        // fired independently and reached different conclusions). Clients now do nothing here at all — they
        // instead react to OnRoomUpdated's "Started"=="True" check below, which is driven by the single,
        // real, host-authoritative Steam lobby data flag SetLobbyStarted() writes, not by their own locally-
        // derived (and possibly wrong) conclusion.
        if (!lobby.IsOwner) return;

        if (LoadingScreen.instance != null) LoadingScreen.instance.Show();
        StartMatchStartWatchdog();
        _expectedPlayerCount = lobby.Members.Count;

        // Snapshot exactly who's visually here right now — see _expectedSteamIds' own doc comment. Parsed
        // straight from the Steam ID strings the lobby already carries; a malformed/unparseable id (should
        // never happen for a real Steam member) is simply skipped rather than crashing this whole method.
        _expectedSteamIds.Clear();
        foreach (var m in lobby.Members)
            if (ulong.TryParse(m.Id, out ulong steamId))
                _expectedSteamIds.Add(steamId);

        // Server-authoritative color assignment (see GameStartData.HostColorAssignments' own doc comment):
        // capture the host's own just-resolved Steam-ID-to-color map atomically, right here at match
        // lock-in, so PlayerDataPD.SanitizeInput can assign each player's real color server-side instead of
        // trusting whatever GameStartData.LocalColorIndex a client happened to have locally at this exact
        // moment (which could still be stale/wrong for a slow-connecting client — the actual bug this
        // replaces a whole confirm/kick handshake with).
        var resolvedColors = CharacterSelectUI.instance?.ResolveColorAssignments(lobby);
        GameStartData.HostColorAssignments.Clear();
        if (resolvedColors != null)
            foreach (var kvp in resolvedColors)
                GameStartData.HostColorAssignments[kvp.Key] = kvp.Value;

        // TEMP diagnostic — cross-checking LobbyManager.ConfirmAllReadyStable's own logging: this is the
        // exact moment _expectedPlayerCount gets captured (host side), which is what TryLoadGameScene's own
        // gate is checked against. If a future repro shows the game loading with fewer players than actually
        // present, compare this line's member list/names against who was really in the lobby at that
        // instant. Remove once the premature-start race is confirmed fixed (or root-caused elsewhere).
        Debug.Log($"[LobbySceneController] OnAllReady: IsOwner={lobby.IsOwner}, capturing _expectedPlayerCount={_expectedPlayerCount} from members [{string.Join(", ", lobby.Members.ConvertAll(m => $"{m.DisplayName}:{m.Id}"))}].");

        // Gated behind lockLobbyOnMatchStart (default on) rather than removed outright — locking the
        // lobby the moment a match starts is the right long-term behavior (stops randoms from
        // Join-by-code'ing into a match already in progress), but it's also the one thing currently
        // stopping a dropped player from rejoining the SAME match via LobbySceneController.OnRoomUpdated's
        // existing "lobby says Started == True, I'm not yet a client" reconnect path. Flip this off in
        // the Inspector to test reconnection without permanently changing the normal-case behavior.
        if (lockLobbyOnMatchStart && ulong.TryParse(lobby.LobbyId, out ulong lockLobbyId))
            SteamMatchmaking.SetLobbyJoinable(new CSteamID(lockLobbyId), false);
        lobbyManager.SetLobbyStarted();

        // Plain, unconditional StartServer() — the ONE path confirmed reliable across every live test this
        // session. Used to also handle "was this machine already a PurrNet client from a previous match
        // cycle" via PromoteToServer(), but Start()/OnRoomLeft() now unconditionally tear down client/server
        // the moment we're anywhere in the Lobby scene (see Start()'s own comment) — networkManager.isClient
        // and .isServer are therefore guaranteed false by the time OnAllReady can ever fire, so that whole
        // branch (and its own follow-on bugs: a duplicate-StartClient race, then an unretried self-connect
        // that could silently never take) can no longer be reached at all. Removed rather than left as dead
        // code, since a currently-unreachable branch has a way of quietly becoming reachable again the next
        // time this method gets touched.
        if (!networkManager.isServer)
            networkManager.StartServer();
        if (!networkManager.isClient)
            StartCoroutine(StartClientDelayed());

        _joinedPlayerIds.Clear();
        foreach (var p in networkManager.players) _joinedPlayerIds.Add(p);
        networkManager.onPlayerJoined += OnPurrNetPlayerJoined;
        networkManager.onPlayerLeft += OnPurrNetPlayerLeft;
        TryLoadGameScene();
    }

    // Was a single, unretried StartClient() call — the host's own listen-server self-connect had none of
    // the resilience ConnectToHostWithRetry() below already gives a REMOTE client's connection attempt.
    // Live-observed cause: a transient native Steam networking stall (e.g. the
    // "SteamnetworkingSockets service thread waited Nms for lock" assertions seen in real test logs) could
    // make this one StartClient() call fail, permanently stranding the host at "server started, client
    // none" — which is exactly broken, since a host is supposed to be BOTH its own server and its own
    // client. Same 20-attempt/4s-timeout/1s-gap shape as ConnectToHostWithRetry(), just without that
    // method's _connectingToHost bookkeeping (that field only matters for the remote-client reconnect-guard
    // logic in OnRoomUpdated, not for the host's own self-connect). MatchStartWatchdog (20s) still catches
    // a genuinely stuck case well before all 20 attempts would exhaust — this just gives a real transient
    // hiccup a chance to recover instead of guaranteeing a stall.
    private IEnumerator StartClientDelayed(float delay = 1f)
    {
        const int maxAttempts = 20;
        const float attemptTimeout = 4f;

        yield return new WaitForSeconds(delay);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            networkManager.StartClient();

            float elapsed = 0f;
            while (elapsed < attemptTimeout && !networkManager.isClient)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (networkManager.isClient)
                yield break;

            yield return new WaitForSeconds(1f);
        }

        Debug.LogError("[LobbySceneController] Host's own self-connect (StartClientDelayed) failed after all attempts.");
    }

    // TEMP diagnostic — logs NetworkManager's state ~1s after a reset attempt, to catch whether something
    // ELSE reconnects it shortly afterward (as opposed to the reset itself never taking effect at all,
    // which the same-frame post-reset log right before this call already covers). Remove alongside the
    // other TEMP diagnostics once the still-connected-after-leaving bug is root-caused.
    private IEnumerator LogNetworkStateDelayed(string sourceLabel)
    {
        yield return new WaitForSeconds(1f);
        if (networkManager != null)
            Debug.Log($"[LobbySceneController] {sourceLabel}: +1s readback serverState={networkManager.serverState}, clientState={networkManager.clientState}.");
    }

    private IEnumerator ConnectToHostWithRetry()
    {
        const int maxAttempts = 20;
        const float attemptTimeout = 4f;
        var initialWait = new WaitForSeconds(1f);
        var retryWait = new WaitForSeconds(1f);

        yield return initialWait;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            networkManager.StartClient();

            float elapsed = 0f;
            while (elapsed < attemptTimeout && !networkManager.isClient)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (networkManager.isClient)
            {
                _connectingToHost = false;
                _connectRoutine = null;
                yield break;
            }

            yield return retryWait;
        }

        Debug.LogError("Failed to connect to host after all attempts.");
        // Without this, a failed attempt left _connectingToHost stuck true forever — nothing else in this
        // class ever clears it, so every later reconnect trigger (OnRoomUpdated on the next lobby update,
        // or OnAllReady on a later ready-up) saw "already connecting" and silently did nothing.
        _connectingToHost = false;
        _connectRoutine = null;
    }

    private void OnPurrNetPlayerJoined(PlayerID player, bool isReconnect, bool asServer)
    {
        if (!asServer) return;

        // Reject anyone the host didn't actually see in its own lobby snapshot at OnAllReady time — see
        // _expectedSteamIds' own doc comment. Never applies to the host's own local (listen-server) player,
        // which never goes through a real remote Connection to resolve a Steam ID from in the first place.
        if (player != networkManager.localPlayer &&
            networkManager.TryGetModule<PlayersManager>(true, out var playersManager) &&
            playersManager.TryGetConnection(player, out var conn) &&
            networkManager.transport is SteamTransport steamTransport)
        {
            ulong steamId = steamTransport.GetSteamID(conn);
            // 0/unresolved fails OPEN (let them through) rather than closed — this should only reject on
            // POSITIVE evidence the connecting Steam ID wasn't part of what the host actually saw, never on
            // an inability to look it up at all.
            if (steamId != 0 && !_expectedSteamIds.Contains(steamId))
            {
                Debug.Log($"[LobbySceneController] Rejecting player {player} (SteamID {steamId}) — not present in the host's own lobby snapshot when the match started.");
                playersManager.KickPlayer(player);
                return;
            }
        }

        // HashSet.Add is naturally idempotent — a reconnect storm minting a fresh PlayerID for what's
        // physically the same client would previously inflate a raw counter past the real distinct-player
        // count; a genuinely new distinct id is what actually moves this closer to _expectedPlayerCount.
        _joinedPlayerIds.Add(player);
        TryLoadGameScene();
    }

    private void OnPurrNetPlayerLeft(PlayerID player, bool asServer)
    {
        if (!asServer) return;
        _joinedPlayerIds.Remove(player);
    }

    private void TryLoadGameScene()
    {
        if (_joinedPlayerIds.Count < _expectedPlayerCount) return;
        networkManager.onPlayerJoined -= OnPurrNetPlayerJoined;
        networkManager.onPlayerLeft -= OnPurrNetPlayerLeft;
        networkManager.sceneModule.LoadSceneAsync(GameStartData.GameSceneName, new PurrSceneSettings
        {
            mode = LoadSceneMode.Single,
            isPublic = true
        });
    }

    private void ShowCharacterSelect()
    {
        mainMenuPanel.SetActive(false);
        purrLobbyPanel.SetActive(false);
        characterSelectPanel.SetActive(true);
    }
}
