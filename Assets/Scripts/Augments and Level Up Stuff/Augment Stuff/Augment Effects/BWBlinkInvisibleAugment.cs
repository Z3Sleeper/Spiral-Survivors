using PurrNet.Prediction;

// Bladeweaver BWBlink-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [BWBlink] — scripts-only convention, that part is Editor-side, not here.
//
// Pattern B switch: sets BWBlinkAbilityPD.grantInvisibility true, which makes ApplyBuffAndCooldown() call
// CharacterStatsPD.GrantInvisibility for this window's own EffectiveDuration — see that field's own doc
// comment for why there's no revert needed (GrantInvisibility is its own "longest contribution wins"
// floor-timer that expires naturally, same contract RogueVanishAbilityPD/ArcherFocusFireAbilityPD's own
// invisibility grants already rely on). Fires on BOTH a real Activate() cast and the Blade-Dance-return
// auto-cast — ApplyBuffAndCooldown() is the one shared method both route through, so this doesn't need its
// own separate hook into either call site. No separate bonus — this augment's whole value is the
// invisibility itself, same single-effect shape ArcherFocusFireInvisibleAugment/
// ArcherArrowRainSelfTargetAugment use.
public class BWBlinkInvisibleAugment : AugmentEffectPD<BWBlinkInvisibleAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    protected override void OnApply()
    {
        var blink = Stats.GetComponent<BWBlinkAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with BWBlinkAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (blink == null) return;

        blink.grantInvisibility = true;
    }
}
