using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Two parts, both on this one augment:
// 1. Dashing through an enemy interrupts it (cancels whatever attack sequence it's mid-way through, same as
//    any other IEnemyPD.Interrupt() caller). No damage, no knockback — purely a disruption tool. Every
//    successful interrupt during the sweep invokes CharacterStatsPD's generic SuccessfulInterrupt (SI)
//    event; the FIRST successful interrupt of a given dash also invokes FirstSuccessfulInterrupt (FSI) —
//    see CharacterStatsPD.NotifyInterrupted. This lets any OTHER "on first successful interrupt" passive
//    react correctly when triggered via a dash, not just this augment's own refund below.
// 2. This same augment also subscribes to the generic SI event (2026-08-21 — was FSI; switched to SI so
//    EVERY successful interrupt grants a stack, not just the first one per dash) and grants
//    dashHastePerStack (default 1) permanent, stacking Dash Haste every time it fires — from ITS OWN sweep
//    (one hop: sweep fires SI, this augment's own listener reacts, potentially several times per dash if
//    multiple enemies get interrupted in one sweep) or from any OTHER interrupt source (AxeThrow, Knight
//    StunHeal) while this augment is active. Scales with Stats.StackPower, same "recompute fully, reapply
//    fresh" shape SurgeInterruptAoEAugment's own stacking Energy Regen passive uses (see its own doc
//    comment) — remove exactly what was last applied, reapply against the CURRENT StackPower, so a later
//    StackPowerAugment pick retroactively boosts every stack already earned, not just future ones. No cap,
//    never resets — permanent stacking for the rest of this run.
//

