using PurrNet.Prediction;

// Knight BuffStun-specific augment (2026-10-02), second requiredAbilities example for KnightBuffStunAbilityPD.
// This augment's own .asset must set requiredAbilities = [KnightBuffStun] — scripts-only convention, that
// part is Editor-side, not here.
//
// Amplifies EVERY buff KnightBuffStunAbilityPD grants during its active window (maxHealthPercent/
// rangePercent/sizePercent, authored 25%/50%/50% — AND any later augment's own buff fields, e.g.
// KnightBuffStunStrengthKnockbackAugment's strengthPercent/knockbackPercent) by buffStrengthMultiplierPercent
// (150% default, i.e. 1.5x — e.g. 25% -> 37.5%), WITHOUT touching duration at all.
//
// Sets a LIVE multiplier field on the ability (KnightBuffStunAbilityPD.buffStrengthMultiplier, 1f/no-op
// default) rather than destructively multiplying specific fields once at OnApply() time — a one-shot
// destructive multiply would only ever affect whichever buff fields already existed AT THE MOMENT this
// augment was picked, silently missing any buff field a LATER-picked ability-specific augment grants
// (order-dependent, exactly the bug this augment must avoid per the explicit requirement that
// KnightBuffStunStrengthKnockbackAugment's grants "should also be affected by [this augment] if the player
// has it"). Setting a multiplier the ABILITY itself applies live, every cast (see Activate()'s own
// _appliedXPercent = X * buffStrengthMultiplier snapshot lines), is naturally order-independent instead —
// whichever buff fields are live at cast time all get scaled, regardless of which augment granted them or
// which order the two were picked in.
public class KnightBuffStunBuffStrengthAugment : AugmentEffectPD<KnightBuffStunBuffStrengthAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, and named with the literal "Percent" suffix at the END — so AugmentDescriptionFormatter's
    // reflection can resolve {buffStrengthMultiplierPercent} as a description token AND get its auto-*100
    // percent formatting, which only fires on an EndsWith("Percent") check (same "PerUnit vs Percent" trap
    // OnHitDamageDistanceAugment's own perUnitDistancePercent rename fixed for the identical reason). 1.5
    // here displays as "150%", which correctly reads as the multiplier itself (150% buff strength = 1.5x).
    // Picking this twice compounds multiplicatively (1.5x then 1.5x = 2.25x total, via *= below) rather
    // than stacking additively — matches the literal "multiply by X" framing this augment was specified
    // with, not the usual CharacterStatsPD buff-stacks-additively convention (that convention is about
    // ongoing percent bonuses against a neutral baseline, not a multiplier scaling an already-fixed
    // authored constant like this). The ability's own mirror field stays named buffStrengthMultiplier
    // (no Percent suffix, [HideInInspector], never a description token) — only this augment's own copy
    // needs the suffix.
    public float buffStrengthMultiplierPercent = 1.5f;

    protected override void OnApply()
    {
        var buffStun = Stats.GetComponent<KnightBuffStunAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with KnightBuffStunAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (buffStun == null) return;
        buffStun.buffStrengthMultiplier *= buffStrengthMultiplierPercent;
    }
}
