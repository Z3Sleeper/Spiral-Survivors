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
    private int _purrNetPlayersJoined;
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
        characterSelectPanel.SetActive(false);
        purrLobbyPanel.SetActive(false);
        mainMenuPanel.SetActive(true);
    }

    private void OnRoomUpdated(Lobby lobby)
    {
        if (characterSelectPanel.activeInHierarchy)
            CharacterSelectUI.instance?.RefreshFromLobby(lobby);

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

            _purrNetPlayersJoined = networkManager.playerCount;
            networkManager.onPlayerJoined += OnPurrNetPlayerJoined;
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
        _purrNetPlayersJoined++;
        TryLoadGameScene();
    }

    private void TryLoadGameScene()
    {
        if (_purrNetPlayersJoined < _expectedPlayerCount) return;
        networkManager.onPlayerJoined -= OnPurrNetPlayerJoined;
        networkManager.sceneModule.LoadSceneAsync("Networked Game Scene", new PurrSceneSettings
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
