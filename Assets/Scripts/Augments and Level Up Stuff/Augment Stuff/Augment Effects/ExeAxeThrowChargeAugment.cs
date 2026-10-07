using PurrNet.Prediction;

// Executioner AxeThrow-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities
// = [ExeAxeThrow] — scripts-only convention, that part is Editor-side, not here.
//
// Three effects:
// 1. +chargeAmount Max Charges (Pattern A, Stats.AddAbilityCharges — resolves the ability's own LIVE Slot
//    at grant time, same RogueSpinChargeAugment shape).
// 2. ALL missing charges restore the instant a completed cooldown would normally only refill 1 — new
//    ExeAxeThrowAbilityPD.restoreAllChargesOnCooldown switch (Pattern B), checked in BOTH Simulate()'s own
//    natural regen tick and ApplyCooldownReduction's artificial-speedup loop, so this is a genuine change
//    to the charge-regen MECHANIC itself, not just the passive tick specifically.
// 3. +cooldownPenaltyPercent to the ability's BASE cooldown (50% default) — the cost for #2, named
//    "Penalty" not "Bonus" since a LONGER cooldown is a downside for the player, not an upside (see
//    [[feedback_good_bad_color_convention]]). ADDITIVE onto the ability's own live cooldownMultiplier field
//    (1f neutral default), not a destructive multiply on cooldown itself — see
//    [[feedback_additive_over_multiplicative_default]]. EffectiveCooldown (cooldown * cooldownMultiplier,
//    haste-divided) is what every consumer (the regen timer, ApplyCooldownReduction, LiveCooldownRatio,
//    CooldownDuration/BaseCooldownDuration) actually reads.
public class ExeAxeThrowChargeAugment : AugmentEffectPD<ExeAxeThrowChargeAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {chargeAmount}/{cooldownPenaltyPercent} directly as description tokens; only public fields/properties/
    // methods are checked. cooldownPenaltyPercent ends in "Percent" so its auto-*100 formatting fires.
    public int   chargeAmount = 1;
    public float cooldownPenaltyPercent = 0.5f;

    protected override void OnApply()
    {
        var axeThrow = Stats.GetComponent<ExeAxeThrowAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ExeAxeThrowAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (axeThrow == null) return;

        Stats.AddAbilityCharges(axeThrow.Slot, chargeAmount);
        axeThrow.restoreAllChargesOnCooldown = true;
        axeThrow.cooldownMultiplier += cooldownPenaltyPercent;
    }
}
