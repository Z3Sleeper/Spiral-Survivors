using PurrNet.Prediction;
using UnityEngine;

// Reworked (2026-08-21) from SurgeInterruptOnStrikeAugment — the old "interrupt on every Strike while
// Surging" design was unplayable to balance (too strong with a fast weapon, too weak with a slow one, since
// its power scaled with attack rate rather than anything this augment itself controls). Replaced the whole
// trigger mechanism; kept the one part that worked. The Surge Cost tax that briefly existed on the old
// design is also removed — this rework's own AoE cast is a self-contained cost/reward, it doesn't need an
// extra tax bolted on to balance a mechanic that no longer exists. Two parts, both on this one augment
// (Surge/Energy family):
//
// 1. (AoE Effects) On Surge Start AND Surge End: deal damagePercent unpenalized Strength, apply normal
//    player knockback (Stats.Knockback, unscaled — no augment-specific multiplier), and Interrupt every
//    enemy in radius, via the shared AreaEffectCastHelper/AreaEffectPD chain (same one
//    DashExplosionAugment/ShieldBreakBurstAugment use) — damage scales with Stats.AreaEffectPower, radius
//    with Stats.AreaEffectSize (via Stats.RangeScalingAoEBase * radiusMultiplier, the standardized "AoE
//    Effects" radius formula), and the whole cast repeats Stats.AreaEffectCount times. notifyOnHit: false
//    (explicit design — this shouldn't combo with onhit-proc augments, matching ShieldBreakBurstAugment's
//    own precedent). A successful interrupt is paired with CharacterStatsPD.NotifyInterrupted(enemy, ref
//    currentState.firedFsiThisBurst) — currentState.firedFsiThisBurst resets at the START of EACH burst
//    (both OnSurgeStarted and OnSurgeEnded, not just once per whole surge), so Surge Start's burst gets its
//    own "first successful interrupt fires the GLOBAL FirstSuccessfulInterrupt (FSI) event" opportunity, and
//    Surge End's burst independently gets its own fresh one too — every OTHER interrupt within either single
//    burst only fires the plain SuccessfulInterrupt (SI) event, same per-activation scoping the old
//    on-strike design used, just now per-burst instead of per-strike.
//
// 2. Passive, always active regardless of Surge state, UNCHANGED from the old design: subscribes to the
//    GENERIC SuccessfulInterrupt (SI) event — fires on EVERY successful interrupt, from ANY source (a dash,
//    AxeThrow, Knight's stun heal, this augment's own AoE bursts above, any future source too). Grants
//    energyRegenPerStack permanent Energy Regen per stack via plain AddEnergyRegenFlat, scaled ONLY by
//    Stats.StackPower — not onHitPower/luck/crit or anything else a per-hit effect would normally roll
//    through, since this isn't triggered by a hit landing, just by the SI notification itself. Recomputes
//    its OWN total contribution from scratch on every new stack (remove exactly what was last applied,
//    reapply fresh against the CURRENT StackPower) rather than incrementally adding per stack — same
//    "re-derive fully" principle OnEnemyDeathHealthStackingAugment/SurgeStackingAttackSpeedAugment already
//    use, so a later StackPowerAugment pick retroactively boosts every stack already earned, not just future
//    ones. No cap, never resets — permanent stacking for the rest of this run.
public class SurgeInterruptAoEAugment : AugmentEffectPD<SurgeInterruptAoEAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Gates the GLOBAL FirstSuccessfulInterrupt event's per-activation scoping for OTHER listeners only —
        // see class doc comment #1. Reset at the start of EACH burst (both OnSurgeStarted and OnSurgeEnded),
        // not shared across the whole surge — each burst gets its own independent FSI opportunity.
        public bool  firedFsiThisBurst;
        // Permanent — never reset, unlike the field above.
        public int   stackCount;
        public void Dispose() { }
    }

    [Header("AoE Effect — On Surge Start/End")]
    public float damagePercent = 0.5f;
    // Radius = Stats.RangeScalingAoEBase * radiusMultiplier — standardized "AoE Effects" formula (see class
    // doc comment). 1 is the neutral default; tune per-augment feel without touching the WeaponRange/
    // AreaEffectSize scaling itself.
    [SerializeField] private float radiusMultiplier = 1.25f;
    // How long between each retrigger cast when Stats.AreaEffectCount > 1 — exposed for tuning.
    [SerializeField] private float retriggerRate = 0.5f;

    [Header("VFX")]
    // Duration is not exposed here — RadiusIndicatorVFXPD owns its own playback timing on the prefab itself.
    [SerializeField] private GameObject radiusVFXPrefab;

    // Spawned on every Surge Start/End — pooled, so this is cheap even when AreaEffectCount is just 1. Must
    // be registered in PredictedPrefabs.asset like any other spawned effect. Can reuse the exact same
    // AreaEffectPD prefab DashExplosionAugment/ShieldBreakBurstAugment use, since the class itself isn't
    // tied to any one augment.
    [Header("Chain (spawned on every Surge Start/End)")]
    [SerializeField] private GameObject chainPrefab;

    [Header("Passive — Stacking Energy Regen on Successful Interrupt")]
    public float energyRegenPerStack = 0.1f;

    // Plain view-layer bookkeeping, not State — tracks what this effect has ACTUALLY applied to
    // CharacterStatsPD so it can be reversed exactly, same role as OnEnemyDeathHealthStackingAugment's own
    // _lastAppliedBonus.
    private float _lastAppliedEnergyRegen;

    // Mirrors AreaEffectCastHelper.Cast's own damage formula (abilityPower left at its neutral default here,
    // so areaAndAbilityPower reduces to exactly Stats.AreaEffectPower — see that method's own doc comment),
    // minus the luck/crit roll (baseline preview, not a randomly-sampled one — see
    // OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the full convention), for
    // AugmentDescriptionFormatter's {LiveStrengthRatio} token.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.Strength * damagePercent * stats.AreaEffectPower)} "
            : "";

    // Current total Energy Regen this passive has actually granted so far (energyRegenPerStack * current
    // stack count * StackPower) for AugmentDescriptionFormatter's {LiveStackedEnergyRegen} token. Reads
    // currentState.stackCount directly — safe now (2026-08-19) because AugmentDescriptionFormatter.Format
    // resolves token methods against the real per-player spawned instance (via AugmentManagerPD's own
    // registry) rather than the dead effectPrefab template whenever one exists, so `this` here is the real
    // live instance once picked. Falls back to the prefab's own untouched 0 default for a not-yet-picked
    // level-up option, which is exactly the "0 stacks" state we want to show there anyway. Defaults to "0"
    // (not blank) outside a live match too — 0 IS the real current value in both cases, not a missing one.
    public string LiveStackedEnergyRegen(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(energyRegenPerStack * stats.StackPower * currentState.stackCount)} "
            : "0 ";

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
        Stats.AddSurgeEndedListener(OnSurgeEnded);
        Stats.AddSuccessfulInterruptListener(OnSuccessfulInterrupt);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats == null) return;
        Stats.RemoveSurgeStartedListener(OnSurgeStarted);
        Stats.RemoveSurgeEndedListener(OnSurgeEnded);
        Stats.RemoveSuccessfulInterruptListener(OnSuccessfulInterrupt);
    }

    private void OnSurgeStarted()
    {
        currentState.firedFsiThisBurst = false;
        CastAoE();
    }

    private void OnSurgeEnded()
    {
        currentState.firedFsiThisBurst = false;
        CastAoE();
    }

    private void CastAoE()
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
                dealDamage: true, damagePercent: damagePercent, notifyOnHit: false,
                hasKnockback: true, knockback: Stats.Knockback,
                interrupt: true, onSuccessfulInterrupt: OnAoEInterrupt, vfxPerCast: true);
    }

    // Named method, not an inline lambda — currentState is a ref-returning property on the PD base class, and
    // a ref field access like this can't be captured inside a C# closure. A plain instance method sidesteps
    // that entirely since it re-resolves currentState fresh each time it's actually invoked.
    private void OnAoEInterrupt(IEnemyPD enemy) => Stats.NotifyInterrupted(enemy, ref currentState.firedFsiThisBurst);

    // Fires on EVERY successful interrupt, any source — see class doc comment #2 for why SI, not FSI.
    private void OnSuccessfulInterrupt()
    {
        currentState.stackCount++;

        Stats.AddEnergyRegenFlat(-_lastAppliedEnergyRegen);
        _lastAppliedEnergyRegen = energyRegenPerStack * currentState.stackCount * Stats.StackPower;
        Stats.AddEnergyRegenFlat(_lastAppliedEnergyRegen);
    }
}
