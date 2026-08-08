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
    // Host-only: Steam IDs that actually completed a real PurrNet connection this attempt (populated
    // alongside _joinedPlayerIds in OnPurrNetPlayerJoined). Not currently used to change any behavior —
    // kept purely as diagnostic bookkeeping (see the "connected — N/M" log in OnPurrNetPlayerJoined) and
    // for the TODO in AbortStuckMatchStart that wants to eventually name which player(s) never connected.
    private readonly HashSet<ulong> _joinedSteamIds = new();
    private bool _initializedForLobby;
    private bool _connectingToHost;
    private Coroutine _connectRoutine;

    // Normal match-start (ready confirmed -> actually loaded into the Game scene) takes well under 5s —
    // 20s is a generous ceiling, not a tuned "typical" wait. A successful transition unloads this whole
    // scene (killing this coroutine along with it) well before this fires, so reaching the timeout only
    // ever means the attempt is genuinely stuck, not just slow.
    [SerializeField] private float matchStartTimeoutSeconds = 20f;
    private Coroutine _matchStartWatchdog;
    // Host-only: re-sends SetLobbyStarted() periodically for the whole watchdog window instead of once —
    // see RewriteStartedFlagPeriodically's own doc comment for why a single write isn't reliable enough.
    private Coroutine _startedFlagRewriteRoutine;

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

        // LoadingScreen is DontDestroyOnLoad — if it were ever somehow left showing (a bug, or a scene
        // reload happening mid-transition instead of via the paths that already hide it), it would persist
        // showing right into this fresh Lobby scene load and permanently lock out every guarded
        // character-select button (see LoadingScreen.IsShowing's own doc comment) with no way to recover.
        // Unconditionally hidden here for the same "emulate a genuinely fresh launch" reasoning as the
        // network reset right below — covers a fresh launch, a return from a match via ReturnToLobbyManager,
        // and a disconnect-triggered kick back to this scene, regardless of what state it was left in.
        LoadingScreen.instance?.Hide();

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

    // A single SetLobbyStarted() write can silently never reach a given client — confirmed live: one
    // client's game never received the resulting "Started" LobbyDataUpdate_t callback at all, so it never
    // even attempted to connect (no watchdog, no connection-state-change, nothing — there was simply
    // nothing to trigger, since that flag is the ONLY signal a client has that a match is starting). Steam
    // lobby-data delivery isn't guaranteed-once, so re-sending the identical write periodically for the
    // whole watchdog window means a client only needs to catch ONE of several attempts instead of the one
    // original write. Stopped alongside the watchdog itself (AbortStuckMatchStart / TryLoadGameScene) —
    // never needs to outlive a single match-start attempt.
    private IEnumerator RewriteStartedFlagPeriodically()
    {
        var wait = new WaitForSeconds(3f);
        while (true)
        {
            yield return wait;
            lobbyManager.SetLobbyStarted();
        }
    }

    private IEnumerator MatchStartWatchdogRoutine()
    {
        yield return new WaitForSeconds(matchStartTimeoutSeconds);
        // Still running after the timeout means the match never actually started — a genuinely successful
        // transition into the Game scene unloads this whole scene (killing this coroutine along with it)
        // well before this point, since a normal match-start takes well under 5s.
        AbortStuckMatchStart($"Match start timed out after {matchStartTimeoutSeconds}s");
    }

    // Shared bail-out for both a detected lost connection and the watchdog timeout — forces a clean end to
    // a stuck match-start attempt instead of leaving a stuck client (frozen "connected but nothing's
    // happening") or a stuck host (never satisfies TryLoadGameScene's player-count gate) sitting on the
    // loading screen indefinitely. Covers both roles: a stuck HOST also gets its own
    // StopServer()/StopClient() here, not just a stuck client.
    //
    // Deliberately simple and unconditional: EVERYONE in the lobby leaves and lands back at the main menu,
    // regardless of role or whether their own connection actually succeeded. Previously tried a more
    // surgical approach (host stays, only the specific non-connecting player gets excluded via a full lobby
    // rebuild + targeted Steam invites) — worked, but was a lot of moving parts (a whole second lobby
    // creation, invite-accept UX, a reentrant-callback bug that took a real debugging session to track down)
    // for a failure mode that should be rare. Simpler and more predictable to just reset everyone and let
    // them manually re-create/re-join, same as any other hard failure.
    private void AbortStuckMatchStart(string reason)
    {
        if (_matchStartWatchdog != null)
        {
            StopCoroutine(_matchStartWatchdog);
            _matchStartWatchdog = null;
        }
        if (_startedFlagRewriteRoutine != null)
        {
            StopCoroutine(_startedFlagRewriteRoutine);
            _startedFlagRewriteRoutine = null;
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
        Debug.LogWarning($"[LobbySceneController] {reason} — aborting and returning to main menu.");

        // Actually leaves the Steam lobby too, not just resets local UI/PurrNet state — without this, the
        // player stayed a real member of the OLD (broken) Steam lobby underneath the main menu, which
        // silently collided with a subsequent "Create Lobby" attempt (LobbyManager.CreateRoom() has no
        // guard against being called while already in a lobby — live-confirmed this exact collision).
        // Routes through the SAME LeaveLobby() path the real "Leave Lobby" button uses, which fires
        // OnRoomLeft() — that handler already resets _expectedPlayerCount/_joinedPlayerIds/_expectedSteamIds
        // and toggles the panels, so none of that is duplicated here.
        // TODO: replace this bare kick-to-main-menu with a proper "match start failed" popup/message once
        // the UI for it exists — right now this is silent from every player's perspective beyond suddenly
        // landing back on the main menu. Ideally it should name which player(s) never connected, e.g.
        // "<name> didn't connect in time" — _expectedSteamIds minus _joinedSteamIds (both still tracked
        // above) is exactly that set, on the host's own machine at least.
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

    // Wrapper specifically so the "Leave Lobby" button can be guarded — it previously called
    // PurrLobby.LobbyManager.LeaveLobby() directly from its own OnClick(), with no way to block it during a
    // transition. Needs the button's OnClick() target rewired in the Inspector from LobbyManager.LeaveLobby
    // to this method instead.
    public void OnLeaveLobbyClicked()
    {
        if (LoadingScreen.instance != null && LoadingScreen.instance.IsShowing) return;
        lobbyManager.LeaveLobby();
    }

    // Locked out while LoadingScreen is up — covers an ordinary match-start attempt. A real logical guard,
    // not just relying on the loading screen visually covering this panel, in case that ever fails to render
    // for some reason — see LoadingScreen.IsShowing's own doc comment. Every other character-select-adjacent
    // button below guards the same way.
    public void OnSettingsClicked()
    {
        if (LoadingScreen.instance != null && LoadingScreen.instance.IsShowing) return;
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
        if (LoadingScreen.instance != null && LoadingScreen.instance.IsShowing) return;
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
        if (LoadingScreen.instance != null && LoadingScreen.instance.IsShowing) return;
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
        // Covers leaving WITHOUT a scene reload (e.g. Leave Lobby, then join/create a different lobby, all
        // still inside this one Lobby scene load) — Start()'s own reset only runs on an actual scene load,
        // so this is the other half of guaranteeing a guarded button can never stay permanently locked out.
        LoadingScreen.instance?.Hide();
        if (_matchStartWatchdog != null)
        {
            StopCoroutine(_matchStartWatchdog);
            _matchStartWatchdog = null;
        }
        if (_startedFlagRewriteRoutine != null)
        {
            StopCoroutine(_startedFlagRewriteRoutine);
            _startedFlagRewriteRoutine = null;
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
            // Same clean-slate reset as Start()'s own, same serverState/clientState guard (see Start()'s own
            // comment for why unconditional Stop*() calls are actively unsafe, not just wasted effort) —
            // covers leaving WITHOUT a scene reload (e.g. Leave Lobby, then Join a different lobby by code,
            // all still inside this one Lobby scene load) where Start() itself won't run again to catch it.
            if (networkManager.serverState != ConnectionState.Disconnected) networkManager.StopServer();
            if (networkManager.clientState != ConnectionState.Disconnected) networkManager.StopClient();
        }
        _expectedPlayerCount = 0;
        _joinedPlayerIds.Clear();
        _expectedSteamIds.Clear();
        _joinedSteamIds.Clear();
        GameStartData.HostColorAssignments.Clear();
        characterSelectPanel.SetActive(false);
        purrLobbyPanel.SetActive(false);
        mainMenuPanel.SetActive(true);
    }

    private void OnRoomUpdated(Lobby lobby)
    {
        if (characterSelectPanel.activeInHierarchy)
            CharacterSelectUI.instance?.RefreshFromLobby(lobby);

        // REMOVED (2026-08-05): this used to shrink _expectedPlayerCount and re-check TryLoadGameScene the
        // instant the Steam lobby's member count dropped below it, so a host wouldn't wait forever for a
        // join that could now never come. Letting the 20s watchdog (AbortStuckMatchStart) be the only path
        // that ever concludes "we're not getting everyone" is simpler and avoids reacting to a member-count
        // dip that might just be a transient reconnect, even though it now takes up to 20s to react instead
        // of being instant.

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

        // This is the exact snapshot _joinedSteamIds gets compared against in AbortStuckMatchStart to decide
        // whether a rebuild is needed — worth keeping in the log for that reason alone, not just the
        // (already-confirmed-fixed) premature-start race this originally diagnosed.
        Debug.Log($"[LobbySceneController] OnAllReady: capturing _expectedPlayerCount={_expectedPlayerCount} from members [{string.Join(", ", lobby.Members.ConvertAll(m => $"{m.DisplayName}:{m.Id}"))}].");

        // Gated behind lockLobbyOnMatchStart (default on) rather than removed outright — locking the
        // lobby the moment a match starts is the right long-term behavior (stops randoms from
        // Join-by-code'ing into a match already in progress), but it's also the one thing currently
        // stopping a dropped player from rejoining the SAME match via LobbySceneController.OnRoomUpdated's
        // existing "lobby says Started == True, I'm not yet a client" reconnect path. Flip this off in
        // the Inspector to test reconnection without permanently changing the normal-case behavior.
        if (lockLobbyOnMatchStart && ulong.TryParse(lobby.LobbyId, out ulong lockLobbyId))
            SteamMatchmaking.SetLobbyJoinable(new CSteamID(lockLobbyId), false);
        lobbyManager.SetLobbyStarted();
        if (_startedFlagRewriteRoutine != null) StopCoroutine(_startedFlagRewriteRoutine);
        _startedFlagRewriteRoutine = StartCoroutine(RewriteStartedFlagPeriodically());

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
        _joinedSteamIds.Clear();
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
        // _expectedSteamIds' own doc comment. The host's own local (listen-server) player never goes through
        // a real remote Connection to resolve a Steam ID from in the first place — its own Steam ID is just
        // whatever this machine's own GameStartData.LocalUserId already is.
        if (player == networkManager.localPlayer)
        {
            if (ulong.TryParse(GameStartData.LocalUserId, out ulong localSteamId))
                _joinedSteamIds.Add(localSteamId);
        }
        else if (networkManager.TryGetModule<PlayersManager>(true, out var playersManager) &&
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
            if (steamId != 0)
            {
                _joinedSteamIds.Add(steamId);
                Debug.Log($"[LobbySceneController] Player {player} (SteamID {steamId}) connected — {_joinedSteamIds.Count}/{_expectedSteamIds.Count} expected Steam IDs now joined.");
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
        if (_startedFlagRewriteRoutine != null)
        {
            StopCoroutine(_startedFlagRewriteRoutine);
            _startedFlagRewriteRoutine = null;
        }
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
