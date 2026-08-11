using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Dashing through an enemy interrupts it (cancels whatever attack sequence it's mid-way through, same as any
// other IEnemyPD.Interrupt() caller). No damage, no knockback — purely a disruption tool. If at least one of
// the enemies interrupted during that dash was genuinely mid dash/windup/attack (not just idly walking) when
// the interrupt landed, refunds ONE dash charge — capped at exactly one refund per dash activation regardless
// of how many enemies get interrupted during it.
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
// Driven by OnDashStarted/OnDashEnded (not IsDashing directly) — RogueSpinAbilityPD's own leap explicitly
// calls NotifyDashStarted()/NotifyDashEnded() to hook into this same shared event pair every "Dash...Augment"
// already reacts to (DashExplosionAugment, DashHealAugment), even though it never sets isDashing itself
// (a separate isSpecialMoving flag). Gating on the event pair covers both a real dash and Rogue's leap
// automatically, same as the rest of the family.
//
// ALSO listens to PlayerControllerPD's own DashInterrupted/SpecialMovementInterrupted events (2026-08-11),
// pointed at the exact same OnDashEnded handler — an Interrupt()'d dash/leap (e.g. by an enemy) fires those
// INSTEAD of the normal Ended event (see PlayerControllerPD.Interrupt()'s own doc comment: an interrupted
// action shouldn't still fire "completed" events other Dash augments treat as a reward trigger). This
// augment doesn't care WHY the window closed, only that it did — without this, an interrupt would leave
// isSweeping stuck true forever, since nothing else would ever clear it.
//
// OnDashEnded ALSO runs one immediate catch-up sweep (not just flipping isSweeping off) — required for
// BWBlinkAbilityPD's Blink specifically: its teleport is a direct, synchronous position write (not physics-
// integrated), and it calls NotifyDashStarted() (at the OLD position) then teleports then NotifyDashEnded()
// (at the NEW position) all within one Activate() call, entirely inside a single Simulate()-phase call —
// isSweeping flips true then false again before LateSimulate() ever runs that tick, so the per-tick sweep
// below would never see it at all otherwise. For a REAL dash this catch-up is a harmless zero-length no-op:
// PlayerControllerPD.EndDash() zeroes velocity and fires OnDashEnded BEFORE that tick's movement block ever
// applies dash velocity, so the last tick that actually moved the player was already fully swept by
// LateSimulate() one tick earlier — there's nothing left for the catch-up to find.
//
// "Successful interrupt" (the refund gate) reworked (2026-08-10) to just be Interrupt()'s own return value
// alone — IEnemyPD.CanBeInterrupted now defaults to false, and each enemy explicitly opts in only for the
// specific states that are both committed (not just stunnable/CC-vulnerable) and meant to be cancelable (see
// EnemyBasePD.CanBeInterrupted's own doc comment), so a true return already means exactly "this landed on
// something real." The old design combined this with a separate IsStunnable check to work around
// CanBeInterrupted previously defaulting to true almost everywhere — no longer needed.
public class DashThroughInterruptAugment : AugmentEffectPD<DashThroughInterruptAugment.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        // True between OnDashStarted and OnDashEnded — the active sweep window.
        public bool isSweeping;
        public Vector3 sweepPrevPos;
        public bool refundedThisActivation;
        // Dedupes repeat Interrupt() calls on the same enemy across multiple ticks of one dash — same
        // convention as EnemyBaseState.killLaunchHitIds.
        public DisposableList<PredictedObjectID> touchedEnemies;
        public void Dispose() { touchedEnemies.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.touchedEnemies = touchedEnemies.Duplicate();
            return copy;
        }
    }

    // Player radius-effect convention: 1f player sphere + 0.25 generous extra.
    [SerializeField] private float sweepRadius = 1.25f;

    private PlayerControllerPD _controller;
    // Matches EnemyBasePD's own _killLaunchSweepResults buffer size — this is the exact same kind of
    // short-lived, per-tick swept-hitbox query.
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];

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
            _controller.AddDashStartedListener(OnDashStarted);
            _controller.AddDashEndedListener(OnDashEnded);
            // A dash/leap that gets Interrupt()'d (e.g. by an enemy) never fires OnDashEnded — that event is
            // reserved for "the action completed," which an interrupted one didn't (see
            // PlayerControllerPD.Interrupt()'s own doc comment). Without also closing the sweep window here,
            // isSweeping would stay stuck true forever after an interrupt, since only OnDashEnded (or the
            // next OnDashStarted) ever clears it. OnDashEnded itself already does exactly the right thing
            // regardless of WHY the window closed (final catch-up sweep + stop sweeping — nothing in it
            // assumes a successful completion), so it's reused directly as the handler for both.
            _controller.AddDashInterruptedListener(OnDashEnded);
            _controller.AddSpecialMovementInterruptedListener(OnDashEnded);
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null)
        {
            _controller.RemoveDashStartedListener(OnDashStarted);
            _controller.RemoveDashEndedListener(OnDashEnded);
            _controller.RemoveDashInterruptedListener(OnDashEnded);
            _controller.RemoveSpecialMovementInterruptedListener(OnDashEnded);
        }
    }

    private void OnDashStarted()
    {
        currentState.isSweeping = true;
        currentState.sweepPrevPos = Stats.transform.position;
        currentState.refundedThisActivation = false;
        currentState.touchedEnemies.Clear();
    }

    private void OnDashEnded()
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

    // Shared by LateSimulate()'s own per-tick sweep and OnDashEnded()'s catch-up sweep — see class doc
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

            bool actuallyInterrupted = enemy.Interrupt();

            if (actuallyInterrupted && !state.refundedThisActivation)
            {
                state.refundedThisActivation = true;
                _controller.RefundDashCharge();
            }
        }
    }
}
