using PurrNet.Prediction;

// Bladeweaver BladeDance-specific augment (2026-10-06). This augment's own .asset must set
// requiredAbilities = [BWBladeDance] — scripts-only convention, that part is Editor-side, not here.
//
// +100% Max Duration — ADDITIVE onto BWBladeDanceAbilityPD's own live durationMultiplier field (1f
// neutral default), not a destructive multiply on holdDuration itself — Stuart's own standing preference
// against multiplicative stacking for this kind of "+X% to Y" augment effect, same shape
// ArcherArrowRainDurationAugment's own durationMultiplier uses. EffectiveHoldDuration
// (holdDuration * durationMultiplier) is what the ability's Outbound->Hold transition and
// ActiveTimeDuration/ActiveTimeRemaining all actually read.
public class BWBladeDanceDurationAugment : AugmentEffectPD<BWBladeDanceDurationAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // "Percent" suffix at the END — AugmentDescriptionFormatter auto-*100s it for description tokens (1f ->
    // "100%"). Directly IS what gets added to durationMultiplier below.
    public float durationBonusPercent = 1f;

    protected override void OnApply()
    {
        var bladeDance = Stats.GetComponent<BWBladeDanceAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with BWBladeDanceAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (bladeDance == null) return;
        bladeDance.durationMultiplier += durationBonusPercent;
    }
}
