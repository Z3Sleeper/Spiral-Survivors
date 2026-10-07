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
// 1. (AoE Effects) On EVERY Surge End, gated by an energy-SPENT requirement instead of a full-energy
//    requirement (2026-09-06 — moved from Surge Start; a much more natural activation path, since ending
//    Surge is exactly when you've actually finished spending the energy that gates this, rather than
//    checking spent-so-far at the moment a fresh Surge merely begins. 2026-08-29 rework #2 before that was
//    "only if that Surge started at full energy," which itself replaced an even earlier "every Surge Start
//    AND every Surge End, unconditionally"; both of those were live-tested and found to either spam
//    trivially or force an awkward always-be-at-max-energy playstyle). Instead:
//    currentState.energySpentSinceLastBurst accumulates every CharacterStatsPD._onEnergySpent event (see
//    EnergySpentData's own doc comment — Surge's own activation cost AND its ongoing per-tick drain both
//    route through this one event, "the only energy-spend source in this codebase") since the last
//    qualifying burst; a Surge End only actually casts once that running total reaches
//    energySpentRequirement (exposed, tunable — default 50), then resets it to 0 for the next burst to
//    accumulate toward. The VERY FIRST Surge End after picking this augment always qualifies regardless of
//    energy-spent history — GetInitialState() seeds energySpentSinceLastBurst to float.MaxValue rather than
//    0, so there's no awkward "can't use it at all until you've already spent 50 energy on nothing" dead
//    window right after picking it. This resolves the spamming problem the earlier full-energy gate was
//    fighting without imposing a "must be at max energy to activate Surge at all" restriction on how the
//    player actually plays — pure Surge-usage volume gates it now, not a snapshot of energy at the instant
//    of activation. Fires on ANY Surge ending (normal or Berserk/forced) — no special-casing between them,
//    since the gate is purely about energy spent, not which kind of Surge produced it.
//
//    Interrupt every enemy in radius, via the shared AreaEffectCastHelper/AreaEffectPD chain (same one
//    DashExplosionAugment/ShieldBreakBurstAugment use) — radius scales with Stats.AreaEffectSize (via
//    Stats.RangeScalingAoEBase * radiusMultiplier, the standardized "AoE Effects" radius formula) — a flat
//    single cast (Stats.AreaEffectCount, the stat that used to let this repeat, was removed entirely
//    2026-09-13). Pure interrupt (+ a paired stun, see below) only
//    (2026-08-25, was also dealing damagePercent unpenalized Strength and applying normal player knockback —
//    removed, this augment has no damage/knockback component at all) — dealDamage/hasKnockback both left at
//    Launch()'s own default (false), same "leave it at default rather than pass it" convention
//    AreaEffectCastHelper's own doc comment establishes for a non-damaging cast.
//
//    A successful interrupt is paired with CharacterStatsPD.NotifyInterrupted(enemy, ref
//    currentState.firedFsiThisBurst, pairedStunDuration) — currentState.firedFsiThisBurst resets at the
//    start of each qualifying burst, so every burst gets its own independent "first successful interrupt
//    fires the GLOBAL FirstSuccessfulInterrupt (FSI) event" opportunity; every OTHER interrupt within that
//    same burst only fires the plain SuccessfulInterrupt (SI) event, same per-activation scoping the old
//    on-strike design used, just now per-burst instead of per-strike. pairedStunDuration (2026-09-06, added
//    back — default 1f) rides NotifyInterrupted's own existing "additional stun on top of the automatic
//    flat interrupt-stun" parameter (see its own doc comment — the two combine via Mathf.Max into one total
//    true-stun, this portion IS scaled by crowdControlResist unlike the automatic flat portion).
//
//    CastAoE's Launch() call ALSO passes pairedStunDuration as a plain onEnemyHit Stun() (2026-09-08 fix,
//    was missing — matches KnightBuffStunAbilityPD's own identical dual-layer shape) — Interrupt()
//    itself only ever succeeds against an enemy currently CanBeInterrupted (mid-windup/dash/attack), so
//    relying on the NotifyInterrupted-paired stun ALONE meant an enemy simply idling/walking when the burst
//    landed got no stun at all. Now every enemy this cast hits gets the plain Stun() unconditionally, and
//    one ALSO successfully interrupted additionally gets InterruptTrueStun's bonus layered on top —
//    ApplyStun's own Mathf.Max semantics mean the two calls can never fight each other.
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
        // below, not 0, so the very first Surge End after picking this augment always qualifies.
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

    // Requirement is exposed (2026-08-29) so it can be tuned without a recompile — default 50. Whole numbers
    // only (2026-09-17, was a plain float) — Surge Cost itself is always a whole number in practice, and
    // this gate is denominated in the same units, so this is now enforced at the type level rather than left
    // as an unenforced convention. SurgeEnergySpentMoveSpeedAugment's own energySpentRequirement got the
    // identical treatment, same day, same reasoning. Public, not [SerializeField] private (2026-09-30 fix)
    // — so AugmentDescriptionFormatter's reflection can resolve {energySpentRequirement} directly as a
    // description token; only public fields/properties/methods are checked. Still Inspector-serializable
    // either way.
    public int energySpentRequirement = 50;

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

    [Header("AoE Effect — On Surge End (gated by energy spent)")]
    // Radius = Stats.RangeScalingAoEBase * radiusMultiplier — standardized "AoE Effects" formula (see class
    // doc comment). 1 is the neutral default; tune per-augment feel without touching the WeaponRange/
    // AreaEffectSize scaling itself. Public, not [SerializeField] private (2026-09-30 fix) — so
    // AugmentDescriptionFormatter's reflection can resolve {radiusMultiplier} directly as a description
    // token; only public fields/properties/methods are checked. Still Inspector-serializable either way.
    public float radiusMultiplier = 1.25f;
    // Rides NotifyInterrupted's own "additional stun on top of the automatic flat interrupt-stun" parameter
    // (2026-09-06, added back) — see class doc comment #1 for the full combination shape. Public, not
    // [SerializeField] private (2026-09-30 fix) — so AugmentDescriptionFormatter's reflection can resolve
    // {pairedStunDuration} directly as a description token; only public fields/properties/methods are
    // checked. Still Inspector-serializable either way.
    public float pairedStunDuration = 1f;

    // Spawned on every qualifying Surge End — pooled, so this is cheap even when AreaEffectCount is just
    // 1. Must be registered in PredictedPrefabs.asset like any other spawned effect. Can reuse the exact
    // same AreaEffectPD prefab DashExplosionAugment/ShieldBreakBurstAugment use, since the class itself
    // isn't tied to any one augment.
    [Header("Chain (spawned on a qualifying Surge End)")]
    [SerializeField] private GameObject chainPrefab;

    // Permanent buff-bar icon (2026-08-29) — unlike every duration-timed buff icon elsewhere in this
    // codebase (SurgeEnergySpentMoveSpeedAugment, OnDodgeGain*), this one is never removed once granted; it's
    // always visible for the rest of the match once picked, tracking gate PROGRESS instead of a countdown.
    // See UpdateView below for the fill/text mapping.
    [Header("Buff Bar")]
    [SerializeField] private Sprite buffIcon;
    private BuffIconPD _buffIconInstance;

    protected override void OnApply()
    {
        Stats.AddSurgeEndedListener(OnSurgeEnded);
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
        Stats.RemoveSurgeEndedListener(OnSurgeEnded);
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
    // #1 for the full gate mechanic (and why the very first Surge End always qualifies regardless).
    private void OnSurgeEnded()
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

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            // onEnemyHit (2026-09-08 fix, was missing entirely) — same layering shape
            // KnightBuffStunAbilityPD's own TriggerAoE uses: pairedStunDuration lands on EVERY enemy
            // this cast hits, unconditionally, via a direct Stun() call. Without this, the ONLY stun this
            // augment ever applied was NotifyInterrupted's own paired-stun bonus below, which only fires for
            // enemies Interrupt() actually succeeds on (CanBeInterrupted true — mid-windup/dash/attack) — an
            // enemy simply idling or walking when the burst landed got no stun at all. Now every hit enemy
            // gets the plain Stun() unconditionally, and an enemy ALSO successfully interrupted additionally
            // gets InterruptTrueStun's bonus layered on top via OnAoEInterrupt below — ApplyStun's own
            // Mathf.Max semantics mean the two calls can never fight each other.
            chain.Launch(Stats, castPosition, radius, null,
                interrupt: true, onEnemyHit: enemy => enemy.Stun(pairedStunDuration),
                onSuccessfulInterrupt: OnAoEInterrupt, vfxPerCast: true);

        // Shockwave VFX opt-in (2026-09-26, user-curated, replaces the old flat-cylinder radiusVFXPrefab
        // stand-in) — safe unconditionally: this callback only ever runs once per real qualifying Surge End,
        // off CharacterStatsPD._onSurgeEnded, a genuine PredictedEvent. Player color for both start/end (no
        // override requested for this one).
        VFXManager.instance?.PlayShockwaveVfx(castPosition, Stats.Size, Stats.PlayerColor, Stats.PlayerColor, radius);
    }

    // Named method, not an inline lambda — currentState is a ref-returning property on the PD base class, and
    // a ref field access like this can't be captured inside a C# closure. A plain instance method sidesteps
    // that entirely since it re-resolves currentState fresh each time it's actually invoked.
    private void OnAoEInterrupt(IEnemyPD enemy) => Stats.NotifyInterrupted(enemy, ref currentState.firedFsiThisBurst, pairedStunDuration);

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
