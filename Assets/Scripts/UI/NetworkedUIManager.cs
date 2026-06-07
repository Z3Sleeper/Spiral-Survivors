using PurrNet;
using UnityEngine;

public class NetworkedUIManager : NetworkIdentity
{
    public static NetworkedUIManager instance;

    [SerializeField] private GameObject gameOverScreen;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        gameOverScreen.SetActive(false);
    }

    public void ShowGameOverScreen()
    {
        gameOverScreen.SetActive(true);
    }

    public void OnReturnToLobbyClicked()
    {
        GameManager.instance.ConfirmReturnToLobbyRpc(localPlayer.Value);
    }
}