// Detection is a swept sphere-cast following the player each tick while dashing — same pattern
// EnemyBasePD.HandleKillLaunch already uses for its own corpse-launch hit detection (prevPos -> currentPos
// segment, SphereCastNonAlloc against CombatLayerMasks.EnemyHurtboxMask), adapted for a DIFFERENT identity's
// movement: since this augment isn't the one moving, sweepPrevPos has to live in ITS OWN predicted State,
// updated in LateSimulate() (after DoPhysicsPass() has resolved the player's own dash movement for this
// tick), not Simulate(). touchedEnemies (a DisposableList<PredictedObjectID>, same convention as
// EnemyBaseState.killLaunchHitIds) dedupes repeat hits on an enemy that stays overlapped across several ticks
// of the same dash.
//
// Driven by PlayerControllerPD's DashMovementStarted/DashMovementEnded (2026-08-13 — previously
// DashStarted/DashEnded, see below), NOT IsDashing directly — RogueSpinAbilityPD's own leap and
// BWBlinkAbilityPD's teleport both explicitly fire this pair alongside their own NotifyDashStarted/
// NotifyDashEnded calls to hook into it, even though neither ever sets isDashing itself. Gating on the event
// pair covers a real dash, Rogue's leap, and Blink's teleport automatically, same as the rest of the family.
//
// Uses the MOVEMENT pair specifically, not Started/Ended (the reward pair every other "Dash...Augment"
// reacts to), for two reasons: (1) DashMovementEnded fires unconditionally, on an interrupted close as well
// as a natural one, so this single subscription replaces what used to be a three-way DashEnded/
// DashInterrupted/SpecialMovementInterrupted subscription needed to catch every way the window could close
// (missing any one of those three used to leave isSweeping stuck true forever after that specific kind of
// interrupt); (2) a "trigger this dash's effects again" augment (DashEffectDoubleTriggerAugment) only
// retriggers the REWARD pair, so it can double up DashHealAugment/DashExplosionAugment payouts without also
// re-arming this augment's stateful sweep window a second time from a stale position.
//
// OnMovementEnded ALSO runs one immediate catch-up sweep (not just flipping isSweeping off) — required for
// BWBlinkAbilityPD's Blink specifically: its teleport is a direct, synchronous position write (not physics-
// integrated), and it fires MovementStarted (at the OLD position) then teleports then MovementEnded (at the
// NEW position) all within one Activate() call, entirely inside a single Simulate()-phase call — isSweeping
// flips true then false again before LateSimulate() ever runs that tick, so the per-tick sweep below would
// never see it at all otherwise. For a REAL dash this catch-up is a harmless zero-length no-op:
// PlayerControllerPD.EndDash() zeroes velocity and fires MovementEnded BEFORE that tick's movement block ever
// applies dash velocity, so the last tick that actually moved the player was already fully swept by
// LateSimulate() one tick earlier — there's nothing left for the catch-up to find.
//
// "Successful interrupt" is just Interrupt()'s own return value — IEnemyPD.CanBeInterrupted defaults to
// false, and each enemy explicitly opts in only for the specific states that are both committed (not just
// stunnable/CC-vulnerable) and meant to be cancelable (see EnemyBasePD.CanBeInterrupted's own doc comment),
// so a true return already means exactly "this landed on something real."
//
// Split into the two generic SI/FSI parts described above (2026-08-11) — previously this augment tracked
// its own local refundedThisActivation guard and called PlayerControllerPD.RefundDashCharge() directly from
// inside the sweep; that direct call is now CharacterStatsPD.NotifyInterrupted (SI/FSI) from the sweep, plus
// a separate SI subscription (see OnApply/OnSuccessfulInterrupt) that performs the actual stacking grant.
public class DashThroughInterruptAugment : AugmentEffectPD<DashThroughInterruptAugment.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        // True between OnMovementStarted and OnMovementEnded — the active sweep window.
        public bool isSweeping;
        public Vector3 sweepPrevPos;
        // Guards FirstSuccessfulInterrupt (FSI) for this dash's own sweep — reset at OnMovementStarted, no
        // longer refund-specific itself (see class doc comment).
        public bool interruptedThisActivation;
        // Dedupes repeat Interrupt() calls on the same enemy across multiple ticks of one dash — same
        // convention as EnemyBaseState.killLaunchHitIds.
        public DisposableList<PredictedObjectID> touchedEnemies;
        // Permanent — never reset, unlike interruptedThisActivation above. See OnSuccessfulInterrupt's own
        // doc comment for the stacking Dash Haste grant this drives.
        public int stackCount;
        public void Dispose() { touchedEnemies.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.touchedEnemies = touchedEnemies.Duplicate();
            return copy;
        }
    }

    // "Dash Through"/contact-on-dash sweep radius convention: 1.5f (2026-08-19 — bumped from the general
    // Player radius-effect convention's 1.25f specifically for this family, since a dash's own speed makes a
    // too-tight sweep feel unreliable at connecting). Every "Dash Through" augment should use this same 1.5f.
    [SerializeField] private float sweepRadius = 1.5f;

    [Header("On Successful Interrupt — Stacking Permanent Dash Haste")]
    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {dashHastePerStack} directly in the description text — see its own doc comment (only public
    // fields/properties/methods are checked).
    public float dashHastePerStack = 1f;

    private PlayerControllerPD _controller;
    // Matches EnemyBasePD's own _killLaunchSweepResults buffer size — this is the exact same kind of
    // short-lived, per-tick swept-hitbox query.
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];

    // Plain view-layer bookkeeping, not State — tracks what this effect has ACTUALLY applied to
    // CharacterStatsPD so it can be reversed exactly, same role as SurgeInterruptAoEAugment's own
    // _lastAppliedEnergyRegen.
    private float _lastAppliedDashHaste;

    protected override State GetInitialState()
    {
        var s = default(State);
        s.touchedEnemies = DisposableList<PredictedObjectID>.Create(8);
        return s;
    }

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null)
        {
            _controller.AddDashMovementStartedListener(OnMovementStarted);
            _controller.AddDashMovementEndedListener(OnMovementEnded);
        }

        // Second part of this augment — reacts to SuccessfulInterrupt from ANY source (see class doc
        // comment), not just this augment's own sweep below.
        Stats.AddSuccessfulInterruptListener(OnSuccessfulInterrupt);
        // Re-values every already-earned stack the instant StackPower itself changes (2026-09-02), not just
        // lazily on the next new stack — see CharacterStatsPD._onStackPowerChanged's own doc comment.
        Stats.AddStackPowerChangedListener(OnStackPowerChanged);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null)
        {
            _controller.RemoveDashMovementStartedListener(OnMovementStarted);
            _controller.RemoveDashMovementEndedListener(OnMovementEnded);
        }
        if (Stats != null)
        {
            Stats.RemoveSuccessfulInterruptListener(OnSuccessfulInterrupt);
            Stats.RemoveStackPowerChangedListener(OnStackPowerChanged);
        }
    }

    // Current total Dash Haste this passive has actually granted so far (dashHastePerStack * current stack
    // count * StackPower) for AugmentDescriptionFormatter's {LiveStackedDashHaste} token — same shape/
    // reasoning as SurgeInterruptAoEAugment.LiveStackedEnergyRegen's own identical token. Falls back to the
    // prefab's own untouched 0 default for a not-yet-picked level-up option, which is exactly the "0 stacks"
    // state we want to show there anyway.
    public string LiveStackedDashHaste(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(dashHastePerStack * stats.StackPower * currentState.stackCount)}"
            : "0";

    // Fires on EVERY successful interrupt (SI), any source (2026-08-21 — was FSI-gated, see class doc
    // comment). No cap, never resets — permanent stacking for the rest of this run.
    private void OnSuccessfulInterrupt()
    {
        if (Stats == null || Stats.IsDead) return;
        currentState.stackCount++;
        ReapplyDashHaste();
    }

    // Re-derives this augment's OWN total Dash Haste contribution from scratch (remove exactly what was
    // last applied, reapply fresh against the CURRENT stackCount/StackPower) rather than incrementally
    // adding per stack — same "re-derive fully" principle SurgeInterruptAoEAugment's own identically-shaped
    // grant uses. Shared by both triggers that can change the total: earning a new stack
    // (OnSuccessfulInterrupt above) and StackPower itself changing (OnStackPowerChanged below), so the two
    // can never drift out of sync with each other.
    private void ReapplyDashHaste()
    {
        Stats.AddDashHaste(-_lastAppliedDashHaste);
        _lastAppliedDashHaste = dashHastePerStack * currentState.stackCount * Stats.StackPower;
        Stats.AddDashHaste(_lastAppliedDashHaste);
    }

    // Fires whenever Stats.StackPower itself changes (e.g. a StackPowerAugment picked mid-run) — reapplies
    // immediately rather than leaving every already-earned stack sitting at a stale total until the next
    // successful interrupt happens to come in. No-op at 0 stacks (nothing earned yet to re-value).
    private void OnStackPowerChanged()
    {
        if (Stats == null || Stats.IsDead || currentState.stackCount <= 0) return;
        ReapplyDashHaste();
    }

    private void OnMovementStarted()
    {
        currentState.isSweeping = true;
        currentState.sweepPrevPos = Stats.transform.position;
        currentState.interruptedThisActivation = false;
        currentState.touchedEnemies.Clear();
    }

    private void OnMovementEnded()
    {
        currentState.isSweeping = false;
        if (Stats == null || _controller == null) return;

        // Catch-up sweep — see class doc comment for why this is needed (BWBlinkAbilityPD's instant,
        // same-tick teleport) and why it's a harmless no-op for a real multi-tick dash.
        Vector3 currentPos = Stats.transform.position;
        SweepSegment(ref currentState, currentState.sweepPrevPos, currentPos);
        currentState.sweepPrevPos = currentPos;
    }

    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (!state.isSweeping) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || _controller == null) return;

        Vector3 currentPos = Stats.transform.position;
        Vector3 prevPos = state.sweepPrevPos;
        state.sweepPrevPos = currentPos;

        SweepSegment(ref state, prevPos, currentPos);
    }

    // Shared by LateSimulate()'s own per-tick sweep and OnMovementEnded()'s catch-up sweep — see class doc
    // comment for why both call sites exist.
    private void SweepSegment(ref State state, Vector3 fromPos, Vector3 toPos)
    {
        Vector3 segment = toPos - fromPos;
        float dist = segment.magnitude;
        if (dist < 0.0001f) return;
        Vector3 dir = segment / dist;
        float radius = sweepRadius * Stats.bonusSizePercent;

        int n = Physics.SphereCastNonAlloc(fromPos, radius, dir, _sweepResults, dist, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            if (!_sweepResults[i].collider.TryGetComponent(out IEnemyPD enemy)) continue;
            if (enemy._isDead) continue;
            if (state.touchedEnemies.Contains(enemy.ObjectId)) continue;
            state.touchedEnemies.Add(enemy.ObjectId);

            if (enemy.Interrupt())
                Stats.NotifyInterrupted(enemy, ref state.interruptedThisActivation);
        }
    }
}
