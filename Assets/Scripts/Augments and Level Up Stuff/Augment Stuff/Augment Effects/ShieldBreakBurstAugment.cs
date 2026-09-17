using PurrNet.Prediction;
using UnityEngine;

// Nerfed (per explicit instruction — interrupt and stun removed entirely) — when your shield breaks
// (CharacterStatsPD._onShieldBreak), become invincible for 1s, deal 200%* unpenalized damage to nearby
// enemies, and knock them back. No stun, no interrupt, no onhit trigger (unlike DashExplosionAugment) — not
// part of this augment's spec anymore.
//
// "AoE Effects" tag (2026-07-25): damage/knockback/radius now all route through the shared
// AreaEffectCastHelper (same one DashExplosionAugment/DashHealAugment use). Radius scales with
// Stats.AreaEffectSize — damage used to also scale with Stats.AreaEffectPower and the cast could repeat
// Stats.AreaEffectCount times, both removed entirely (2026-09-13). Radius formula standardized (2026-08-10) across
// DashExplosionAugment/DashHealAugment/ShieldBreakBurstAugment: Stats.RangeScalingAoEBase * radiusMultiplier —
// extracted onto CharacterStatsPD itself on 2026-08-12 (single source of truth after the 0.5 -> 1
// WeaponRange-coefficient retune needed touching every family member by hand), same shape
// KnightAreaStunHealAbilityPD already uses. Every cast (a flat single cast now) goes through a
// spawned AreaEffectPD (pooled, so this costs a reactivate, not a real spawn; the exact same prefab
// DashExplosionAugment uses, since the class isn't tied to any one augment), rather than a separate
// direct-cast path kept in sync by hand. Only the invincibility grant (not per-enemy, so just granted
// directly once before spawning the chain) stays as this augment's own unique payload — everything else
// (enemy-finding, damage roll, knockback, VFX) is shared, not duplicated.
//
// Knockback reworked per explicit instruction: no longer an augment-owned flat value entirely separate from
// the player's own stat (the old baseKnockback field) — now Stats.Knockback * knockbackMultiplier, i.e.
// inherited from the player's actual Knockback stat and scaled by an augment-specific multiplier (default 2x,
// exposed below) instead of being wholly disconnected from it.
//
// hierarchy.Create()/Launch() fire directly inside the PredictedEvent listener (OnShieldBreak) — same "safe
// because PredictedEvent guarantees exactly-once-per-real-occurrence" reasoning as DashExplosionAugment's
// own dash-end listener. Any retrigger casts beyond the first go through AreaEffectPD's own
// PredictedEvent, same as every other chained area effect.
public class ShieldBreakBurstAugment : AugmentEffectPD<ShieldBreakBurstAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float invincibilityDuration = 0.25f;
    public float damagePercent        = 3f;
    // Radius = Stats.RangeScalingAoEBase * radiusMultiplier — see class doc
    // comment. 1 is the neutral default; tune per-augment feel without touching the WeaponRange/AreaEffectSize
    // scaling itself.
    [SerializeField] private float radiusMultiplier    = 1.25f;
    [SerializeField] private float knockbackMultiplier = 2f;

    // Mirrors AreaEffectCastHelper.Cast's own damage formula, minus the luck/crit roll (baseline preview, not
    // a randomly-sampled one — see OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the
    // full convention), for AugmentDescriptionFormatter's {LiveStrengthRatio} token.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.Strength * damagePercent)} "
            : "";

    [Header("VFX")]
    // Duration is not exposed here — RadiusIndicatorVFXPD owns its own playback timing on the prefab itself.
    [SerializeField] private GameObject radiusVFXPrefab;

    // Spawned on every shield break (see OnShieldBreak) — pooled, so this is cheap even when
    // AreaEffectCount is just 1. Must be registered in PredictedPrefabs.asset like any other spawned effect.
    // Can reuse the exact same AreaEffectPD prefab DashExplosionAugment uses, since the class itself
    // isn't tied to any one augment.
    [Header("Chain (spawned on every shield break)")]
    [SerializeField] private GameObject chainPrefab;

    protected override void OnApply()
    {
        Stats.AddShieldBreakListener(OnShieldBreak);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveShieldBreakListener(OnShieldBreak);
    }

    private void OnShieldBreak()
    {
        if (Stats == null || Stats.IsDead) return;
        if (chainPrefab == null) return;

        Stats.GrantInvincibility(invincibilityDuration);

        Vector3 castPosition = Stats.transform.position;
        float radius = Stats.RangeScalingAoEBase * radiusMultiplier;
        float knockback = Stats.Knockback * knockbackMultiplier;

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(Stats, castPosition, radius, radiusVFXPrefab,
                damagePercent: damagePercent, notifyOnHit: false, hasKnockback: true, knockback: knockback, vfxPerCast: true);
    }
}
