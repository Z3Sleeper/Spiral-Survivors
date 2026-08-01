using PurrNet.Prediction;
using UnityEngine;

// New tech — when your shield breaks (CharacterStatsPD._onShieldBreak), become invincible for 1s, deal
// 200%* unpenalized damage to nearby enemies, knock them back, and stun them for 1s. No onhit trigger
// (unlike DashExplosionAugment) — not part of this augment's spec.
//
// The stun is the first real caller of the public IEnemyPD.Stun(duration) entry point anywhere in this
// codebase, but NOT a new mechanism — EnemyBasePD already has a fully generic stunTimer gate baked into its
// base Simulate(), already respects stunResistant/crowdControlResist uniformly. Paired with interrupt: true
// below (via AreaEffectCastHelper's own interrupt param, applied per-enemy before damage/knockback/onEnemyHit)
// — this augment's stun is meant to actually cancel whatever attack an enemy is mid-sequence on, not just
// land and wait for it to finish like a bare Stun() now does by default — see EnemyBasePD.Interrupt()'s own
// doc comment for the full reasoning and why the ordering (interrupt first, then knockback/stun) matters.
//
// "AoE Effects" tag (2026-07-25): damage/knockback/radius/count now all route through the shared
// AreaEffectCastHelper (same one DashExplosionAugment/DashHealAugment use). Damage scales with
// Stats.AreaEffectPower, radius with Stats.AreaEffectSize, and the whole cast repeats Stats.AreaEffectCount
// times — same as every other area effect. Knockback deliberately does NOT scale with AreaEffectPower (see
// its own comment below). Every cast — including AreaEffectCount == 1, always today — goes through a
// spawned AreaEffectPD (pooled, so this costs a reactivate, not a real spawn; the exact same prefab
// DashExplosionAugment uses, since the class isn't tied to any one augment), rather than a separate
// direct-cast path kept in sync by hand. Only the invincibility grant (not per-enemy, so just granted
// directly once before spawning the chain) and the stun (layered on via the helper's onEnemyHit callback,
// passed through for every cast in the chain, not just the first — ALSO scaled by Stats.AreaEffectPower,
// same as damage, per the user's own call) stay as this augment's own unique payload — everything else
// (enemy-finding, damage roll, knockback, VFX) is shared, not duplicated.
//
// Knockback reworked per explicit instruction: no longer an augment-owned flat value entirely separate from
// the player's own stat (the old baseKnockback field) — now Stats.Knockback * knockbackMultiplier, i.e.
// inherited from the player's actual Knockback stat and scaled by an augment-specific multiplier (default 2x,
// exposed below) instead of being wholly disconnected from it. Deliberately NOT scaled by Stats.AreaEffectPower
// (per the "AoE Effects" default rules — knockback only ever scales off the player's own Knockback stat).
//
// hierarchy.Create()/Launch() fire directly inside the PredictedEvent listener (OnShieldBreak) — same "safe
// because PredictedEvent guarantees exactly-once-per-real-occurrence" reasoning as DashExplosionAugment's
// own dash-end listener. Any retrigger casts beyond the first go through AreaEffectPD's own
// PredictedEvent, same as every other chained area effect.
public class ShieldBreakBurstAugment : AugmentEffectPD<ShieldBreakBurstAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float invincibilityDuration = 1f;
    [SerializeField] private float damagePercent        = 2f;
    [SerializeField] private float burstRadius          = 5f;
    [SerializeField] private float knockbackMultiplier  = 2f;
    [SerializeField] private float stunDuration         = 1f;
    // How long between each retrigger cast when Stats.AreaEffectCount > 1 — exposed for tuning.
    [SerializeField] private float retriggerRate        = 0.5f;

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
        float radius = burstRadius * Stats.AreaEffectSize;
        float knockback = Stats.Knockback * knockbackMultiplier;
        int totalCasts = Mathf.Max(1, Stats.AreaEffectCount);

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        // interrupt: true — this stun is meant to cancel whatever attack an enemy is mid-sequence on, not
        // just land and wait for it to finish (see EnemyBasePD.Interrupt()'s own doc comment); Cast() applies
        // it before damage/knockback/onEnemyHit for exactly that reason.
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(Stats, castPosition, totalCasts, retriggerRate, radius, radiusVFXPrefab,
                damagePercent: damagePercent, notifyOnHit: false, hasKnockback: true, knockback: knockback, interrupt: true,
                onEnemyHit: enemy => enemy.Stun(stunDuration * Stats.AreaEffectPower));
    }
}
