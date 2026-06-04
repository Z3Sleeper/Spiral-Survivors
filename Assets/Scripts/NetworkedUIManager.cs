using System.Threading;
using PurrNet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class NetworkedUIManager : NetworkIdentity
{
    public static NetworkedUIManager instance;

    [Header("Level Up Panel")]
    [SerializeField] private GameObject levelUpScreen;
    [SerializeField] private Button continueButton;
    [SerializeField] private TMP_Text timerText;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;

        levelUpScreen.SetActive(false);
    }

    private void Start()
    {
        continueButton.onClick.AddListener(OnContinueClicked);
    }

    private void OnContinueClicked()
    {
        continueButton.interactable = false;
        if (localPlayer.HasValue)
            GameManager.instance.ConfirmReadyRpc(localPlayer.Value);
    }

    public void ShowLevelUpScreen(bool showTimer)
    {
        levelUpScreen.SetActive(true);
        continueButton.interactable = true;

        if (showTimer)
            timerText.text = "Time Remaining: 30s";
        else
            timerText.text = "";
    }

    public void HideLevelUpScreen()
    {
        levelUpScreen.SetActive(false);
    }

    public void UpdateTimer(int secondsRemaining)
    {
        if (timerText != null)
            if (networkManager.playerCount >= 2)
                timerText.text = "Time Remaining: " + secondsRemaining.ToString() + "s";
            else
                timerText.text = "";
    }
}