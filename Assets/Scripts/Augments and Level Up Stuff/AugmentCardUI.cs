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
    private float _luckMultiplierA = 1f;
    private float _luckMultiplierB = 1f;

    // stats is optional (same convention as AugmentSetup) — pass the picking player's own live
    // CharacterStatsPD so LuckRolledLevelUpStatsAugment can be detected; leave null for display-only
    // contexts with no live character to check.
    public void StatAugmentSetup(StatAugmentPair pair, int cardIndex, CharacterStatsPD stats = null)
    {
        _cardIndex = cardIndex;
        pickButton.interactable = true;

        // Rolled ONCE per card (not once per stat) and cached, never re-rolled — the roll shown on the card
        // is exactly the roll that gets applied on pick, threaded through OnPicked -> RequestPick -> Input ->
        // SubmitPick -> PlayerPick, all the way to CharacterStatsPD.ApplyStatAugmentPick. Both stats on the
        // card share this single roll (not independent per-stat rolls) — this is a "how lucky was this pick"
        // roll, not two separate ones. Uses UnityEngine.Random, NOT GameRandom: this runs in purely local,
        // non-deterministic UI-display code on one client only, and GameRandom is the shared, order-sensitive
        // PD stream every machine must draw from identically — rolling it here would desync every other
        // client's subsequent draws.
        bool hasLuckRoll = stats != null && stats.luckRolledLevelUpStats;
        float luckMultiplier = 1f;
        bool isHighroll = false;
        // HighrollBonus is scaled down (not passed raw) — that stat is now a damage-scope 300%-at-base
        // replacement multiplier (see WeaponDamageMath.ApplyCritAndHighroll), way too strong to hand a stat
        // pick unscaled; see LuckRolledLevelUpStatsAugment's own highrollBonusScale doc comment.
        if (hasLuckRoll)
        {
            luckMultiplier = WeaponDamageMath.RollLuckMultiplier(stats.LuckChance, stats.LuckFloor, stats.LuckCeiling, stats.Highroll, stats.HighrollBonus * stats.luckRolledLevelUpStatsHighrollScale, UnityEngine.Random.Range, out isHighroll);
            // Dampens the whole roll's swing down to (by default) half — see LuckRolledLevelUpStatsAugment's
            // own effectiveness doc comment.
            luckMultiplier = 1f + (luckMultiplier - 1f) * stats.luckRolledLevelUpStatsEffectiveness;
        }
        _luckMultiplierA = luckMultiplier;
        _luckMultiplierB = luckMultiplier;

        _effectiveValueA = pair.statA.value * ProgressionManager.GetStatMultiplier(pair.statA.statType) * _luckMultiplierA;
        _effectiveValueB = pair.statB.value * ProgressionManager.GetStatMultiplier(pair.statB.statType) * _luckMultiplierB;

        // LuckRolledLevelUpStatsAugment's own roll, surfaced on the card so the player can see how lucky
        // this particular card is before picking — "!" marks a Highroll (the roll snapped to LuckCeiling and
        // got HighrollBonus applied on top, see RollLuckMultiplier), otherwise just the plain rolled percent.
        // Colored with the game-wide Luck color (#00FF87) for a positive roll/Highroll, red for a negative
        // one. FormatLiveNumber gives the same tiered decimal precision (whole/1dp/2dp by magnitude) every
        // other live-value token in this codebase uses.
        string luckPrefix = "";
        if (hasLuckRoll)
        {
            float percent = (luckMultiplier - 1f) * 100f;
            string magnitude = AugmentDescriptionFormatter.FormatLiveNumber(Mathf.Abs(percent));
            string luckColor = percent >= 0f ? "#00FF87" : "red";
            luckPrefix = isHighroll ? $"<color={luckColor}>(+{magnitude}%!)</color>\n" : percent >= 0f ? $"<color={luckColor}>(+{magnitude}%)</color>\n" : $"<color={luckColor}>(-{magnitude}%)</color>\n";
        }

        augmentImage.sprite = statAugmentSprite;
        titleText.text = $"{luckPrefix}{pair.statA.ColoredName} & {pair.statB.ColoredName}";
        descriptionText.text = $"{pair.statA.Colored($"{pair.statA.prependText}{AugmentDescriptionFormatter.FormatLiveNumber(_effectiveValueA)}{pair.statA.appendText} {pair.statA.displayName}")}\n{pair.statB.Colored($"{pair.statB.prependText}{AugmentDescriptionFormatter.FormatLiveNumber(_effectiveValueB)}{pair.statB.appendText} {pair.statB.displayName}")}";

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
        LocalUIManagerPD.instance.RequestPick(_cardIndex, _luckMultiplierA, _luckMultiplierB);
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