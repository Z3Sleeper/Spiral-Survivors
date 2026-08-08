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

    // stats is optional — pass the picking player's own live CharacterStatsPD for a real level-up card so
    // {Token}-style placeholders in the description resolve to actual computed values; leave it null for
    // display-only contexts (Character Select, Encyclopedia) where there's no live character to read from.
    // See AugmentDescriptionFormatter's own doc comment for the full resolution contract.
    public void AugmentSetup(AugmentData augment, int cardIndex, CharacterStatsPD stats = null)
    {
        _cardIndex = cardIndex;
        pickButton.interactable = true;

        augmentImage.sprite = augment.icon;
        titleText.text = augment.displayName;
        descriptionText.text = AugmentDescriptionFormatter.Format(augment, stats);

        pickButton.onClick.RemoveAllListeners();
        pickButton.onClick.AddListener(OnPicked);
    }

    private void OnPicked()
    {
        SFXManager.instance?.Play(pickSound, Vector3.zero);
        LocalUIManagerPD.instance.HideAllCards();
        LocalUIManagerPD.instance.RequestPick(_cardIndex);
    }

    // For display-only contexts (e.g. an encyclopedia/browse list) where OnPicked's LocalUIManagerPD.instance
    // call wouldn't have anything valid to hit — this only touches interactability, never the onClick wiring
    // itself, so a real pick flow calling AugmentSetup/StatAugmentSetup afterward is unaffected.
    public void SetInteractive(bool interactive) => pickButton.interactable = interactive;

    // For showing a class's IAbilityPD in the same card layout as its passive augments (character select's
    // detail panel) — there's no AugmentData asset backing an ability, so this takes the raw display fields
    // directly instead. Always paired with SetInteractive(false) by the caller, same as the passive cards.
    // Takes the ability itself (not just its already-extracted Description string) so
    // AugmentDescriptionFormatter has an object to resolve {Token}-style placeholders against — same
    // optional-stats convention as AugmentSetup (null for display-only contexts like Character Select,
    // where there's no live character to preview values off of).
    public void AbilitySetup(string title, IAbilityPD ability, Sprite icon, CharacterStatsPD stats = null)
    {
        pickButton.onClick.RemoveAllListeners();
        augmentImage.sprite = icon;
        titleText.text = title;
        descriptionText.text = AugmentDescriptionFormatter.Format(ability.Description, ability, stats);
    }
}