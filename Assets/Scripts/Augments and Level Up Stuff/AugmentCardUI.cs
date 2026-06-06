using PurrNet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class AugmentCardUI : MonoBehaviour
{
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text descriptionText;
    [SerializeField] private Image augmentImage;
    [SerializeField] private Button pickButton;

    private int _cardIndex;
    private PlayerID _player;

    public void Setup(StatAugmentPair pair, int cardIndex, PlayerID player)
    {
        _cardIndex = cardIndex;
        _player = player;
        pickButton.interactable = true; // add this

        titleText.text = $"{pair.statA.displayName} & {pair.statB.displayName}";
        descriptionText.text = $"{pair.statA.prependText}{pair.statA.value}{pair.statA.appendText} {pair.statA.displayName}\n{pair.statB.prependText}{pair.statB.value}{pair.statB.appendText} {pair.statB.displayName}";
        
        pickButton.onClick.RemoveAllListeners();
        pickButton.onClick.AddListener(OnPicked);
    }

    private void OnPicked()
    {
        pickButton.interactable = false;
        AugmentManager.instance.PickAugmentRpc(_player, _cardIndex);
        NetworkedUIManager.instance.HideAllCards();
    }
}