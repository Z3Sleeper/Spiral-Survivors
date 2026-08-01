using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/DashExplosionAugment.cs — On Dash End, deals unpenalized-
// weapon-damage-scaled AOE damage to nearby enemies and triggers onhit listeners (OnHitDamageAugment,
// CritTriggerOnHitAugment, etc.) on each one via Stats.NotifyHit — same non-weapon-hit-source pattern
// PhantomBladePD uses. Rolls luck variance independently for both the damage and the onhit-power figure
// (both "*" annotated), same as every other damage effect. Deviates from old here: this is a direct damage
// source (same category as PhantomBladePD), so it rolls its own independent crit per enemy
// (GameRandom.instance.NextFloat() < Stats.CritChance), same as PhantomBladeSummonHelper.SummonAtTarget —
// old never crit, but every other direct-damage source in the new system does. No longer grants a flat
// dashHaste bonus (removed per the "no training wheels" balance pass). Subscribes directly to this player's
// own PlayerControllerPD._onDashEnded, same per-owner-instance cross-player-safety reasoning as
// PhantomBladeOnDashAugment.
//
// "AoE Effects" tag (2026-07-25): damage/onHitPower scale with Stats.AreaEffectPower, radius (and the VFX
// showing it) scale with Stats.AreaEffectSize, and the whole cast can repeat Stats.AreaEffectCount times
// total via retriggerRate. Every cast — including AreaEffectCount == 1, i.e. always today, since nothing
// grants it above 1 yet — goes through a spawned AreaEffectPD, one dedicated, fully self-contained instance
// PER dash, rather than tracking chain data inside this augment's own State (so back-to-back dashes each get
// their own independent chain instead of sharing one overwritable slot) or splitting into a separate
// direct-cast path (which would mean every cast parameter needs threading through two call sites kept in
// sync by hand). Pooled on the prefab, so the common single-cast case costs a pooled reactivate, not a real
// spawn. AreaEffectPD itself isn't Dash-specific (the same generic chain every "AoE Effects" caller uses,
// enemy- or ally-targeting, damage or not) — see its own doc comment for why that couldn't just be a List<T>
// inside a single State struct.
public class DashExplosionAugment : AugmentEffectPD<DashExplosionAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float damagePercent     = 2f;
    [SerializeField] private float onHitPowerPercent = 2f;
    [SerializeField] private float explosionRadius   = 5f;
    // How long between each retrigger cast when Stats.AreaEffectCount > 1 — exposed for tuning.
    [SerializeField] private float retriggerRate     = 0.5f;

    [Header("VFX")]
    // Duration is not exposed here — RadiusIndicatorVFXPD owns its own playback timing on the prefab itself.
    [SerializeField] private GameObject radiusVFXPrefab;

    // Spawned on every dash (see OnDashEnded) — pooled, so this is cheap even when AreaEffectCount is just
    // 1. Must be registered in PredictedPrefabs.asset like any other spawned effect.
    [Header("Chain (spawned on every dash)")]
    [SerializeField] private GameObject chainPrefab;

    private PlayerControllerPD _controller;

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null) _controller.AddDashEndedListener(OnDashEnded);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashEndedListener(OnDashEnded);
    }

    private void OnDashEnded()
    {
        if (Stats == null || Stats.IsDead) return;
        if (chainPrefab == null) return;

        Vector3 castPosition = Stats.transform.position;
        float radius = explosionRadius * Stats.AreaEffectSize;
        int totalCasts = Mathf.Max(1, Stats.AreaEffectCount);

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        // hasKnockback: true, knockback left at its default (null) — resolves to Stats.Knockback exactly,
        // deliberately NOT scaled by Stats.AreaEffectPower (per the "AoE Effects" default rules).
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(Stats, castPosition, totalCasts, retriggerRate, radius, radiusVFXPrefab,
                damagePercent: damagePercent, onHitPowerPercent: onHitPowerPercent, hasKnockback: true, vfxPerCast: true);
    }
}
