using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/DashHealAugment.cs — On Dash End, heals self and all nearby
// allies for a percent of HealthRegen. No longer rolls luck variance (2026-08-12: heals stopped rolling luck
// entirely — see CharacterStatsPD.Heal's own doc comment; drop any "*" annotation from this augment's own
// description if it still has one). No longer grants a flat dashHaste bonus (removed per the "no training wheels" balance pass — augments granting
// unique effects shouldn't hand out the stats needed to build around them). Subscribes directly to this
// player's own PlayerControllerPD._onDashEnded, same per-owner-instance cross-player-safety reasoning as
// PhantomBladeOnDashAugment (no shared/static channel for another player's dash to leak through).
//
// "AoE Effects" tag (2026-07-25): radius (and the VFX showing it) scales with Stats.AreaEffectSize. Heal
// amount used to also scale with Stats.AreaEffectPower and the cast could repeat Stats.AreaEffectCount times
// — both removed entirely (2026-09-13); every cast is now a flat single cast at this augment's own plain
// baseline. Radius formula standardized (2026-08-10) across DashExplosionAugment/DashHealAugment/
// ShieldBreakBurstAugment: Stats.RangeScalingAoEBase * radiusMultiplier — extracted onto CharacterStatsPD
// itself on 2026-08-12 (single source of truth after the 0.5 -> 1 WeaponRange-coefficient retune needed
// touching every family member by hand), same shape KnightAreaStunHealAbilityPD already uses. See
// DashExplosionAugment's own doc comment for the full reasoning — identical shape here (every cast goes
// through a spawned AreaEffectPD — pooled, so this costs a reactivate, not a real spawn — rather than a
// separate direct-cast path kept in sync by hand). AreaEffectPD itself isn't Dash-specific — the same
// generic chain every "AoE Effects" caller uses, enemy- or ally-targeting; this augment passes
// targetAllies: true and its own RollAndHeal as onAllyHit, since the heal-roll formula is this augment's own
// business logic, not generic chain plumbing.
public class DashHealAugment : AugmentEffectPD<DashHealAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 2026-09-14: scales off Stats.BonusHealthRegen (was Stats.HealthRegen, the full/total stat) — see that
    // property's own doc comment. Renamed from healthRegenMultiplier to bonusHealthRegenPercent (now ends in
    // "Percent", so AugmentDescriptionFormatter's *100 convention applies directly) — the old
    // HealthRegenMultiplierPercent alias property this needed is gone, no longer necessary.
    public float bonusHealthRegenPercent = 2.5f;
    // Flat floor added to the scaled portion below (2026-08-12, playtesting: this augment did literally
    // nothing for a build with zero HealthRegen investment) — guarantees a non-zero base even at zero
    // HealthRegen investment.
    [SerializeField] private float baseHeal = 10f;
    // Radius = Stats.RangeScalingAoEBase * radiusMultiplier — see class doc
    // comment. 1 is the neutral default; tune per-augment feel without touching the WeaponRange/AreaEffectSize
    // scaling itself.
    [SerializeField] private float radiusMultiplier = 1.25f;

    // For AugmentDescriptionFormatter's {LiveHealthRegenRatio} token.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseHeal + stats.BonusHealthRegen * bonusHealthRegenPercent)} "
            : "";
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

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(Stats, castPosition, radius, radiusVFXPrefab,
                targetAllies: true, onAllyHit: RollAndHeal, vfxPerCast: true);
    }

    // Moved out of the old dedicated AreaEffectHealPD (now merged away) — onAllyHit is just "roll this
    // ally's heal and apply it," the same per-target extra shape ShieldBreakBurstAugment's Stun already uses
    // via onEnemyHit, just for the ally-targeting side of AreaEffectCastHelper.Cast. FriendlyTargetRegistryPD.
    // All (what Cast's own targetAllies loop iterates) includes the caster themselves — no ally == owner
    // exclusion there — so this fires for Stats too, not just genuinely other players.
    private void RollAndHeal(IFriendlyPD ally)
    {
        float totalHeal = baseHeal + Stats.BonusHealthRegen * bonusHealthRegenPercent;

        // Self heals through the normal, HealPower-scaled Heal() (same as any other self-heal source) — a
        // genuinely different ally goes through HealFromAlly() instead (NOT scaled by the ally's own
        // HealPower), same "don't let a receiving ally's own stacked HealPower also amplify heals they
        // receive from someone else" reasoning OnStrikeHealAllyAugment's own identical swap uses (see
        // CharacterStatsPD.HealFromAlly's own doc comment). Stub (2026-08-30) — HealFromAlly is
        // CharacterStatsPD-specific, not on IFriendlyPD yet (ally was widened as part of the combat-targeting
        // migration); same "stub for now" call as OnStrikeHealAllyAugment/DamageNumberManager's own
        // IFriendlyPD overloads — a future non-player ally just doesn't get healed by this augment yet.
        if (ReferenceEquals(ally, Stats)) Stats.Heal(totalHeal);
        else if (ally is CharacterStatsPD player) player.HealFromAlly(totalHeal, Stats);
    }
}
