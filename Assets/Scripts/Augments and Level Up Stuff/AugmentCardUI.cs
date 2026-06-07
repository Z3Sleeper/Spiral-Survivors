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
    [SerializeField] private Sprite statAugmentSprite;

    private int _cardIndex;
    private PlayerID _player;

    public void StatAugmentSetup(StatAugmentPair pair, int cardIndex, PlayerID player)
    {
        _cardIndex = cardIndex;
        _player = player;
        pickButton.interactable = true; // add this

        augmentImage.sprite = statAugmentSprite;
        titleText.text = $"{pair.statA.displayName} & {pair.statB.displayName}";
        descriptionText.text = $"{pair.statA.prependText}{pair.statA.value}{pair.statA.appendText} {pair.statA.displayName}\n{pair.statB.prependText}{pair.statB.value}{pair.statB.appendText} {pair.statB.displayName}";
        
        pickButton.onClick.RemoveAllListeners();
        pickButton.onClick.AddListener(OnPicked);
    }

    private void OnPicked()
    {
        LocalUIManager.instance.HideAllCards();
        AugmentManager.instance.PickAugmentRpc(_player, _cardIndex);
    }
}