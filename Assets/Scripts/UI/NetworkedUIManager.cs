using PurrNet;
using TMPro;
using UnityEngine;

public class NetworkedUIManager : NetworkIdentity
{
    public static NetworkedUIManager instance;

    [SerializeField] private GameObject gameOverScreen;
    [SerializeField] private TextMeshProUGUI gameOverText;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        gameOverScreen.SetActive(false);
    }

    public void ShowGameOverScreen()
    {
        gameOverScreen.SetActive(true);
        UpdateReadyText(0, networkManager.playerCount);
    }

    public void UpdateReadyText(int ready, int total)
    {
        gameOverText.text = $"({ready}/{total} Players Ready)";
    }

    public void OnReturnToLobbyClicked()
    {
        GameManager.instance.ConfirmReturnToLobbyRpc(localPlayer.Value);
    }
}