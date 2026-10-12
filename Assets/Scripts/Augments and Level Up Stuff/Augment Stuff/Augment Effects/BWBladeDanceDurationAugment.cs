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
//
// Also +100% energy generation for the whole dance window (2026-10-10, buff pass — this and
// BWBladeDanceStunAugment were both underperforming) — additive onto BWBladeDanceAbilityPD's own
// energyGenerationBonusPercent (0f neutral default), applied/reverted at the dance's own Activate()/
// EndDance() boundary, same shape attackSpeedPercentPerHaste already uses on that ability. This also
// doubles as the signal BWBladeDanceStunAugment's own synergy point reads to detect "is this augment also
// picked" — see that field's own doc comment for why a shared ability-owned knob, not a direct
// cross-augment reference.
public class BWBladeDanceDurationAugment : AugmentEffectPD<BWBladeDanceDurationAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // "Percent" suffix at the END — AugmentDescriptionFormatter auto-*100s it for description tokens (1f ->
    // "100%"). Directly IS what gets added to durationMultiplier below.
    public float durationBonusPercent = 1f;
    // Directly IS what gets added to energyGenerationBonusPercent below.
    public float energyGenerationBonusPercent = 1f;

    protected override void OnApply()
    {
        var bladeDance = Stats.GetComponent<BWBladeDanceAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with BWBladeDanceAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (bladeDance == null) return;
        bladeDance.durationMultiplier += durationBonusPercent;
        bladeDance.energyGenerationBonusPercent += energyGenerationBonusPercent;
    }
}
