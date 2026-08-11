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
// total via retriggerRate. Radius formula standardized (2026-08-10) across DashExplosionAugment/
// DashHealAugment/ShieldBreakBurstAugment: Stats.RangeScalingAoEBase * radiusMultiplier — extracted onto
// CharacterStatsPD itself on 2026-08-12 (single source of truth after the 0.5 -> 1 WeaponRange-coefficient
// retune needed touching every family member by hand), same shape KnightAreaStunHealAbilityPD
// already uses. See DashExplosionAugment's own doc comment for the full
// reasoning — identical
// shape here (every cast, including the common AreaEffectCount == 1 case, goes through a spawned AreaEffectPD
// — pooled, so this costs a reactivate, not a real spawn — rather than a separate direct-cast path kept in
// sync by hand). AreaEffectPD itself isn't Dash-specific — the same generic chain every "AoE Effects" caller
// uses, enemy- or ally-targeting; this augment passes targetAllies: true and its own RollAndHeal as
// onAllyHit, since the heal-roll formula is this augment's own business logic, not generic chain plumbing.
public class DashHealAugment : AugmentEffectPD<DashHealAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float healthRegenMultiplier = 5f;
    // Radius = Stats.RangeScalingAoEBase * radiusMultiplier — see class doc
    // comment. 1 is the neutral default; tune per-augment feel without touching the WeaponRange/AreaEffectSize
    // scaling itself.
    [SerializeField] private float radiusMultiplier = 1f;

    // healthRegenMultiplier itself doesn't end in "Percent", so AugmentDescriptionFormatter's *100
    // convention wouldn't apply to it directly — this alias exists so a description can reference
    // {HealthRegenMultiplierPercent} for a static fallback (shown outside a live match, when
    // {LiveHealthRegenRatio} itself is blank) alongside the live number.
    public float HealthRegenMultiplierPercent => healthRegenMultiplier;

    // Fully accurate live preview — unlike the OnHit/OnStrike family, AreaEffectPower is itself a stat (not
    // a per-event roll), so nothing needs to be excluded here — for AugmentDescriptionFormatter's
    // {LiveHealthRegenRatio} token.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.HealthRegen * healthRegenMultiplier * stats.AreaEffectPower)} "
            : "";
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
        float radius = Stats.RangeScalingAoEBase * radiusMultiplier;
        int totalCasts = Mathf.Max(1, Stats.AreaEffectCount);

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(Stats, castPosition, totalCasts, retriggerRate, radius, radiusVFXPrefab,
                targetAllies: true, onAllyHit: RollAndHeal, vfxPerCast: true);
    }

    // Moved out of the old dedicated AreaEffectHealPD (now merged away) — onAllyHit is just "roll this
    // ally's heal and apply it," the same per-target extra shape ShieldBreakBurstAugment's Stun already uses
    // via onEnemyHit, just for the ally-targeting side of AreaEffectCastHelper.Cast. CharacterStatsPD.All
    // (what Cast's own targetAllies loop iterates) includes the caster themselves — no ally == owner
    // exclusion there — so this fires for Stats too, not just genuinely other players.
    private void RollAndHeal(CharacterStatsPD ally)
    {
        // baseHeal is passed RAW (un-rolled) — Heal()/HealFromAlly() each roll their own luck variance now
        // (see their own doc comments on CharacterStatsPD), independently per target rather than one shared
        // roll reused across everyone hit.
        float baseHeal = Stats.HealthRegen * healthRegenMultiplier * Stats.AreaEffectPower;

        // Self heals through the normal, HealPower-scaled Heal() (same as any other self-heal source, rolls
        // against Stats' own Luck) — a genuinely different ally goes through HealFromAlly() instead (rolls
        // against the CASTER's Luck too — "the healer rolls for the healee" — but NOT scaled by the ally's
        // own HealPower), same "don't let a receiving ally's own stacked HealPower also amplify heals they
        // receive from someone else" reasoning OnStrikeHealAllyAugment's own identical swap uses (see
        // CharacterStatsPD.HealFromAlly's own doc comment).
        if (ally == Stats) ally.Heal(baseHeal);
        else ally.HealFromAlly(baseHeal, Stats);
    }
}
