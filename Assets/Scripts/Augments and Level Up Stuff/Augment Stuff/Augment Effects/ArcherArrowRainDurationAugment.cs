using PurrNet.Prediction;

// Archer ArrowRain-specific augment (2026-10-05). This augment's own .asset must set requiredAbilities =
// [ArcherArrowRain] — scripts-only convention, that part is Editor-side, not here.
//
// +50% duration — ADDITIVE onto the ability's own live durationMultiplier field (see its own doc comment
// for why — additive, not a destructive one-shot multiply, so this composes correctly alongside a
// DIFFERENT ArrowRain-specific duration augment rather than compounding; augments are never granted more
// than once each, so same-augment repeat-pick stacking was never the concern — see
// [[project_augments_never_duplicate_granted]]). EffectiveDuration (duration * durationMultiplier) is what
// every tick-count/remainder/VFX-duration/growth-cap calculation in the ability actually reads, so the
// longer rain automatically gets however many extra ticks that implies (more total damage, for free) AND
// automatically extends ArcherArrowRainGrowthAugment's own growth window if that's also equipped — no
// interaction code needed between the two augments.
//
// No separate damage bonus (2026-10-06, explicit design call — "the duration is a big enough buff
// already") — unlike ArcherArrowRainGrowthAugment/ArcherArrowRainSlowAugment, which each also fold in a
// flat +25% damage.
public class ArcherArrowRainDurationAugment : AugmentEffectPD<ArcherArrowRainDurationAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // "Percent" suffix at the END — see ArcherArrowRainGrowthAugment.growthPerSecondPercent's own doc
    // comment for why the suffix position matters (AugmentDescriptionFormatter's auto-*100 only fires on an
    // EndsWith("Percent") check). Directly IS what gets added to durationMultiplier below.
    public float durationBonusPercent = 0.5f;

    protected override void OnApply()
    {
        var arrowRain = Stats.GetComponent<ArcherArrowRainAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ArcherArrowRainAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (arrowRain == null) return;

        arrowRain.durationMultiplier += durationBonusPercent;
    }
}
