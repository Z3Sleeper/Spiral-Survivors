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
    [SerializeField] private AudioClip pickSound;

    private int _cardIndex;
    private PlayerID _player;

    public void StatAugmentSetup(StatAugmentPair pair, int cardIndex, PlayerID player)
    {
        _cardIndex = cardIndex;
        _player = player;
        pickButton.interactable = true; // add this

        augmentImage.sprite = statAugmentSprite;
        titleText.text = $"{pair.statA.ColoredName} & {pair.statB.ColoredName}";
        descriptionText.text = $"{pair.statA.Colored($"{pair.statA.prependText}{pair.statA.value}{pair.statA.appendText} {pair.statA.displayName}")}\n{pair.statB.Colored($"{pair.statB.prependText}{pair.statB.value}{pair.statB.appendText} {pair.statB.displayName}")}";
        
        pickButton.onClick.RemoveAllListeners();
        pickButton.onClick.AddListener(OnPicked);
    }

    public void AugmentSetup(AugmentData augment, int cardIndex, PlayerID player)
    {
        _cardIndex = cardIndex;
        _player = player;
        pickButton.interactable = true;
        augmentImage.sprite = augment.icon;
        titleText.text = augment.displayName;
        descriptionText.text = augment.description;
        pickButton.onClick.RemoveAllListeners();
        pickButton.onClick.AddListener(OnPicked);
    }

    private void OnPicked()
    {
        SFXManager.instance?.Play(pickSound, Vector3.zero);
        LocalUIManager.instance.HideAllCards();
        AugmentManager.instance.PickAugmentRpc(_player, _cardIndex);
    }
}