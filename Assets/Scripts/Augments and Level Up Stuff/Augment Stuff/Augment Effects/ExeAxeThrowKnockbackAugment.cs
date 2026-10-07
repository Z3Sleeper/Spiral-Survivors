using PurrNet.Prediction;

// Executioner AxeThrow-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities
// = [ExeAxeThrow] — scripts-only convention, that part is Editor-side, not here.
//
// +500% Knockback and +25% damage — ADDITIVE onto the ability's own live knockbackMultiplier/
// damageMultiplier fields (both 1f neutral default), not a destructive multiply on
// strengthPercent/Knockback themselves — see [[feedback_additive_over_multiplicative_default]].
// EffectiveStrengthPercent (what Activate() actually passes into the projectile's own Launch() as its
// damage baseline) and knockbackMultiplier (passed straight through to Launch(), since the projectile
// reads live Stats.Knockback itself rather than a snapshot) both automatically reflect this.
public class ExeAxeThowKnockbackAugment : AugmentEffectPD<ExeAxeThowKnockbackAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // "Percent" suffix at the END on both — AugmentDescriptionFormatter auto-*100s them for description
    // tokens (5f -> "500%", 0.25f -> "25%"). Directly IS what gets added to the ability's own multiplier
    // fields below.
    public float knockbackBonusPercent = 5f;
    public float damageBonusPercent = 0.25f;

    protected override void OnApply()
    {
        var axeThrow = Stats.GetComponent<ExeAxeThrowAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ExeAxeThrowAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (axeThrow == null) return;

        axeThrow.knockbackMultiplier += knockbackBonusPercent;
        axeThrow.damageMultiplier += damageBonusPercent;
    }
}
