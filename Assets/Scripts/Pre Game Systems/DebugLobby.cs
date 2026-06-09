#if UNITY_EDITOR
using System.Collections;
using PurrNet;
using PurrNet.Modules;
using UnityEngine;
using UnityEngine.SceneManagement;

public class DebugLobby : MonoBehaviour
{
    [SerializeField] private GameObject startGameButton;

    private void Start()
    {
        GameStartData.LobbySceneName = "Debug Character Select";

        bool isClone = ParrelSync.ClonesManager.IsClone();
        startGameButton.SetActive(!isClone);

        // Already running (returned from game scene) — NetworkManager persisted via DDOL
        if (NetworkManager.main != null && (NetworkManager.main.isServer || NetworkManager.main.isClient))
            return;

        if (isClone)
        {
            GameStartData.LocalUserId = "debug_client";
            StartCoroutine(StartClientDelayed());
        }
        else
        {
            GameStartData.LocalUserId = "debug_host";
            NetworkManager.main.StartServer();
            StartCoroutine(StartClientDelayed());
        }
    }

    private IEnumerator StartClientDelayed()
    {
        yield return new WaitForSeconds(1f);
        NetworkManager.main.StartClient();
    }

    public void SelectCharacter(int index)
    {
        GameStartData.LocalCharacterIndex = index;
    }

    public void StartGame()
    {
        if (!NetworkManager.main.isServer) return;
        if (LoadingScreen.instance != null) LoadingScreen.instance.Show();
        NetworkManager.main.sceneModule.LoadSceneAsync("Networked Game Scene", new PurrSceneSettings
        {
            mode = LoadSceneMode.Single,
            isPublic = true
        });
    }
}
#endif
