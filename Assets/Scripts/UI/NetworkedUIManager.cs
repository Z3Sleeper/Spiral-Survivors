using System.Collections.Generic;
using PurrNet;
using TMPro;
using UnityEngine;

public class NetworkedUIManager : NetworkIdentity
{
    public static NetworkedUIManager instance;

    [Header("Level Up Panel")]
    [SerializeField] private GameObject levelUpScreen;
    [SerializeField] private GameObject gameOverScreen;
    [SerializeField] private TMP_Text timerText;
    [SerializeField] private AugmentCardUI cardPrefab;
    [SerializeField] private Transform cardContainer;
    private List<AugmentCardUI> _cardPool = new();

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        levelUpScreen.SetActive(false);
        gameOverScreen.SetActive(false);
    }

    public void HideLevelUpScreen()
    {
        levelUpScreen.SetActive(false);
    }

    public void UpdateTimer(int secondsRemaining)
    {
        if (timerText != null)
            timerText.text = networkManager.playerCount >= 2
                ? "Time Remaining: " + secondsRemaining + "s"
                : "";
    }

    private AugmentCardUI GetCard(int index)
    {
        if (index < _cardPool.Count)
        {
            _cardPool[index].gameObject.SetActive(true);
            return _cardPool[index];
        }
        var card = Instantiate(cardPrefab, cardContainer);
        _cardPool.Add(card);
        return card;
    }

    public void HideAllCards()
    {
        foreach (var card in _cardPool)
            card.gameObject.SetActive(false);
    }

    public void ShowGameOverScreen()
    {
        gameOverScreen.SetActive(true);
    }

    public void OnReturnToLobbyClicked()
    {
        GameManager.instance.ConfirmReturnToLobbyRpc(localPlayer.Value);
    }

    public void ShowLevelUpScreen(List<StatAugmentPair> options)
    {
        levelUpScreen.SetActive(true);
        timerText.text = (networkManager.playerCount > 1) ? "Time Remaining: 30s" : "";
        HideAllCards();
        for (int i = 0; i < options.Count; i++)
            GetCard(i).Setup(options[i], i, localPlayer.Value);
    }
}