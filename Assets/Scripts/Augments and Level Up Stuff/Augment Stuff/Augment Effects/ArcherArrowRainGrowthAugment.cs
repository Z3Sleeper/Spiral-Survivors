using PurrNet.Prediction;

// Archer ArrowRain-specific augment (2026-10-05). This augment's own .asset must set requiredAbilities =
// [ArcherArrowRain] — scripts-only convention, that part is Editor-side, not here. Augments are never
// granted more than once each (140+ unique augments total, no duplicates in the pool), so stacking here
// only ever means "this augment plus a DIFFERENT ArrowRain-specific damage augment," never a repeat pick of
// this same one.
//
// Two permanent, one-shot effects applied directly to ArcherArrowRainAbilityPD at OnApply():
//
// 1. Makes the rain's own AoE grow from its cast-time radius by growthPerSecondPercent (20%/sec default,
//    so at the ability's own base 5s duration it ends roughly double its starting size) — ADDITIVE onto the
//    ability's own [HideInInspector] growthPercentPerSecond field (Pattern B — the ability's own Simulate()
//    does the actual per-tick radius math; see its own doc comment for why this stacks additively with
//    Stats.AreaEffectSize rather than being yet another separate multiplier).
// 2. +25% damage — ADDITIVE onto the ability's own [HideInInspector] damageMultiplier field (1f neutral
//    default), NOT a destructive one-shot multiply on strengthPerSecond itself (that was this augment's
//    first draft — caught in review that it'd compound rather than add alongside any future second
//    ArrowRain-specific damage augment: 1.25*1.25=1.5625x instead of the intended 1.5x for two "+25%"
//    augments). strengthPerSecond stays the pure authored base value; TickDamagePercent (and everything
//    derived from it — TotalDamagePercent/LiveDamageRatio/SpawnChain's own damagePercent) reads
//    strengthPerSecond * damageMultiplier live, so this augment's bonus shows up everywhere automatically.
public class ArcherArrowRainGrowthAugment : AugmentEffectPD<ArcherArrowRainGrowthAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, and named with the literal "Percent" suffix at the END (not "growthPercentPerSecond", which
    // buries it mid-name) — so AugmentDescriptionFormatter's reflection can resolve {growthPerSecondPercent}
    // AND get its auto-*100 percent formatting, which only fires on an EndsWith("Percent") check (same
    // "PerUnit vs Percent" trap OnHitDamageDistanceAugment's own perUnitDistancePercent rename fixed for the
    // identical reason — caught in the same pass). The ability's own mirror field stays named
    // growthPercentPerSecond ([HideInInspector], never a description token, so the suffix position there
    // doesn't matter) — only this augment's own copy needs it at the end.
    public float growthPerSecondPercent = 0.2f;

    // The real tunable damage bonus (+25% default) — directly IS the description token value ({25%}), and
    // directly IS what gets added to the ability's damageMultiplier below. No separate "multiplier" field
    // (1.25x) needed now that the ability applies this additively rather than this augment multiplying
    // strengthPerSecond itself — see class doc comment for why that first draft was wrong.
    public float damageBonusPercent = 0.25f;

    protected override void OnApply()
    {
        var arrowRain = Stats.GetComponent<ArcherArrowRainAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ArcherArrowRainAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (arrowRain == null) return;

        arrowRain.growthPercentPerSecond += growthPerSecondPercent;
        arrowRain.damageMultiplier += damageBonusPercent;
    }
}
