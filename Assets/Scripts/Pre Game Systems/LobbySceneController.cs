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

    private int _expectedPlayerCount;
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
    }

    private void OnRoomJoined(Lobby lobby)
    {
        if (_initializedForLobby) return;
        _initializedForLobby = true;

        GameStartData.LocalUserId = lobbyManager.CurrentProvider.GetLocalUserIdAsync().Result;
        GameStartData.LocalCharacterIndex = 0;
        lobbyManager.SetLobbyData($"char_{GameStartData.LocalUserId}", "0");
        ShowCharacterSelect();
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
            if (networkManager.isServer)
            {
                networkManager.sceneModule.LoadSceneAsync("Networked Game Scene", new PurrSceneSettings
                {
                    mode = LoadSceneMode.Single,
                    isPublic = true
                });
            }
            else
            {
                networkManager.StartServer();
                networkManager.onPlayerJoined += OnPurrNetPlayerJoined;
                StartCoroutine(StartClientDelayed());
            }
        }
        else if (!networkManager.isClient)
        {
            StartCoroutine(StartClientDelayed());
        }
    }

    private IEnumerator StartClientDelayed()
    {
        yield return new WaitForSeconds(1f);
        networkManager.StartClient();
    }

    private void OnPurrNetPlayerJoined(PlayerID player, bool isReconnect, bool asServer)
    {
        if (!asServer) return;
        if (networkManager.playerCount < _expectedPlayerCount) return;

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
