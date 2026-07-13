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
    private float _effectiveValueA;
    private float _effectiveValueB;

    public void StatAugmentSetup(StatAugmentPair pair, int cardIndex)
    {
        _cardIndex = cardIndex;
        pickButton.interactable = true;

        _effectiveValueA = pair.statA.value * ProgressionManager.GetStatMultiplier(pair.statA.statType);
        _effectiveValueB = pair.statB.value * ProgressionManager.GetStatMultiplier(pair.statB.statType);

        augmentImage.sprite = statAugmentSprite;
        titleText.text = $"{pair.statA.ColoredName} & {pair.statB.ColoredName}";
        descriptionText.text = $"{pair.statA.Colored($"{pair.statA.prependText}{_effectiveValueA}{pair.statA.appendText} {pair.statA.displayName}")}\n{pair.statB.Colored($"{pair.statB.prependText}{_effectiveValueB}{pair.statB.appendText} {pair.statB.displayName}")}";

        pickButton.onClick.RemoveAllListeners();
        pickButton.onClick.AddListener(OnPicked);
    }

    private void OnPicked()
    {
        SFXManager.instance?.Play(pickSound, Vector3.zero);
        LocalUIManagerPD.instance.HideAllCards();
        LocalUIManagerPD.instance.RequestPick(_cardIndex);
    }
}