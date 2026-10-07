using PurrNet.Prediction;

// Executioner BigAxe-specific augment (2026-10-06), built to the exact same shape as
// KnightBuffStunBuffStrengthAugment (Stuart's own explicit request — mirror that one, not the usual
// additive-multiplier default). This augment's own .asset must set requiredAbilities = [ExeBigAxe] —
// scripts-only convention, that part is Editor-side, not here.
//
// Amplifies EVERY buff ExeBigAxeAbilityPD grants during its active window (weaponRangeTotalPercent/
// executeThresholdPercent/critChancePercent, AND any later augment's own buff fields, e.g.
// ExeBigAxeKnockbackAttackSpeedAugment's knockbackPercent/attackSpeedPercent) by
// buffStrengthMultiplierPercent (200% default, i.e. 2x — e.g. 25% -> 50%), WITHOUT touching duration at all.
//
// Sets a LIVE multiplier field on the ability (ExeBigAxeAbilityPD.buffStrengthMultiplier, 1f/no-op default)
// rather than destructively multiplying specific fields once at OnApply() time — a one-shot destructive
// multiply would only ever affect whichever buff fields already existed AT THE MOMENT this augment was
// picked, silently missing any buff field a LATER-picked ability-specific augment grants (order-dependent —
// exactly the bug this augment must avoid, same reasoning KnightBuffStunBuffStrengthAugment's own doc
// comment gives). Setting a multiplier the ABILITY itself applies live, every cast (see Activate()'s own
// _appliedXPercent = X * buffStrengthMultiplier snapshot lines), is naturally order-independent instead —
// whichever buff fields are live at cast time all get scaled, regardless of which augment granted them or
// which order the two were picked in.
public class ExeBigAxeBuffAugment : AugmentEffectPD<ExeBigAxeBuffAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, and named with the literal "Percent" suffix at the END — so AugmentDescriptionFormatter's
    // reflection can resolve {buffStrengthMultiplierPercent} as a description token AND get its auto-*100
    // percent formatting, which only fires on an EndsWith("Percent") check (same "PerUnit vs Percent" trap
    // OnHitDamageDistanceAugment's own perUnitDistancePercent rename fixed for the identical reason). 2f
    // here displays as "200%", which correctly reads as the multiplier itself (200% buff strength = 2x,
    // i.e. +100%). Picking a SECOND, different buff-amplifying augment for this same ability (if one ever
    // existed) would compound multiplicatively via *= below, matching the literal "multiply by X" framing
    // this augment was specified with — not the usual CharacterStatsPD buff-stacks-additively convention.
    public float buffStrengthMultiplierPercent = 2f;

    // Display-only derived token (2026-10-07) — buffStrengthMultiplierPercent minus the neutral 1x
    // baseline, so {buffStrengthBonusPercent} resolves to the actual BONUS (e.g. "150%" at a 2.5x
    // multiplier) instead of buffStrengthMultiplierPercent's own raw multiplier reading (e.g. "250%") —
    // same fix as ArcherArrowRainGrowthAugment.damageBonusPercent. Computed, not stored — always exactly
    // in sync with whatever buffStrengthMultiplierPercent is tuned to.
    public float buffStrengthBonusPercent => buffStrengthMultiplierPercent - 1f;

    protected override void OnApply()
    {
        var bigAxe = Stats.GetComponent<ExeBigAxeAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ExeBigAxeAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (bigAxe == null) return;
        bigAxe.buffStrengthMultiplier *= buffStrengthMultiplierPercent;
    }
}
