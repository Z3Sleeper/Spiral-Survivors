using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/DashHealAugment.cs — On Dash End, heals self and all nearby
// allies for a percent of HealthRegen. Rolls luck variance per ally independently (the "*" annotation),
// same as every other healing/damage effect (see OnStrikeHealAllyAugment's own RollDamage call). No longer
// grants a flat dashHaste bonus (removed per the "no training wheels" balance pass — augments granting
// unique effects shouldn't hand out the stats needed to build around them). Subscribes directly to this
// player's own PlayerControllerPD._onDashEnded, same per-owner-instance cross-player-safety reasoning as
// PhantomBladeOnDashAugment (no shared/static channel for another player's dash to leak through).
//
// "AoE Effects" tag (2026-07-25): heal amount scales with Stats.AreaEffectPower, radius (and the VFX
// showing it) scale with Stats.AreaEffectSize, and the whole cast can repeat Stats.AreaEffectCount times
// total via retriggerRate. See DashExplosionAugment's own doc comment for the full reasoning — identical
// shape here (every cast, including the common AreaEffectCount == 1 case, goes through a spawned AreaEffectPD
// — pooled, so this costs a reactivate, not a real spawn — rather than a separate direct-cast path kept in
// sync by hand). AreaEffectPD itself isn't Dash-specific — the same generic chain every "AoE Effects" caller
// uses, enemy- or ally-targeting; this augment passes targetAllies: true and its own RollAndHeal as
// onAllyHit, since the heal-roll formula is this augment's own business logic, not generic chain plumbing.
public class DashHealAugment : AugmentEffectPD<DashHealAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float healthRegenMultiplier = 5f;
    [SerializeField] private float healRadius   = 10f;
    // How long between each retrigger cast when Stats.AreaEffectCount > 1 — exposed for tuning.
    [SerializeField] private float retriggerRate = 0.5f;

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
        float radius = healRadius * Stats.AreaEffectSize;
        int totalCasts = Mathf.Max(1, Stats.AreaEffectCount);

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(Stats, castPosition, totalCasts, retriggerRate, radius, radiusVFXPrefab,
                targetAllies: true, onAllyHit: RollAndHeal);
    }

    // Moved out of the old dedicated AreaEffectHealPD (now merged away) — onAllyHit is just "roll this
    // ally's heal and apply it," the same per-target extra shape ShieldBreakBurstAugment's Stun already uses
    // via onEnemyHit, just for the ally-targeting side of AreaEffectCastHelper.Cast.
    private void RollAndHeal(CharacterStatsPD ally)
    {
        float baseHeal = Stats.HealthRegen * healthRegenMultiplier * Stats.AreaEffectPower;
        float rolled = WeaponDamageMath.RollDamage(baseHeal, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
        if (isHighroll) rolled *= 1f + Stats.HighrollBonus;

        ally.Heal(rolled);
    }
}
