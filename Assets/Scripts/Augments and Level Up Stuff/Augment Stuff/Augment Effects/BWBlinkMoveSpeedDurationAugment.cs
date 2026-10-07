using PurrNet.Prediction;

// Bladeweaver BWBlink-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [BWBlink] — scripts-only convention, that part is Editor-side, not here.
//
// +100% Move Speed and +100% Duration on Blink's own Move Speed buff — ADDITIVE onto the ability's own
// live moveSpeedMultiplier/durationMultiplier fields (both 1f neutral default), not a destructive multiply
// on moveSpeedPercent/duration themselves — Stuart's own standing preference against multiplicative
// stacking for this kind of "+X% to Y" augment effect. EffectiveMoveSpeedPercent/EffectiveDuration (what
// ApplyBuffAndCooldown's own snapshots actually read) automatically reflect both.
public class BWBlinkMoveSpeedDurationAugment : AugmentEffectPD<BWBlinkMoveSpeedDurationAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // "Percent" suffix at the END on both — AugmentDescriptionFormatter auto-*100s them for description
    // tokens (1f -> "100%"). Directly IS what gets added to the ability's own multiplier fields below.
    public float moveSpeedBonusPercent = 1f;
    public float durationBonusPercent = 1f;

    protected override void OnApply()
    {
        var blink = Stats.GetComponent<BWBlinkAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with BWBlinkAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (blink == null) return;

        blink.moveSpeedMultiplier += moveSpeedBonusPercent;
        blink.durationMultiplier += durationBonusPercent;
    }
}
