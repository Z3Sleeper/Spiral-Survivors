using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — our first StackPower-consuming augment. While Surging, gain a stack every 1s (up
// to maxStacks); each stack grants +15% Attack Speed (AddAttackSpeedFlat's additive-into-baseAttackSpeed
// convention, "not total" per the user's own wording — stacks sum linearly, they don't compound
// multiplicatively), scaled by Stats.StackPower. Stacks reset to 0 the instant Surge ends, so every Surge
// starts the climb over from scratch rather than carrying stacks between Surges.
//
// The stack COUNT lives in predicted State and ticks in Simulate() (safe — same shape as
// CharacterStatsPD's own regen timers), but per AugmentEffectPD's own doc comment a discrete stat += is NOT
// safe to call directly from Simulate() (risks double-firing during the client's own reconciliation pass).
// So the actual Stats.AddAttackSpeedFlat mutation happens in UpdateView(), gated to verified state and
// keyed off a change in stack count — the same "react to a verified-state transition" shape used elsewhere
// (e.g. EnemyBasePD's own isDead-transition detection), just for an int delta instead of a bool edge.
//
// Recomputes its OWN total contribution from scratch on every stack-count change (removing exactly what it
// last applied, then reapplying fresh against the CURRENT StackPower) rather than incrementally adding per
// stack — same "re-derive fully, don't incrementally adjust" principle as CharacterStatsPD.AddSizePercent.
// This keeps it exactly reversible with zero drift, and self-corrects if StackPower itself changes (e.g. a
// later StackPowerAugment pick) by the next time this augment's own stack count changes.
public class SurgeStackingAttackSpeedAugment : AugmentEffectPD<SurgeStackingAttackSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float stackTimer;
        public int   stackCount;
        public void Dispose() { }
    }

    [SerializeField] private float stackInterval    = 1f;
    [SerializeField] private int   maxStacks         = 10;
    [SerializeField] private float attackSpeedFlat     = 0.25f;

    // Plain view-layer bookkeeping, not State — tracks what this effect has ACTUALLY applied to
    // CharacterStatsPD so it can be reversed exactly, same role as EnemyBasePD's _wasDeadLastFrame.
    private int   _lastStackCount;
    private float _lastAppliedBonus;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;

        if (!Stats.IsSurging)
        {
            state.stackCount = 0;
            // Reset to a full interval, not 0 — leaving it at 0 would let the very first Simulate() tick
            // after Surge starts immediately cross the <= 0 threshold below and grant a stack instantly,
            // instead of waiting the full stackInterval like every subsequent stack does.
            state.stackTimer = stackInterval;
            return;
        }

        state.stackTimer -= delta;
        if (state.stackTimer > 0f) return;

        state.stackTimer = stackInterval;
        if (state.stackCount < maxStacks)
            state.stackCount++;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!verified.HasValue || Stats == null) return;

        int newCount = verified.Value.stackCount;
        if (newCount == _lastStackCount) return;
        _lastStackCount = newCount;

        Stats.AddAttackSpeedFlat(-_lastAppliedBonus);
        _lastAppliedBonus = attackSpeedFlat * Stats.StackPower * newCount;
        Stats.AddAttackSpeedFlat(_lastAppliedBonus);
    }
}
