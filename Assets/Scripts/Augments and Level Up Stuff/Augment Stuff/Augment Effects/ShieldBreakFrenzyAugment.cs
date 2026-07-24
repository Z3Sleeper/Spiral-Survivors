using PurrNet.Prediction;
using UnityEngine;

// New tech — when your shield breaks (CharacterStatsPD._onShieldBreak, fired the tick shield crosses from
// >0 to <=0 inside TakeDamage), gain +20% Total Attack Speed and +20% Move Speed for 3 seconds. Re-breaking
// your shield while the buff is still active simply refreshes the full duration, rather than stacking.
//
// Grant/refresh happens directly inside the PredictedEvent listener (OnShieldBreak) — same pattern
// SurgeDualWeildAugment's own start/end listeners use, safe because PredictedEvent guarantees a listener
// fires exactly once per real logical occurrence regardless of resimulation. The REVERT is different: it's
// not gated by another PredictedEvent, just this effect's own State.buffTimer counting down to 0 inside
// Simulate() — a raw state transition detected from Simulate() is NOT safe to act on directly (a one-time
// stat -= there risks double-firing during the client's in-place reconciliation replay, same reasoning
// AugmentEffectPD's own doc comment gives for one-shot grants). So the revert instead follows that same
// established shape: State.buffTimer ticks deterministically in Simulate(), and the actual
// Add*Percent(-amount) call only fires off a confirmed transition in verified state (UpdateView), guarded
// by _grantedView so it only ever fires once per real activation and once per real expiry.
public class ShieldBreakFrenzyAugment : AugmentEffectPD<ShieldBreakFrenzyAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // > 0 while the buff is active, ticking down toward 0 in Simulate(). "Active" is just buffTimer > 0
        // — no separate bool needed.
        public float buffTimer;
        public void Dispose() { }
    }

    [SerializeField] private float attackSpeedTotalPercent = 0.2f;
    [SerializeField] private float totalMoveSpeedPercent   = 0.2f;
    [SerializeField] private float buffDuration     = 3f;

    // Tracks whether the CONFIRMED (verified) buff window is currently granted — mutated only from
    // UpdateView() off verified state, so it only ever toggles once per real activation/expiry.
    private bool _grantedView;

    protected override void OnApply()
    {
        Stats.AddShieldBreakListener(OnShieldBreak);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveShieldBreakListener(OnShieldBreak);
    }

    private void OnShieldBreak()
    {
        currentState.buffTimer = buffDuration;
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.buffTimer > 0f)
            state.buffTimer = Mathf.Max(0f, state.buffTimer - delta);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        bool active = verified.Value.buffTimer > 0f;
        if (active == _grantedView) return;
        _grantedView = active;

        float sign = active ? 1f : -1f;
        Stats.AddAttackSpeedTotalPercent(sign * attackSpeedTotalPercent);
        Stats.AddMoveSpeedTotalPercent(sign * totalMoveSpeedPercent);
    }
}
