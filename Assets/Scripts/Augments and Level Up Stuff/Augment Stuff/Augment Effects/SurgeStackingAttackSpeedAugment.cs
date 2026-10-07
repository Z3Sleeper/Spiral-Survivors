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
// This keeps it exactly reversible with zero drift. ALSO reapplies immediately the instant StackPower itself
// changes (2026-09-02, CharacterStatsPD.AddStackPowerChangedListener/OnStackPowerChanged below) — a
// StackPowerAugment picked mid-run now retroactively boosts every stack already earned right away, not on a
// delay tied to the next stack tick. That one is a PredictedEvent-driven direct mutation (safe — same
// "guaranteed exactly once per real occurrence" reasoning every other PredictedEvent listener's direct Stats
// mutation already relies on in this codebase), independent of the verified-gated UpdateView path above,
// which stays exactly as it was for the stack-COUNT-change case specifically.
//
// Also grants a flat +25 Max Energy, unconditionally in OnApply() (2026-08-31, same shape/reasoning as
// SurgeWeaponCountAugment's own bonusMaxEnergyFlat) — a permanent, always-on grant, not gated behind Surge
// start/end like the stacking Attack Speed above.
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
    // Permanent, unconditional grant (2026-08-31) — same +25 Max Energy shape/reasoning as
    // SurgeWeaponCountAugment's own bonusMaxEnergyFlat: more Surge uptime per full-to-empty cycle, granted
    // once in OnApply() rather than gated behind Surge start/end like the stacking Attack Speed above.
    public float bonusMaxEnergyFlat = 25f;

    // Plain view-layer bookkeeping, not State — tracks what this effect has ACTUALLY applied to
    // CharacterStatsPD so it can be reversed exactly, same role as EnemyBasePD's _wasDeadLastFrame.
    private int   _lastStackCount;
    private float _lastAppliedBonus;

    protected override void OnApply()
    {
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
        // Re-values every already-earned stack the instant StackPower itself changes (2026-09-02) — see
        // class doc comment.
        Stats.AddStackPowerChangedListener(OnStackPowerChanged);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveStackPowerChangedListener(OnStackPowerChanged);
    }

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
        ReapplyAttackSpeedBonus(newCount);
    }

    // Re-derives this augment's OWN total Attack Speed contribution from scratch (remove exactly what was
    // last applied, reapply fresh against the CURRENT StackPower) — shared by both triggers that can change
    // the total: a verified stack-count change (UpdateView above) and StackPower itself changing
    // (OnStackPowerChanged below), so the two can never drift out of sync with each other.
    private void ReapplyAttackSpeedBonus(int stackCount)
    {
        Stats.AddAttackSpeedFlat(-_lastAppliedBonus);
        _lastAppliedBonus = attackSpeedFlat * Stats.StackPower * stackCount;
        Stats.AddAttackSpeedFlat(_lastAppliedBonus);
    }

    // Fires whenever Stats.StackPower itself changes (e.g. a StackPowerAugment picked mid-run) — reapplies
    // immediately using the current predicted stack count, rather than leaving every already-earned stack
    // sitting at a stale total until the next stack tick's verified state happens to reach UpdateView. Safe
    // to mutate Stats directly here (unlike Simulate()) — this is a PredictedEvent, guaranteed exactly once
    // per real occurrence regardless of resimulation, same reasoning every other direct-from-PredictedEvent
    // Stats mutation in this codebase already relies on. No-op at 0 stacks (nothing earned yet to re-value).
    private void OnStackPowerChanged()
    {
        if (Stats == null || Stats.IsDead || currentState.stackCount <= 0) return;
        ReapplyAttackSpeedBonus(currentState.stackCount);
    }
}
