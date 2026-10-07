using PurrNet.Prediction;

// Archer FocusFire-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [ArcherFocusFire] — scripts-only convention, that part is Editor-side, not here.
//
// Grants +moveSpeedBonusPercent Total Move Speed and +knockbackBonusPercent Knockback, but ONLY while
// ArcherFocusFireAbilityPD's own active window is running — same Pattern B "ability owns the no-op fields,
// augment just bumps them" shape as ArcherFocusFireRicochetAugment/ArcherFocusFireMultishotHomingAugment.
// The actual grant/revert pairing lives on the ability itself (moveSpeedPercent/knockbackPercent, applied
// in Activate()/reverted in EndFocusFire() — see those fields' own doc comments).
public class ArcherFocusFireMoveSpeedKnockbackAugment : AugmentEffectPD<ArcherFocusFireMoveSpeedKnockbackAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private, "Percent" suffix at the END — so AugmentDescriptionFormatter's
    // reflection can resolve {moveSpeedBonusPercent}/{knockbackBonusPercent} directly as description tokens
    // AND get the auto-*100 percent formatting (EndsWith("Percent") check).
    public float moveSpeedBonusPercent = 0.3f;
    public float knockbackBonusPercent = 1f;

    protected override void OnApply()
    {
        var focusFire = Stats.GetComponent<ArcherFocusFireAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ArcherFocusFireAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (focusFire == null) return;

        focusFire.moveSpeedPercent += moveSpeedBonusPercent;
        focusFire.knockbackPercent += knockbackBonusPercent;
    }
}
