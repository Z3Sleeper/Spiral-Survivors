using PurrNet.Prediction;

// Executioner BigAxe-specific augment (2026-10-07). This augment's own .asset must set requiredAbilities =
// [ExeBigAxe] — scripts-only convention, that part is Editor-side, not here.
//
// +100% to BigAxe's own Weapon Range buff amount ONLY — Execute Threshold/Crit Chance/Knockback/Attack
// Speed are untouched. Adds directly into ExeBigAxeAbilityPD.rangeMultiplierBonus (0f neutral default),
// which itself adds onto buffStrengthMultiplier at the point weaponRangeTotalPercent is scaled — a
// deliberately ADDITIVE relationship with ExeBigAxeBuffAugment's own multiplier, per the explicit
// "should stack additively with the other buff augment" request (picking both: buffStrengthMultiplier(2x
// default) + rangeMultiplierBonus(1x default) = 3x Range, not the 4x a separate multiplicative factor
// would have produced). See ExeBigAxeAbilityPD.rangeMultiplierBonus's own doc comment for the full worked
// example.
public class ExeBigAxeRangeAugment : AugmentEffectPD<ExeBigAxeRangeAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // "Percent" suffix at the END — AugmentDescriptionFormatter auto-*100s it for description tokens (1f ->
    // "100%"). Directly IS what gets added to rangeMultiplierBonus below.
    public float rangeBonusPercent = 1f;

    protected override void OnApply()
    {
        var bigAxe = Stats.GetComponent<ExeBigAxeAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ExeBigAxeAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (bigAxe == null) return;
        bigAxe.rangeMultiplierBonus += rangeBonusPercent;
    }
}
