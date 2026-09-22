using PurrNet.Prediction;
using UnityEngine;

// On taking damage (CharacterStatsPD._onDamaged — fires from TakeDamage on any positive damage taken,
// excluding a killing blow), grants a Bonus-tier PERCENT Move Speed buff (AddMoveSpeedPercent, 2026-09-19 —
// was AddMoveSpeedFlat, converted project-wide alongside every other flat-Move-Speed augment now that Move
// Speed no longer comes off Stat augments, per explicit design) for a fixed duration.
//
// Re-triggering while the buff is already active (e.g. two hits in quick succession) just refreshes the
// duration back to full — it does NOT re-grant/stack a second copy of the bonus, same "start/end pairing
// needs a granted guard" convention every timed on-trigger buff in this codebase follows (see
// OnDodgeGainAttackSpeedAugment's own doc comment for the identical shape). State.timer doubles as
// both the countdown AND the "is it currently active" flag (> 0 means active), no separate bool needed.
public class OnDamageTakenGainMoveSpeedAugment : AugmentEffectPD<OnDamageTakenGainMoveSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float timer;
        public void Dispose() { }
    }

    public float moveSpeedPercent = 0.30f;
    public float duration = 3f;

    protected override void OnApply()
    {
        Stats.AddDamagedListener(OnDamaged);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDamagedListener(OnDamaged);
    }

    private void OnDamaged(DamageTakenData data)
    {
        if (Stats == null || Stats.IsDead) return;
        // Only grants on the rising edge (see class doc comment) — a hit landing while the buff is already
        // up just refreshes the timer below, it doesn't stack a second copy of the bonus.
        if (currentState.timer <= 0f) Stats.AddMoveSpeedPercent(moveSpeedPercent);
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
        Stats.AddMoveSpeedPercent(-moveSpeedPercent);
    }
}
