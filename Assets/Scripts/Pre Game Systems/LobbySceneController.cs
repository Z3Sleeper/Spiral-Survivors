using System.Collections;
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
    [SerializeField] private GameObject encyclopediaPanel;
    [SerializeField] private GameObject upgradePanel;
    [SerializeField] private ViewManager viewManager;

    private int _expectedPlayerCount;
    private int _purrNetPlayersJoined;
    private bool _initializedForLobby;

    private void Start()
    {
        if (!networkManager)
            networkManager = NetworkManager.main;

        lobbyManager.OnRoomJoined.AddListener(OnRoomJoined);
        lobbyManager.OnRoomLeft.AddListener(OnRoomLeft);
        lobbyManager.OnRoomUpdated.AddListener(OnRoomUpdated);
        lobbyManager.OnAllReady.AddListener(OnAllReady);

        if (LobbyFlags.IsReturningFromGame || lobbyManager.CurrentLobby.IsValid)
        {
            LobbyFlags.IsReturningFromGame = false;
            _initializedForLobby = true;
            ShowCharacterSelect();
            lobbyManager.SetIsReady(GameStartData.LocalUserId, false);
            CharacterSelectUI.instance?.RefreshFromLobby(lobbyManager.CurrentLobby);
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

    public void OnSettingsClicked()
    {
        settingsPanel.SetActive(true);
    }

    public void OnSettingsBackClicked()
    {
        settingsPanel.SetActive(false);
    }

    public void OnUpgradeClicked()
    {
        upgradePanel.SetActive(true);
    }

    public void OnUpgradeBackClicked()
    {
        upgradePanel.SetActive(false);
    }

    public void OnEncyclopediaClicked()
    {
        mainMenuPanel.SetActive(false);
        encyclopediaPanel.SetActive(true);
    }

    public void OnEncyclopediaBackClicked()
    {
        encyclopediaPanel.SetActive(false);
        mainMenuPanel.SetActive(true);
    }

    public void OnExitClicked()
    {
        Application.Quit();
    }

    private void OnRoomJoined(Lobby lobby)
    {
        if (_initializedForLobby) return;
        _initializedForLobby = true;

        GameStartData.LocalUserId = lobbyManager.CurrentProvider.GetLocalUserIdAsync().Result;
        GameStartData.LocalCharacterIndex = 0;
        if (ulong.TryParse(lobby.LobbyId, out ulong lobbyIdRaw))
            SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobbyIdRaw), "char", "0");
        ShowCharacterSelect();
        CharacterSelectUI.instance?.SetLocalDefault(GameStartData.LocalUserId, 0);
        CharacterSelectUI.instance?.RefreshFromLobby(lobby);
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
            lobbyManager.SetLobbyStarted();
            if (!networkManager.isServer)
                networkManager.StartServer();
            if (!networkManager.isClient)
                StartCoroutine(StartClientDelayed());

            _purrNetPlayersJoined = networkManager.playerCount;
            networkManager.onPlayerJoined += OnPurrNetPlayerJoined;
            TryLoadGameScene();
        }
        else if (!networkManager.isClient)
        {
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
