using PurrNet.Prediction;

// Executioner BigAxe-specific augment (2026-10-06), second requiredAbilities example for ExeBigAxeAbilityPD.
// This augment's own .asset must set requiredAbilities = [ExeBigAxe] — scripts-only convention, that part
// is Editor-side, not here.
//
// Adds +knockbackPercent (100%) Knockback and +attackSpeedPercent (20%) Attack Speed to the list of buffs
// ExeBigAxeAbilityPD grants during its active window, alongside the existing Weapon Range/Execute
// Threshold/Crit Chance — same lifecycle (granted in Activate(), reverted in Simulate()'s own end-of-active
// block, snapshotted into _appliedKnockbackPercent/_appliedAttackSpeedPercent at cast time so a live
// Inspector/augment change mid-buff can't desync the later revert). Sets two dormant, [HideInInspector]
// fields directly on the ability (ExeBigAxeAbilityPD.knockbackPercent/attackSpeedPercent, both 0f/no-op
// until this augment sets them) — same Pattern B shape as KnightBuffStunStrengthKnockbackAugment.
//
// Also correctly amplified by ExeBigAxeBuffAugment if the player has BOTH (100%/20% -> 200%/40% at that
// augment's default 2x) — see ExeBigAxeAbilityPD.Activate()'s own knockbackPercent/attackSpeedPercent *
// buffStrengthMultiplier snapshot lines, and ExeBigAxeBuffAugment's own doc comment for why that multiplier
// is applied live by the ability itself rather than baked destructively into specific fields, so it
// correctly reaches these two fields regardless of which of the two augments was picked first.
public class ExeBigAxeKnockbackAttackSpeedAugment : AugmentEffectPD<ExeBigAxeKnockbackAttackSpeedAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {knockbackPercent}/{attackSpeedPercent} directly as description tokens; only public fields/
    // properties/methods are checked. Still Inspector-serializable either way.
    public float knockbackPercent   = 1f;
    public float attackSpeedPercent = 0.2f;

    protected override void OnApply()
    {
        var bigAxe = Stats.GetComponent<ExeBigAxeAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ExeBigAxeAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (bigAxe == null) return;
        bigAxe.knockbackPercent   += knockbackPercent;
        bigAxe.attackSpeedPercent += attackSpeedPercent;
    }
}
