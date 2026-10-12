using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// 0.25s after every genuine dash-like Start, and separately 0.25s after every genuine dash-like End,
// re-invokes that same event again via PlayerControllerPD's own NotifyDashStarted()/NotifyDashEnded() — the
// exact same public entry point RogueSpinAbilityPD's leap and BWBlinkAbilityPD already use to hook into this
// shared event pair. Doesn't touch StartDash()/EndDash() at all — no second charge spent, no second
// invincibility grant, no actual movement — purely re-fires the EVENT, so every other "Dash...Augment"
// reacting to it (DashHealAugment, DashExplosionAugment, PhantomBladeOnDashAugment) triggers a second time
// too, from the player's real position/state 0.25s later. "Genuine dash-like Start/End" means a real dash
// (StartDash/EndDash) OR RogueSpin's leap OR BWBlink's teleport OR RogueVanish's blink — anything that fires
// PlayerControllerPD._onDashMovementStarted/_onDashMovementEnded, but see the interrupted check below for
// why Ended specifically also has to pass that.
//
// Listens to _onDashMovementStarted/_onDashMovementEnded (2026-10-10, was a separate _onRealDashStarted/
// _onRealDashEnded pair this augment was the ONLY listener of anywhere in the codebase — folded away once
// _onDashMovementEnded grew an `interrupted` payload, making the separate pair redundant), NOT the
// _onDashStarted/_onDashEnded pair its own retrigger fires into — two real bugs this still has to avoid,
// same as the old RealDash pair was built to fix:
// 1. This augment used to listen to the exact same event its own retrigger call re-invokes, so the
//    retrigger firing the event called straight back into its own listener, re-arming a fresh 0.25s timer
//    forever — an infinite self-sustaining loop. The Movement pair is never touched by this augment's own
//    retrigger (which only ever fires the plain Started/Ended pair), so that's structurally impossible here
//    too, no reentrancy guard needed — same property the old RealDash pair had.
// 2. Even with a reentrancy guard (the first fix attempted, long before the RealDash rework), a SECOND real
//    trigger of the same type landing before the first one's own 0.25s retrigger fired would silently
//    stomp/drop the first one's pending timer if tracked as a single bool+float. Fixed by tracking a LIST of
//    independent pending retrigger timers instead of one shared slot (see State below) — unrelated to which
//    event pair is listened to, still needed regardless.
//
// On the End side specifically, interrupted is checked and skipped (OnDashMovementEnded below) — unlike
// Start, Ended fires on EVERY movement-window close including an interrupted one, which the old RealDashEnded
// never did (it only ever fired on a natural completion). Without this check, an interrupted dash — which
// already correctly pays its direct reward zero times (DashExplosionAugment/DashHealAugment skip it too,
// same "being interrupted is a punishment" rule) — would still get rewarded once via this augment's own
// delayed echo. Checking it here is what lets this augment use the Movement pair safely instead of needing
// its own dedicated event.
public class DashEffectDoubleTriggerAugment : AugmentEffectPD<DashEffectDoubleTriggerAugment.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        // Each entry counts down independently from retriggerDelay to 0; reaching 0 fires that one
        // retrigger and removes the entry. Never more than a handful of entries at once in practice (would
        // need several genuine dash-like triggers within the same 0.25s window), so a plain list scan every
        // tick is cheap.
        public DisposableList<float> pendingStartTimers;
        public DisposableList<float> pendingEndTimers;

        public void Dispose()
        {
            pendingStartTimers.Dispose();
            pendingEndTimers.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.pendingStartTimers = pendingStartTimers.Duplicate();
            copy.pendingEndTimers = pendingEndTimers.Duplicate();
            return copy;
        }
    }

    [SerializeField] private float retriggerDelay = 0.25f;

    private PlayerControllerPD _controller;

    // A default DisposableList<T> has no valid backing list — GetInitialState() only ever runs on true
    // spawn, same convention DashThroughInterruptAugment's own touchedEnemies list already uses.
    protected override State GetInitialState()
    {
        var s = default(State);
        s.pendingStartTimers = DisposableList<float>.Create(2);
        s.pendingEndTimers = DisposableList<float>.Create(2);
        return s;
    }

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null)
        {
            _controller.AddDashMovementStartedListener(OnDashMovementStarted);
            _controller.AddDashMovementEndedListener(OnDashMovementEnded);
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null)
        {
            _controller.RemoveDashMovementStartedListener(OnDashMovementStarted);
            _controller.RemoveDashMovementEndedListener(OnDashMovementEnded);
        }
    }

    private void OnDashMovementStarted() => currentState.pendingStartTimers.Add(retriggerDelay);

    // interrupted: skipped outright — see class doc comment for why this check is what lets this augment
    // safely use the Movement pair (which fires on every close, interrupted or not) in place of the old,
    // narrower RealDashEnded (which only ever fired on a natural completion).
    private void OnDashMovementEnded(bool interrupted)
    {
        if (interrupted) return;
        currentState.pendingEndTimers.Add(retriggerDelay);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (_controller == null) return;

        // Iterated backwards so RemoveAt on a firing entry doesn't skip the next one — same convention
        // every other per-entry countdown list in this codebase uses (e.g. EnemyBaseState.contactCooldowns).
        for (int i = state.pendingStartTimers.Count - 1; i >= 0; i--)
        {
            float t = state.pendingStartTimers[i] - delta;
            if (t <= 0f)
            {
                state.pendingStartTimers.RemoveAt(i);
                _controller.NotifyDashStarted();
            }
            else
            {
                state.pendingStartTimers[i] = t;
            }
        }

        for (int i = state.pendingEndTimers.Count - 1; i >= 0; i--)
        {
            float t = state.pendingEndTimers[i] - delta;
            if (t <= 0f)
            {
                state.pendingEndTimers.RemoveAt(i);
                _controller.NotifyDashEnded();
            }
            else
            {
                state.pendingEndTimers[i] = t;
            }
        }
    }
}
