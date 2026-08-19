using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. 0.25s after every real DashStarted, and separately 0.25s after every real DashEnded,
// re-invokes that same event again via PlayerControllerPD's own NotifyDashStarted()/NotifyDashEnded() — the
// exact same public entry point RogueSpinAbilityPD's leap already uses to hook into this shared event pair
// (see DashThroughInterruptAugment's own doc comment). Doesn't touch StartDash()/EndDash() at all — no second
// charge spent, no second invincibility grant, no actual movement — purely re-fires the EVENT, so every other
// "Dash...Augment" reacting to it (DashHealAugment, DashExplosionAugment, PhantomBladeOnDashAugment,
// DashThroughInterruptAugment) triggers a second time too, from the player's real position/state 0.25s later.
//
// Flagged interaction with DashThroughInterruptAugment (per design brief) turned out to have a clean fix
// rather than just a caveat: that augment used to treat DashStarted/DashEnded as its own stateful sweep
// window, which a naive retrigger here would have re-armed a second time from a stale position. Fixed at the
// source instead — PlayerControllerPD now has a separate DashMovementStarted/DashMovementEnded pair
// (2026-08-13) specifically for stateful movement-window listeners, and DashThroughInterruptAugment was moved
// onto that pair. This augment only ever retriggers the reward pair (Started/Ended), so it has zero effect on
// DashThroughInterruptAugment's sweep window — see that augment's own class doc comment for the full story.
public class DashEffectDoubleTriggerAugment : AugmentEffectPD<DashEffectDoubleTriggerAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public bool  awaitingRetriggerStart;
        public float retriggerStartTimer;
        public bool  awaitingRetriggerEnd;
        public float retriggerEndTimer;
        public void Dispose() { }
    }

    [SerializeField] private float retriggerDelay = 0.25f;

    private PlayerControllerPD _controller;

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null)
        {
            _controller.AddDashStartedListener(OnDashStarted);
            _controller.AddDashEndedListener(OnDashEnded);
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null)
        {
            _controller.RemoveDashStartedListener(OnDashStarted);
            _controller.RemoveDashEndedListener(OnDashEnded);
        }
    }

    private void OnDashStarted()
    {
        currentState.awaitingRetriggerStart = true;
        currentState.retriggerStartTimer = retriggerDelay;
    }

    private void OnDashEnded()
    {
        currentState.awaitingRetriggerEnd = true;
        currentState.retriggerEndTimer = retriggerDelay;
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (_controller == null) return;

        if (state.awaitingRetriggerStart)
        {
            state.retriggerStartTimer -= delta;
            if (state.retriggerStartTimer <= 0f)
            {
                state.awaitingRetriggerStart = false;
                _controller.NotifyDashStarted();
            }
        }

        if (state.awaitingRetriggerEnd)
        {
            state.retriggerEndTimer -= delta;
            if (state.retriggerEndTimer <= 0f)
            {
                state.awaitingRetriggerEnd = false;
                _controller.NotifyDashEnded();
            }
        }
    }
}
