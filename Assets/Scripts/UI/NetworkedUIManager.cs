using System.Collections.Generic;
using PurrNet;
using TMPro;
using UnityEngine;

public class NetworkedUIManager : NetworkIdentity
{
    public static NetworkedUIManager instance;

    [Header("Level Up Panel")]
    [SerializeField] private GameObject levelUpScreen;
    [SerializeField] private TMP_Text timerText;
    [SerializeField] private AugmentCardUI cardPrefab;
    [SerializeField] private Transform cardContainer;
    private List<AugmentCardUI> _cardPool = new List<AugmentCardUI>();

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        levelUpScreen.SetActive(false);
    }

    public void ShowLevelUpScreen(bool showTimer)
    {
        levelUpScreen.SetActive(true);
        timerText.text = showTimer ? "Time Remaining: 30s" : "";

        HideAllCards();

        if (localPlayer.HasValue)
        {
            var options = AugmentManager.instance.GetOptionsForPlayer(localPlayer.Value);
            if (options != null)
            {
                for (int i = 0; i < options.Count; i++)
                    GetCard(i).Setup(options[i], i, localPlayer.Value);
            }
        }
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

    private void HideAllCards()
    {
        foreach (var card in _cardPool)
            card.gameObject.SetActive(false);
    }
}