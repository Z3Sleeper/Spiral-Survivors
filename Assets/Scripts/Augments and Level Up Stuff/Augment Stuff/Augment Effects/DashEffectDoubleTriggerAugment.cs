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
// (StartDash/EndDash) OR RogueSpin's leap OR BWBlink's teleport — anything that fires
// PlayerControllerPD._onRealDashStarted/_onRealDashEnded.
//
// Listens to _onRealDashStarted/_onRealDashEnded (2026-09-03 rework), NOT the _onDashStarted/_onDashEnded
// pair its own retrigger fires into — two real bugs this fixes at the root instead of guarding around them:
// 1. This augment used to listen to the exact same event its own retrigger call re-invokes, so the
//    retrigger firing the event called straight back into its own listener, re-arming a fresh 0.25s timer
//    forever — an infinite self-sustaining loop. Listening to a genuinely separate event this augment never
//    fires into makes that structurally impossible, no reentrancy guard needed.
// 2. Even with a reentrancy guard (the first fix attempted), a SECOND real trigger of the same type
//    (Started-after-Started, or Ended-after-Ended) landing before the first one's own 0.25s retrigger fired
//    would silently stomp/drop the first one's pending timer if tracked as a single bool+float — a real,
//    easily-hit case: an ability activated mid-dash cleanly ends the dash (EndDash, a natural end — fires
//    _onRealDashEnded) and then that same ability's own Activate() (e.g. RogueSpin) fires
//    _onRealDashStarted a moment later, well within the 0.25s window, landing two genuine "Started" events
//    close together. Fixed by tracking a LIST of independent pending retrigger timers instead of one shared
//    slot (see State below) — every genuine trigger gets its own retrigger fired at its own time, no matter
//    how many land close together.
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
            _controller.AddRealDashStartedListener(OnRealDashStarted);
            _controller.AddRealDashEndedListener(OnRealDashEnded);
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null)
        {
            _controller.RemoveRealDashStartedListener(OnRealDashStarted);
            _controller.RemoveRealDashEndedListener(OnRealDashEnded);
        }
    }

    private void OnRealDashStarted() => currentState.pendingStartTimers.Add(retriggerDelay);
    private void OnRealDashEnded() => currentState.pendingEndTimers.Add(retriggerDelay);

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
