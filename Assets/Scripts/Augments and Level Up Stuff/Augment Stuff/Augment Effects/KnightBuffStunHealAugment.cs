using PurrNet.Prediction;

// Knight BuffStun-specific augment (2026-10-03), fourth and final KnightBuffStun augment. This augment's
// own .asset must set requiredAbilities = [KnightBuffStun] — scripts-only convention, that part is
// Editor-side, not here.
//
// Heals healPerHitPercent (0.5% Max Health default) for EACH enemy touched by a stun AoE cast, tallied
// across the whole cast — e.g. 6 enemies caught by one AoE heals for 6 * 0.5% = 3% Max Health in one call.
// Counts every enemy the AoE actually touched, NOT just ones successfully stunned/interrupted/knocked back
// — see KnightBuffStunAbilityPD.OnAoECastComplete's own doc comment for why (AreaEffectCastHelper.Cast's
// hitCount increments for every enemy found, independent of interrupt/knockback/onEnemyHit success).
// Covers all three of this ability's possible triggers per cast (on-cast/recast/natural-end, or just the
// one base trigger without KnightBuffStunMoreStunsAugment) for free, since all of them funnel through the
// ability's own single TriggerAoE method.
//
// Pattern B (see KnightBerserkMoveSpeedAugment) — the actual counting/healing logic lives entirely on the
// ability itself (KnightBuffStunAbilityPD.healPercentPerHit, a no-op-by-default field). This augment just
// bumps that one field in OnApply().
public class KnightBuffStunHealAugment : AugmentEffectPD<KnightBuffStunHealAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, and named with the literal "Percent" suffix at the END (not buried mid-name as
    // healPercentPerHit was) — so AugmentDescriptionFormatter's reflection can resolve
    // {healPerHitPercent} as a description token AND get its auto-*100 percent formatting, which only
    // fires on an EndsWith("Percent") check (same "PerUnit vs Percent" trap OnHitDamageDistanceAugment's
    // own perUnitDistancePercent rename fixed for the identical reason). The ability's own mirror field
    // stays named healPercentPerHit (no Percent suffix, [HideInInspector], never a description token) —
    // only this augment's own copy needs the suffix.
    public float healPerHitPercent = 0.005f;

    protected override void OnApply()
    {
        var buffStun = Stats.GetComponent<KnightBuffStunAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with KnightBuffStunAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (buffStun == null) return;
        buffStun.healPercentPerHit += healPerHitPercent;
    }
}
