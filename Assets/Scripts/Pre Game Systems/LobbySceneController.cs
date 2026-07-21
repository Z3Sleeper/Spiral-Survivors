using System.Collections;
using System.Collections.Generic;
using TMPro;
using PurrLobby;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Steam;
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
    [SerializeField] private GameObject[] upgradeButtons;
    [SerializeField] private ViewManager viewManager;

    [Header("Unlock Popup")]
    [SerializeField] private GameObject unlockPopup;
    [SerializeField] private TextMeshProUGUI unlockPopupText;
    [SerializeField] private AudioClip unlockSound;

    private int _expectedPlayerCount;
    // Deduplicated by PlayerID rather than a raw counter — a flaky connection can retry and register a
    // brand-new PlayerID with the server for what is physically the same reconnecting client (PurrNet mints
    // a fresh PlayerID per completed handshake when host migration/cookies aren't in play), so a plain
    // counter could cross _expectedPlayerCount purely from reconnect noise without every distinct player
    // actually being present.
    private readonly HashSet<PlayerID> _joinedPlayerIds = new();
    private bool _initializedForLobby;
    private bool _connectingToHost;

    private void OnEnable()  => UnlockManager.OnUnlockQueued += OnUnlockQueuedWhileInLobby;
    private void OnDisable() => UnlockManager.OnUnlockQueued -= OnUnlockQueuedWhileInLobby;

    private void OnUnlockQueuedWhileInLobby()
    {
        if (unlockPopup != null && !unlockPopup.activeSelf)
            ShowNextUnlock();
    }

    private void Start()
    {
        if (!networkManager)
            networkManager = NetworkManager.main;

        bool shopUnlocked = ProgressionManager.IsShopUnlocked;
        foreach (var btn in upgradeButtons)
            if (btn != null) btn.SetActive(shopUnlocked);

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

        int assignedColor = CharacterSelectUI.instance?.AssignUniqueColor(lobby) ?? 0;
        GameStartData.LocalColorIndex = assignedColor;
        GameStartData.LocalColor = CharacterSelectUI.instance?.GetColor(assignedColor) ?? UnityEngine.Color.white;

        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
        {
            var steamLobby = new CSteamID(lobbyIdRaw);
            SteamMatchmaking.SetLobbyMemberData(steamLobby, "char", "0");
            SteamMatchmaking.SetLobbyMemberData(steamLobby, "color", assignedColor.ToString());
            SteamMatchmaking.SetLobbyMemberData(steamLobby, "power", ProgressionManager.SpentPoints.ToString());
        }
        CharacterSelectUI.instance?.SetLocalDefault(GameStartData.LocalUserId, 0, assignedColor);
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
        }
        _expectedPlayerCount = 0;
        _joinedPlayerIds.Clear();
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

        if (!lobby.IsOwner && !networkManager.isClient && !_connectingToHost)
        {
            if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
            {
                var steamLobbyId = new CSteamID(lobbyIdRaw);
                string started = SteamMatchmaking.GetLobbyData(steamLobbyId, "Started");
                if (started == "True")
                {
                    _connectingToHost = true;
                    if (networkManager.transport is SteamTransport steamTransport)
                    {
                        var hostSteamId = SteamMatchmaking.GetLobbyOwner(steamLobbyId);
                        steamTransport.address = hostSteamId.m_SteamID.ToString();
                    }
                    StartCoroutine(ConnectToHostWithRetry());
                }
            }
        }
    }

    private void OnAllReady()
    {
        if (!networkManager)
            networkManager = NetworkManager.main;

        if (LoadingScreen.instance != null) LoadingScreen.instance.Show();
        var lobby = lobbyManager.CurrentLobby;
        _expectedPlayerCount = lobby.Members.Count;

        if (lobby.IsOwner)
        {
            if (ulong.TryParse(lobby.LobbyId, out ulong lockLobbyId))
                SteamMatchmaking.SetLobbyJoinable(new CSteamID(lockLobbyId), false);
            lobbyManager.SetLobbyStarted();
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
        else if (!networkManager.isClient && !_connectingToHost)
        {
            _connectingToHost = true;
            if (networkManager.transport is SteamTransport steamTransport &&
                ulong.TryParse(lobby.LobbyId, out var lobbyIdRaw))
            {
                var hostSteamId = SteamMatchmaking.GetLobbyOwner(new CSteamID(lobbyIdRaw));
                steamTransport.address = hostSteamId.m_SteamID.ToString();
            }
            StartCoroutine(ConnectToHostWithRetry());
        }
    }

    private IEnumerator StartClientDelayed(float delay = 1f)
    {
        yield return new WaitForSeconds(delay);
        networkManager.StartClient();
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
                yield break;

            yield return retryWait;
        }

        Debug.LogError("Failed to connect to host after all attempts.");
    }

    private void OnPurrNetPlayerJoined(PlayerID player, bool isReconnect, bool asServer)
    {
        if (!asServer) return;
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
