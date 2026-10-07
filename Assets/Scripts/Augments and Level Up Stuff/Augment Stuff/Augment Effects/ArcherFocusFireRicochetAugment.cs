using PurrNet.Prediction;

// Archer FocusFire-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [ArcherFocusFire] — scripts-only convention, that part is Editor-side, not here.
//
// Grants +ricochetAmount Ricochet, but ONLY while ArcherFocusFireAbilityPD's own active window is running —
// deliberately NOT a permanent CharacterStatsPD.AddRicochetFlat call here, same "ability owns the no-op
// field, augment just bumps it" shape KnightBerserkMoveSpeedAugment's own Move Speed half uses. The actual
// grant/revert pairing lives on the ability itself (ArcherFocusFireAbilityPD.ricochetFlat, applied in
// Activate()/reverted in EndFocusFire() — see that field's own doc comment).
public class ArcherFocusFireRicochetAugment : AugmentEffectPD<ArcherFocusFireRicochetAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {ricochetAmount} directly as a description token; only public fields/properties/methods are checked.
    // Still Inspector-serializable either way. No "Flat"/"Percent" suffix needed — Ricochet is itself a
    // flat bounce count, not a percent, same naming shape RogueSpinChargeAugment.chargeAmount uses.
    public float ricochetAmount = 2f;

    protected override void OnApply()
    {
        var focusFire = Stats.GetComponent<ArcherFocusFireAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ArcherFocusFireAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (focusFire == null) return;
        focusFire.ricochetFlat += ricochetAmount;
    }
}
