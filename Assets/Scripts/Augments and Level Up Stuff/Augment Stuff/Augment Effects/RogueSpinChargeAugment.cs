using PurrNet.Prediction;
using UnityEngine;

// Rogue Spin-specific augment (2026-10-02) — the first real use of the AbilityIdentity/requiredAbilities
// system (see AugmentData.requiredAbilities's own doc comment). This augment's own .asset must set
// requiredAbilities = [RogueSpin] so it's only ever offered to a player who actually has RogueSpinAbilityPD
// equipped, regardless of class — scripts-only convention, that part is Editor-side, not here.
//
// Grants +chargeAmount Max Charges to RogueSpinAbilityPD specifically. Resolves the ability's own LIVE Slot
// at grant time (CharacterStatsPD.AddAbilityCharges) rather than hardcoding an assumed slot — a hardcoded
// AddAbility1Charges() call here would silently buff whichever OTHER ability actually occupies Ability1 the
// moment Spin gets reslotted onto Ability2, with no warning. No OnDestroy() override needed — same
// permanent-for-the-whole-match shape as every other simple stat-granting augment, never explicitly
// reverted.
public class RogueSpinChargeAugment : AugmentEffectPD<RogueSpinChargeAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {chargeAmount} directly as a description token; only public fields/properties/methods are checked.
    // Still Inspector-serializable either way.
    public int chargeAmount = 1;

    protected override void OnApply()
    {
        var spin = Stats.GetComponent<RogueSpinAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with RogueSpinAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (spin == null) return;
        Stats.AddAbilityCharges(spin.Slot, chargeAmount);
    }
}
