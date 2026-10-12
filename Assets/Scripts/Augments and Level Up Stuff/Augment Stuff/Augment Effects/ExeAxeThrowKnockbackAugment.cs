using PurrNet.Prediction;

// Executioner AxeThrow-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities
// = [ExeAxeThrow] — scripts-only convention, that part is Editor-side, not here.
//
// Doubles Knockback and grants +25% damage (nerfed 2026-10-08 from tripling — see
// baseKnockbackBonusPercent's own doc comment: once the ability's +500% innate baseline alone already
// proved "super wonky" and got cut to +200%, tripling on top of that was never actually play-tested as
// shipped, so this got cut to doubling in the same pass). Damage is ADDITIVE onto the ability's own live
// damageMultiplier field (1f neutral default), not a destructive multiply on strengthPercent itself —
// see [[feedback_additive_over_multiplicative_default]]. Knockback is MULTIPLICATIVE against the whole
// ability's existing knockback multiplier (EffectiveKnockbackMultiplier, baseline included), not an
// additive flat chunk — see ExeAxeThrowAbilityPD.knockbackMultiplier's own doc comment for why (keeps this
// augment's relative value proportional to whatever the baseline is tuned to, instead of shrinking as a
// fraction of the total if the baseline ever goes up). Description reads "+200% Knockback" as shorthand
// players read as "x2 total," not a literal additive delta — same "show the raw multiplier, not a derived
// bonus-to-it" choice KnightBuffStunBuffStrengthAugment's own buffStrengthMultiplier display already makes.
public class ExeAxeThowKnockbackAugment : AugmentEffectPD<ExeAxeThowKnockbackAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // "Percent" suffix at the END on both — AugmentDescriptionFormatter auto-*100s them for description
    // tokens (2f -> "200", 0.25f -> "25"). knockbackMultiplierPercent is a direct MULTIPLY onto the
    // ability's own knockbackMultiplier (1f neutral, *2f here = "doubles the multiplier"), not an add.
    public float knockbackMultiplierPercent = 2f;
    public float damageBonusPercent = 0.25f;

    protected override void OnApply()
    {
        var axeThrow = Stats.GetComponent<ExeAxeThrowAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ExeAxeThrowAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (axeThrow == null) return;

        axeThrow.knockbackMultiplier *= knockbackMultiplierPercent;
        axeThrow.damageMultiplier += damageBonusPercent;
    }
}
