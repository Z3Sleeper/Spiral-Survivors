using PurrNet.Prediction;
using UnityEngine;

// On taking damage (CharacterStatsPD._onDamaged — fires from TakeDamage on any positive damage taken,
// excluding a killing blow), grants a Total-tier PERCENT Move Speed buff (AddMoveSpeedTotalPercent,
// 2026-09-30 — was Bonus-tier via AddMoveSpeedPercent, migrated project-wide the same day that whole
// Bonus-tier percent pair was removed from CharacterStatsPD, see bonusMoveSpeedFlat's own doc comment there)
// for a fixed duration.
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
        if (currentState.timer <= 0f) Stats.AddMoveSpeedTotalPercent(moveSpeedPercent);
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
        Stats.AddMoveSpeedTotalPercent(-moveSpeedPercent);
    }
}
