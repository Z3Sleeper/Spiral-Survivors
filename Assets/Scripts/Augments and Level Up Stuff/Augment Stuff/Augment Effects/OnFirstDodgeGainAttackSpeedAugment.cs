using PurrNet.Prediction;
using UnityEngine;

// On First Dodge (FD — CharacterStatsPD.AddFirstDodgeListener, fires once per dash-movement's own activation
// window — see CharacterStatsPD.NotifyDodge's own doc comment for the exact 3-condition Dodge contract),
// grants a flat Bonus-tier Attack Speed buff (AddAttackSpeedFlat — flat over percent per explicit user
// preference; despite the "Flat" name it takes an intuitive fraction of baseAttackSpeed, e.g. 0.6 for +60%,
// see that method's own doc comment) for a fixed duration.
//
// Re-triggering while the buff is already active just refreshes the duration back to full — see OnFirstDodge's
// own rising-edge guard — it does NOT re-grant/stack a second copy of the bonus, same "start/end pairing
// needs a granted guard" convention every timed on-trigger buff in this codebase follows. State.timer doubles
// as both the countdown AND the "is it currently active" flag (> 0 means active), no separate bool needed.
public class OnFirstDodgeGainAttackSpeedAugment : AugmentEffectPD<OnFirstDodgeGainAttackSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float timer;
        public void Dispose() { }
    }

    public float attackSpeedPercent = 0.6f;
    public float duration = 5f;

    protected override void OnApply()
    {
        Stats.AddFirstDodgeListener(OnFirstDodge);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveFirstDodgeListener(OnFirstDodge);
    }

    private void OnFirstDodge()
    {
        if (Stats == null || Stats.IsDead) return;
        // Only grants on the rising edge (see class doc comment) — a Dodge landing while the buff is already
        // up just refreshes the timer below, it doesn't stack a second copy of the bonus.
        if (currentState.timer <= 0f) Stats.AddAttackSpeedFlat(attackSpeedPercent);
        currentState.timer = duration;
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;
        if (state.timer <= 0f) return;

        state.timer -= delta;
        if (state.timer > 0f) return;

        state.timer = 0f;
        Stats.AddAttackSpeedFlat(-attackSpeedPercent);
    }
}
