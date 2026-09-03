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
// 1. (AoE Effects) On EVERY Surge Start, gated by an energy-SPENT requirement instead of a full-energy
//    requirement (2026-08-29 rework #2 — was "only if that Surge started at full energy," which itself
//    replaced an even earlier "every Surge Start AND every Surge End, unconditionally"; both of those were
//    live-tested and found to either spam trivially or force an awkward always-be-at-max-energy playstyle).
//    Instead: currentState.energySpentSinceLastBurst accumulates every CharacterStatsPD._onEnergySpent
//    event (see EnergySpentData's own doc comment — Surge's own activation cost AND its ongoing per-tick
//    drain both route through this one event, "the only energy-spend source in this codebase") since the
//    last qualifying burst; a Surge Start only actually casts once that running total reaches
//    energySpentRequirement (exposed, tunable — default 50), then resets it to 0 for the next burst to
//    accumulate toward. The VERY FIRST Surge Start after picking this augment always qualifies regardless
//    of energy-spent history — GetInitialState() seeds energySpentSinceLastBurst to float.MaxValue rather
//    than 0, so there's no awkward "can't use it at all until you've already spent 50 energy on nothing"
//    dead window right after picking it. This resolves the spamming problem the earlier full-energy gate
//    was fighting without imposing a "must be at max energy to activate Surge at all" restriction on how the
//    player actually plays — pure Surge-usage volume gates it now, not a snapshot of energy at the instant
//    of activation.
//
//    Interrupt every enemy in radius, via the shared AreaEffectCastHelper/AreaEffectPD chain (same one
//    DashExplosionAugment/ShieldBreakBurstAugment use) — radius scales with Stats.AreaEffectSize (via
//    Stats.RangeScalingAoEBase * radiusMultiplier, the standardized "AoE Effects" radius formula), and the
//    whole cast repeats Stats.AreaEffectCount times. Pure interrupt only (2026-08-25, was also dealing
//    damagePercent unpenalized Strength and applying normal player knockback — removed, this augment is
//    interrupt-only now, no damage/knockback component at all) — dealDamage/hasKnockback both left at
//    Launch()'s own default (false), same "leave it at default rather than pass it" convention
//    AreaEffectCastHelper's own doc comment establishes for a non-damaging cast.
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
//    this augment's own AoE burst above, any future source too). Grants +maxEnergyPerStack permanent,
//    stacking Max Energy, scaled by Stats.StackPower (2026-09-02 — was "instantly refill Energy to full",
//    2026-08-25; was RechargeShield() before that; was permanent stacking Energy Regen before THAT — this
//    is effectively a return to that original stacking-permanent shape, just Max Energy instead of Energy
//    Regen). Recomputes its OWN total contribution from scratch on every stack (remove exactly what was
//    last applied, reapply fresh against the CURRENT StackPower) rather than incrementally adding per
//    stack — same "re-derive fully, don't incrementally adjust" principle DashThroughInterruptAugment's own
//    identically-shaped stacking Dash Haste grant already uses, so a later StackPowerAugment pick
//    retroactively boosts every stack already earned, not just future ones. No cap, never resets —
//    permanent stacking for the rest of this run. Untouched by either gate rework above — this half was
//    never the problem.
public class SurgeInterruptAoEAugment : AugmentEffectPD<SurgeInterruptAoEAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Gates the GLOBAL FirstSuccessfulInterrupt event's per-activation scoping for OTHER listeners only —
        // see class doc comment #1. Reset at the start of each qualifying burst.
        public bool  firedFsiThisBurst;
        // Running total of energy spent (CharacterStatsPD._onEnergySpent) since the last qualifying burst —
        // see class doc comment #1 for the full gate mechanic. Seeded to float.MaxValue by GetInitialState()
        // below, not 0, so the very first Surge Start after picking this augment always qualifies.
        public float energySpentSinceLastBurst;
        // Permanent — never reset, unlike energySpentSinceLastBurst above. See OnSuccessfulInterrupt's own
        // doc comment for the stacking Max Energy grant this drives.
        public int   stackCount;
        public void Dispose() { }
    }

    protected override State GetInitialState()
    {
        var s = default(State);
        s.energySpentSinceLastBurst = float.MaxValue;
        return s;
    }

    // Requirement is exposed (2026-08-29) so it can be tuned without a recompile — default 50.
    [Header("Activation Gate")]
    [SerializeField] private float energySpentRequirement = 50f;

    [Header("On Successful Interrupt — Stacking Permanent Max Energy")]
    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {maxEnergyPerStack} directly in the description text — see LiveStackedMaxEnergy's own doc comment
    // (only public fields/properties/methods are checked).
    public float maxEnergyPerStack = 1f;

    // Current total Max Energy this passive has actually granted so far (maxEnergyPerStack * current stack
    // count * StackPower) for AugmentDescriptionFormatter's {LiveStackedMaxEnergy} token — same shape/
    // reasoning as DashThroughInterruptAugment.LiveStackedDashHaste's own identical token. Falls back to the
    // prefab's own untouched 0 default for a not-yet-picked level-up option, which is exactly the "0 stacks"
    // state we want to show there anyway.
    public string LiveStackedMaxEnergy(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(maxEnergyPerStack * stats.StackPower * currentState.stackCount)}"
            : "0";

    // Tracks what this effect has ACTUALLY applied to CharacterStatsPD so it can be reversed exactly and
    // reapplied fresh — same role as DashThroughInterruptAugment's own _lastAppliedDashHaste. Plain, not
    // State — mutated only from OnSuccessfulInterrupt's own PredictedEvent listener callback, which (like
    // every PredictedEvent in this class) fires exactly once per real occurrence regardless of resimulation.
    private float _lastAppliedMaxEnergy;

    [Header("AoE Effect — On Surge Start (gated by energy spent)")]
    // Radius = Stats.RangeScalingAoEBase * radiusMultiplier — standardized "AoE Effects" formula (see class
    // doc comment). 1 is the neutral default; tune per-augment feel without touching the WeaponRange/
    // AreaEffectSize scaling itself.
    [SerializeField] private float radiusMultiplier = 1.25f;
    // How long between each retrigger cast when Stats.AreaEffectCount > 1 — exposed for tuning.
    [SerializeField] private float retriggerRate = 0.5f;

    [Header("VFX")]
    // Duration is not exposed here — RadiusIndicatorVFXPD owns its own playback timing on the prefab itself.
    [SerializeField] private GameObject radiusVFXPrefab;

    // Spawned on every qualifying Surge Start — pooled, so this is cheap even when AreaEffectCount is just
    // 1. Must be registered in PredictedPrefabs.asset like any other spawned effect. Can reuse the exact
    // same AreaEffectPD prefab DashExplosionAugment/ShieldBreakBurstAugment use, since the class itself
    // isn't tied to any one augment.
    [Header("Chain (spawned on a qualifying Surge Start)")]
    [SerializeField] private GameObject chainPrefab;

    // Permanent buff-bar icon (2026-08-29) — unlike every duration-timed buff icon elsewhere in this
    // codebase (SurgeEndMoveSpeedAugment, OnFirstDodgeGain*), this one is never removed once granted; it's
    // always visible for the rest of the match once picked, tracking gate PROGRESS instead of a countdown.
    // See UpdateView below for the fill/text mapping.
    [Header("Buff Bar")]
    [SerializeField] private Sprite buffIcon;
    private BuffIconPD _buffIconInstance;

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
        Stats.AddSuccessfulInterruptListener(OnSuccessfulInterrupt);
        Stats.AddEnergySpentListener(OnEnergySpent);
        // Re-values every already-earned stack the instant StackPower itself changes (2026-09-02), not just
        // lazily on the next new stack — see CharacterStatsPD._onStackPowerChanged's own doc comment.
        Stats.AddStackPowerChangedListener(OnStackPowerChanged);

        if (isOwner && BuffBarPD.instance != null && _buffIconInstance == null)
            _buffIconInstance = BuffBarPD.instance.AddBuffIcon(buffIcon);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_buffIconInstance != null && BuffBarPD.instance != null)
            BuffBarPD.instance.RemoveBuffIcon(_buffIconInstance);
        if (Stats == null) return;
        Stats.RemoveSurgeStartedListener(OnSurgeStarted);
        Stats.RemoveSuccessfulInterruptListener(OnSuccessfulInterrupt);
        Stats.RemoveEnergySpentListener(OnEnergySpent);
        Stats.RemoveStackPowerChangedListener(OnStackPowerChanged);
    }

    // Reuses BuffIconPD's own established cooldown-sweep semantics (0 = fully ready/available, sweep empty;
    // 1 = just triggered/fully "on cooldown," sweep full — see SetCooldownFill's own doc comment) but keyed
    // off energySpentSinceLastBurst instead of a countdown timer: "expired" (just fired, 0 energy spent yet
    // toward the next one) reads as a full sweep, "fresh" (energySpentRequirement reached, ready to fire
    // again) reads as an empty sweep — the exact same visual grammar every duration-timed buff icon in this
    // codebase already uses, just counting UP toward ready instead of DOWN toward expiry. Clamped so the
    // seeded-float.MaxValue first-use state reads as fully ready (empty sweep, "READY" text) instead of
    // overflowing into nonsense. requirement floored at a tiny epsilon to avoid a divide-by-zero if
    // energySpentRequirement is ever authored as 0.
    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);
        if (_buffIconInstance == null) return;

        float requirement = Mathf.Max(0.0001f, energySpentRequirement);
        float clampedSpent = Mathf.Clamp(viewState.energySpentSinceLastBurst, 0f, requirement);

        _buffIconInstance.SetCooldownFill(1f - clampedSpent / requirement);
        // "READY" once the gate is actually satisfied (2026-09-02, was always "x/requirement", including the
        // not-very-readable "50/50" at full) — clampedSpent can only ever reach requirement exactly (it's
        // clamped to it), so this check is exact, not an approximation.
        _buffIconInstance.SetStackText(clampedSpent >= requirement ? "READY" : $"{Mathf.RoundToInt(clampedSpent)}/{Mathf.RoundToInt(requirement)}");
    }

    // Accumulates toward energySpentRequirement — see class doc comment #1. Every source of energy spend in
    // this codebase routes through this one event (EnergySpentData's own doc comment), so this doesn't need
    // to know or care whether a given spend came from Surge's own activation cost or its ongoing drain tick.
    private void OnEnergySpent(EnergySpentData data) => currentState.energySpentSinceLastBurst += data.amount;

    // Only bursts once energySpentSinceLastBurst has reached energySpentRequirement — see class doc comment
    // #1 for the full gate mechanic (and why the very first Surge Start always qualifies regardless).
    private void OnSurgeStarted()
    {
        if (Stats == null) return;
        if (currentState.energySpentSinceLastBurst < energySpentRequirement) return;

        currentState.energySpentSinceLastBurst = 0f;
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

    // Fires on EVERY successful interrupt, any source — see class doc comment #2 for why SI, not FSI, and
    // for the full stacking-grant reasoning.
    private void OnSuccessfulInterrupt()
    {
        if (Stats == null || Stats.IsDead) return;
        currentState.stackCount++;
        ReapplyMaxEnergyBonus();
    }

    // Re-derives this augment's OWN total Max Energy contribution from scratch (remove exactly what was
    // last applied, reapply fresh against the CURRENT stackCount/StackPower) — shared by both triggers that
    // can change the total: earning a new stack (OnSuccessfulInterrupt above) and StackPower itself
    // changing (OnStackPowerChanged below), so the two can never drift out of sync with each other.
    private void ReapplyMaxEnergyBonus()
    {
        Stats.AddMaxEnergyFlat(-_lastAppliedMaxEnergy);
        _lastAppliedMaxEnergy = maxEnergyPerStack * currentState.stackCount * Stats.StackPower;
        Stats.AddMaxEnergyFlat(_lastAppliedMaxEnergy);
    }

    // Fires whenever Stats.StackPower itself changes (e.g. a StackPowerAugment picked mid-run) — reapplies
    // immediately rather than leaving every already-earned stack sitting at a stale total until the next
    // successful interrupt happens to come in. No-op at 0 stacks (nothing earned yet to re-value).
    private void OnStackPowerChanged()
    {
        if (Stats == null || Stats.IsDead || currentState.stackCount <= 0) return;
        ReapplyMaxEnergyBonus();
    }
}
