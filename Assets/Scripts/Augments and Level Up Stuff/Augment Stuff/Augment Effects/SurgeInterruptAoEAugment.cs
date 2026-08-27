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
// 1. (AoE Effects) On Surge Start ONLY, and ONLY if that Surge started at full energy (2026-08-24, was
//    "every Surge Start AND every Surge End, unconditionally" — live-tested and found gamebreakingly
//    strong, since a player could just repeatedly toggle Surge on/off for a free AoE burst every single
//    time regardless of energy investment): Interrupt every enemy in radius, via the shared
//    AreaEffectCastHelper/AreaEffectPD chain (same one DashExplosionAugment/ShieldBreakBurstAugment use) —
//    radius scales with Stats.AreaEffectSize (via Stats.RangeScalingAoEBase * radiusMultiplier, the
//    standardized "AoE Effects" radius formula), and the whole cast repeats Stats.AreaEffectCount times.
//    Pure interrupt only (2026-08-25, was also dealing damagePercent unpenalized Strength and applying
//    normal player knockback — removed, this augment is interrupt-only now, no damage/knockback component
//    at all) — dealDamage/hasKnockback both left at Launch()'s own default (false), same "leave it at
//    default rather than pass it" convention AreaEffectCastHelper's own doc comment establishes for a
//    non-damaging cast.
//
//    "Started at full energy" check mirrors FullEnergyBerserkSurgeAugment's own identical reconstruction —
//    _onSurgeStarted fires AFTER StartSurge() has already deducted SurgeCost from currentState.energy, so
//    "was full at the moment of activation" can't be read directly off CurrentEnergy once this listener
//    runs; it has to add SurgeCost back to reconstruct the pre-deduction value. The same check is also
//    correct (trivially true) for a Berserk Surge start — StartBerserkSurge() sets energy to MaxEnergy
//    BEFORE firing _onSurgeStarted, so CurrentEnergy already equals MaxEnergy there and adding SurgeCost on
//    top only keeps it >= MaxEnergy — no separate branch needed for the two cases.
//
//    A successful interrupt is paired with CharacterStatsPD.NotifyInterrupted(enemy, ref
//    currentState.firedFsiThisBurst) — currentState.firedFsiThisBurst resets at the start of each qualifying
//    burst, so every burst gets its own independent "first successful interrupt fires the GLOBAL
//    FirstSuccessfulInterrupt (FSI) event" opportunity; every OTHER interrupt within that same burst only
//    fires the plain SuccessfulInterrupt (SI) event, same per-activation scoping the old on-strike design
//    used, just now per-burst instead of per-strike.
//
// 2. Passive, always active regardless of Surge state: subscribes to the GENERIC SuccessfulInterrupt (SI)
//    event — fires on EVERY successful interrupt, from ANY source (a dash, AxeThrow, Knight's stun heal,
//    this augment's own AoE burst above, any future source too). Instantly refills Energy to full
//    (2026-08-25, was RechargeShield() — was permanent stacking Energy Regen before that — see
//    CharacterStatsPD.GainEnergy's own clamped-gain shape). No cooldown/cap of its own — an interrupt
//    landing while Energy's already full is just a harmless no-op, same shape GainEnergy already has
//    everywhere else it's called. Untouched by the 2026-08-24 fix above — this half was never the problem.
public class SurgeInterruptAoEAugment : AugmentEffectPD<SurgeInterruptAoEAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Gates the GLOBAL FirstSuccessfulInterrupt event's per-activation scoping for OTHER listeners only —
        // see class doc comment #1. Reset at the start of each qualifying burst (a full-energy Surge Start).
        public bool  firedFsiThisBurst;
        public void Dispose() { }
    }

    [Header("AoE Effect — On Surge Start (full energy only)")]
    // Radius = Stats.RangeScalingAoEBase * radiusMultiplier — standardized "AoE Effects" formula (see class
    // doc comment). 1 is the neutral default; tune per-augment feel without touching the WeaponRange/
    // AreaEffectSize scaling itself.
    [SerializeField] private float radiusMultiplier = 1.25f;
    // How long between each retrigger cast when Stats.AreaEffectCount > 1 — exposed for tuning.
    [SerializeField] private float retriggerRate = 0.5f;

    [Header("VFX")]
    // Duration is not exposed here — RadiusIndicatorVFXPD owns its own playback timing on the prefab itself.
    [SerializeField] private GameObject radiusVFXPrefab;

    // Spawned on every qualifying (full-energy) Surge Start — pooled, so this is cheap even when
    // AreaEffectCount is just 1. Must be registered in PredictedPrefabs.asset like any other spawned effect.
    // Can reuse the exact same AreaEffectPD prefab DashExplosionAugment/ShieldBreakBurstAugment use, since
    // the class itself isn't tied to any one augment.
    [Header("Chain (spawned on a qualifying Surge Start)")]
    [SerializeField] private GameObject chainPrefab;

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
        Stats.AddSuccessfulInterruptListener(OnSuccessfulInterrupt);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats == null) return;
        Stats.RemoveSurgeStartedListener(OnSurgeStarted);
        Stats.RemoveSuccessfulInterruptListener(OnSuccessfulInterrupt);
    }

    // Only bursts if this Surge started at full energy — see class doc comment #1 for the full
    // reconstruction reasoning (same check FullEnergyBerserkSurgeAugment uses).
    private void OnSurgeStarted()
    {
        if (Stats == null) return;
        if (Stats.CurrentEnergy + Stats.SurgeCost < Stats.MaxEnergy) return;

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
                interrupt: true, onSuccessfulInterrupt: OnAoEInterrupt, vfxPerCast: true);
    }

    // Named method, not an inline lambda — currentState is a ref-returning property on the PD base class, and
    // a ref field access like this can't be captured inside a C# closure. A plain instance method sidesteps
    // that entirely since it re-resolves currentState fresh each time it's actually invoked.
    private void OnAoEInterrupt(IEnemyPD enemy) => Stats.NotifyInterrupted(enemy, ref currentState.firedFsiThisBurst);

    // Fires on EVERY successful interrupt, any source — see class doc comment #2 for why SI, not FSI.
    private void OnSuccessfulInterrupt() => Stats.GainEnergy(Stats.MaxEnergy);
}
