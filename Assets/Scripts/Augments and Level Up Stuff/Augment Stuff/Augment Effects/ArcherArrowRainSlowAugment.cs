using PurrNet.Prediction;

// Archer ArrowRain-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [ArcherArrowRain] — scripts-only convention, that part is Editor-side, not here.
//
// Two permanent, one-shot effects applied directly to ArcherArrowRainAbilityPD at OnApply(), both additive
// onto the ability's own live multiplier fields — same shape as ArcherArrowRainGrowthAugment/
// ArcherArrowRainDurationAugment, so this composes correctly alongside those rather than compounding
// (augments are never granted more than once each — see [[project_augments_never_duplicate_granted]]):
//
// 1. +100% slow — ADDITIVE onto slowPercentMultiplier (doubles slowPercent at this augment's own default,
//    e.g. 35% slower -> 70% slower). EffectiveSlowPercent (slowPercent * slowPercentMultiplier) is what
//    SpawnChain/LiveSlowRatio actually read — see slowPercentMultiplier's own doc comment for why no clamp
//    is needed here (EnemyBasePD/CharacterStatsPD's own Slow() already floors the max slow downstream).
// 2. +25% damage — ADDITIVE onto damageMultiplier, same mechanism ArcherArrowRainGrowthAugment/
//    ArcherArrowRainDurationAugment's own damage halves already use (independently duplicated field, not
//    shared — three separate augments that each happen to grant +25%).
public class ArcherArrowRainSlowAugment : AugmentEffectPD<ArcherArrowRainSlowAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // "Percent" suffix at the END — see ArcherArrowRainGrowthAugment.growthPerSecondPercent's own doc
    // comment for why the suffix position matters (AugmentDescriptionFormatter's auto-*100 only fires on an
    // EndsWith("Percent") check). Directly IS what gets added to slowPercentMultiplier below.
    public float slowBonusPercent = 1f;

    // Same field/value as ArcherArrowRainGrowthAugment/ArcherArrowRainDurationAugment's own
    // damageBonusPercent — intentionally duplicated here rather than shared, since these are three
    // independent augments that each happen to grant the same +25% damage bonus.
    public float damageBonusPercent = 0.25f;

    protected override void OnApply()
    {
        var arrowRain = Stats.GetComponent<ArcherArrowRainAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ArcherArrowRainAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (arrowRain == null) return;

        arrowRain.slowPercentMultiplier += slowBonusPercent;
        arrowRain.damageMultiplier += damageBonusPercent;
    }
}
