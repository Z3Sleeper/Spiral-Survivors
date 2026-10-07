using PurrNet.Prediction;

// Archer FocusFire-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [ArcherFocusFire] — scripts-only convention, that part is Editor-side, not here.
//
// Pattern B switch: sets ArcherFocusFireAbilityPD.grantInvisibility true, which makes Activate() call
// CharacterStatsPD.GrantInvisibility for this window's own duration — see that field's own doc comment for
// why there's no revert needed (GrantInvisibility is its own "longest contribution wins" floor-timer that
// expires naturally, same contract RogueVanishAbilityPD's own invisibility grant already relies on). No
// separate bonus — this augment's whole value is the invisibility itself, same single-effect shape
// ArcherArrowRainSelfTargetAugment uses.
public class ArcherFocusFireInvisibleAugment : AugmentEffectPD<ArcherFocusFireInvisibleAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    protected override void OnApply()
    {
        var focusFire = Stats.GetComponent<ArcherFocusFireAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ArcherFocusFireAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (focusFire == null) return;

        focusFire.grantInvisibility = true;
    }
}
