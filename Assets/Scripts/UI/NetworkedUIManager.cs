using PurrNet;
using TMPro;
using UnityEngine;

public class NetworkedUIManager : NetworkIdentity
{
    public static NetworkedUIManager instance;

    [SerializeField] private GameObject gameOverScreen;
    [SerializeField] private TextMeshProUGUI gameOverReadyText;
    [SerializeField] private GameObject winScreen;
    [SerializeField] private TextMeshProUGUI winReadyText;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        gameOverScreen.SetActive(false);
        winScreen.SetActive(false);
    }

    public void ShowGameOverScreen()
    {
        gameOverScreen.SetActive(true);
        UpdateReadyText(0, networkManager.playerCount);
    }

    public void UpdateReadyText(int ready, int total)
    {
        gameOverReadyText.text = $"({ready}/{total} Players Ready)";
        if (winReadyText != null)
            winReadyText.text = $"({ready}/{total} Players Ready)";
    }

    public void ShowWinScreen()
    {
        winScreen.SetActive(true);
        UpdateReadyText(0, networkManager.playerCount);
    }

    public void OnReturnToLobbyClicked()
    {
        GameManager.instance.ConfirmReturnToLobbyRpc(localPlayer.Value);
    }
}