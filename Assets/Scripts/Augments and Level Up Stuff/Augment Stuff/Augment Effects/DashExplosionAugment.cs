using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/DashExplosionAugment.cs — On Dash End, deals unpenalized-
// weapon-damage-scaled AOE damage to nearby enemies. No longer triggers onhit listeners (OnHitDamageAugment,
// CritTriggerOnHitAugment, etc.) — notifyOnHit: false (2026-10-18, per explicit instruction, matching
// ShieldBreakBurstAugment's own "no onhit trigger" spec); onHitPowerPercent is now inert, kept only because
// the live description text still references {onHitPowerPercent} (needs a manual asset-text update if that's
// ever cleaned up). Rolls luck variance independently for the damage figure, same as every other damage
// effect. Deviates from old here: this is a direct damage
// source (same category as PhantomBladePD), so it rolls its own independent crit per enemy
// (Stats.NextFloat() < Stats.CritChance, off this player's own stream — was GameRandom.instance until
// 2026-10-16), same as PhantomBladeSummonHelper.SummonAtTarget —
// old never crit, but every other direct-damage source in the new system does. No longer grants a flat
// dashHaste bonus (removed per the "no training wheels" balance pass). Subscribes directly to this player's
// own PlayerControllerPD._onDashEnded, same per-owner-instance cross-player-safety reasoning as
// PhantomBladeOnDashAugment.
//
// "AoE Effects" tag (2026-07-25): radius (and the VFX showing it) scales with Stats.AreaEffectSize. Damage/
// onHitPower used to also scale with Stats.AreaEffectPower and the cast could repeat Stats.AreaEffectCount
// times — both removed entirely (2026-09-13); every cast is now a flat single cast at this augment's own
// plain baseline. Radius formula standardized (2026-08-10) across DashExplosionAugment/DashHealAugment/
// ShieldBreakBurstAugment: Stats.RangeScalingAoEBase * radiusMultiplier — extracted onto CharacterStatsPD
// itself on 2026-08-12 (single source of truth after the 0.5 -> 1 WeaponRange-coefficient retune needed
// touching every family member by hand), same shape KnightBuffStunAbilityPD already uses — WeaponRange
// is the actual stat, not the caster's true weapon extent. Every cast goes through a spawned AreaEffectPD,
// one dedicated, fully self-contained instance PER dash, rather than tracking chain data inside this
// augment's own State (so back-to-back dashes each get their own independent chain instead of sharing one
// overwritable slot) or splitting into a separate direct-cast path (which would mean every cast parameter
// needs threading through two call sites kept in sync by hand). Pooled on the prefab, so the common
// single-cast case costs a pooled reactivate, not a real spawn. AreaEffectPD itself isn't Dash-specific (the
// same generic chain every "AoE Effects" caller uses, enemy- or ally-targeting, damage or not) — see its own
// doc comment for why that couldn't just be a List<T> inside a single State struct.
public class DashExplosionAugment : AugmentEffectPD<DashExplosionAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float damagePercent     = 2f;
    public float onHitPowerPercent = 2f;
    // Radius = Stats.RangeScalingAoEBase * radiusMultiplier — see class doc
    // comment. 1 is the neutral default; tune per-augment feel without touching the WeaponRange/AreaEffectSize
    // scaling itself.
    [SerializeField] private float radiusMultiplier    = 1.25f;
    // Multiplies Stats.Knockback (unpenalized — no onHitPower multiply, same convention
    // ShieldBreakBurstAugment's own knockbackMultiplierPercent uses). 1 preserves this augment's previous
    // behavior (raw Stats.Knockback, unscaled) now that it's exposed for tuning. Public, "Percent" suffix
    // at the END (not public/not-Percent-suffixed before) — so AugmentDescriptionFormatter's reflection can
    // resolve {knockbackMultiplierPercent} directly as a description token, with its auto-*100 formatting
    // firing correctly.
    public float knockbackMultiplierPercent = 1f;

    // Mirrors AreaEffectCastHelper.Cast's own damage formula, minus the luck/crit roll (baseline preview, not
    // a randomly-sampled one — see OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the
    // full convention), for AugmentDescriptionFormatter's {LiveStrengthRatio} token.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.Strength * damagePercent)} "
            : "";

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
        float radius = Stats.RangeScalingAoEBase * radiusMultiplier;
        float knockback = Stats.Knockback * knockbackMultiplierPercent;

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        // hasKnockback: true, knockback explicitly passed (Stats.Knockback * knockbackMultiplier) — same
        // "unpenalized" convention ShieldBreakBurstAugment's own knockback uses.
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(Stats, castPosition, radius, null,
                damagePercent: damagePercent, onHitPowerPercent: onHitPowerPercent, notifyOnHit: false, hasKnockback: true, knockback: knockback, vfxPerCast: true);

        // Shockwave VFX opt-in (2026-09-26, user-curated, replaces the old flat-cylinder radiusVFXPrefab
        // stand-in) — safe unconditionally: this callback only ever runs once per real dash end, off
        // PlayerControllerPD._onDashEnded, a genuine PredictedEvent. Player color for both start/end (no
        // override requested for this one).
        VFXManager.instance?.PlayShockwaveVfx(castPosition, Stats.Size, Stats.PlayerColor, Stats.PlayerColor, radius);
    }
}
