using System.Collections;
using PurrLobby;
using PurrNet;
using PurrNet.Modules;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LobbySceneController : MonoBehaviour
{
    [SerializeField] private LobbyManager lobbyManager;
    [SerializeField] private NetworkManager networkManager;
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject purrLobbyPanel;
    [SerializeField] private GameObject characterSelectPanel;
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

        if (lobbyManager.CurrentLobby.IsValid)
        {
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

    public void OnMultiplayerClicked()
    {
        mainMenuPanel.SetActive(false);
        purrLobbyPanel.SetActive(true);
        viewManager.OnLeaveBrowseClicked();
    }

    private void OnRoomJoined(Lobby lobby)
    {
        if (_initializedForLobby) return;
        _initializedForLobby = true;

        GameStartData.LocalUserId = lobbyManager.CurrentProvider.GetLocalUserIdAsync().Result;
        GameStartData.LocalCharacterIndex = 0;
        lobbyManager.SetLobbyData($"char_{GameStartData.LocalUserId}", "0");
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
        Debug.Log($"[OnAllReady] Members: {lobby.Members.Count}, IsOwner: {lobby.IsOwner}, isServer={networkManager.isServer}, isClient={networkManager.isClient}, playerCount={networkManager.playerCount}");

        if (lobby.IsOwner)
        {
            if (!networkManager.isServer)
                networkManager.StartServer();
            if (!networkManager.isClient)
                StartCoroutine(StartClientDelayed());

            _purrNetPlayersJoined = networkManager.playerCount;
            Debug.Log($"[OnAllReady] seeding _purrNetPlayersJoined={_purrNetPlayersJoined}, expected={_expectedPlayerCount}");
            networkManager.onPlayerJoined += OnPurrNetPlayerJoined;
            TryLoadGameScene();
        }
        else if (!networkManager.isClient)
        {
            StartCoroutine(StartClientDelayed());
        }
    }

    private IEnumerator StartClientDelayed()
    {
        Debug.Log("[StartClientDelayed] waiting...");
        yield return new WaitForSeconds(1f);
        Debug.Log("[StartClientDelayed] calling StartClient");
        networkManager.StartClient();
    }

    private void OnPurrNetPlayerJoined(PlayerID player, bool isReconnect, bool asServer)
    {
        if (!asServer) return;
        _purrNetPlayersJoined++;
        Debug.Log($"[OnPurrNetPlayerJoined] joined={_purrNetPlayersJoined}, expected={_expectedPlayerCount}");
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
