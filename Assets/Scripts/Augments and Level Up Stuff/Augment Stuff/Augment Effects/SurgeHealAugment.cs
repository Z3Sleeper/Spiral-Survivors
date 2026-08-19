using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. While the player is Surging, heals them (healBase + healthRegenPercent * HealthRegen)
// per second, in tickInterval-spaced ticks (0.25s). Ticks are anchored to Surge START, not a free-running
// background timer: the very first tick fires IMMEDIATELY the instant Surge starts (no waiting the first
// 0.25s), and if Surge ends mid-interval, one final "catch-up" tick fires scaled by however much of that
// partial interval had actually elapsed since the last tick (not a full tickInterval's worth) — so a surge
// is never shortchanged OR over-credited for a partial tick window, regardless of how long any given surge
// happens to last.
//
// Routes every tick through the shared Heal() choke point (see its own doc comment), so this correctly
// picks up HealPower/CritHealPower/the always-on crit roll for free — and deliberately does NOT roll luck
// variance, since heals never do (2026-08-12 reversal, see feedback_all_damage_rolls_luck memory). Callers
// pass the RAW per-tick amount; Heal() itself applies the multiplier.
//
// Per AugmentEffectPD's own doc comment, a discrete one-shot Stats.Heal() call is NOT safe directly from
// Simulate() or from a PredictedEvent handler that only mutates cross-identity state (risks double-firing
// during the client's own resimulation/reconciliation pass — same reasoning SurgeStackingAttackSpeedAugment's
// own doc comment gives for why IT defers Stats.AddAttackSpeedFlat to UpdateView() instead of calling it from
// Simulate()). So this augment only ever accumulates a monotonically-growing desiredTotalHealed total in its
// OWN predicted State (safe — Surge Start/End event handlers and Simulate() itself may freely mutate this
// augment's own currentState, exactly like SurgeInterruptAoEAugment's own currentState.stackCount
// already does) — the actual Stats.Heal() call only ever fires from UpdateView(), off verified state, for
// just the DELTA since last applied, same "recompute fully, apply only the delta" shape
// HealthToHealthRegenAugment already uses.
public class SurgeHealAugment : AugmentEffectPD<SurgeHealAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Counts down from tickInterval to the next regular heal tick — only ticks while Surging. Reset to
        // a fresh tickInterval every time a tick actually fires, including the immediate Surge Start tick.
        public float tickTimer;
        // Monotonically-growing total amount this augment has EARNED so far (never decreases) — grows by a
        // full tick's worth on Surge Start (the immediate first tick) and every regular tickInterval tick,
        // and by a partial tick's worth on Surge End (the catch-up tick, scaled by tickInterval - tickTimer,
        // i.e. however long since the last tick actually was). See class doc comment for why the real
        // Stats.Heal() application is deferred to UpdateView() instead of happening right where this grows.
        public float desiredTotalHealed;
        public void Dispose() { }
    }

    [SerializeField] private float healBase = 0.4f;
    // 2f = 200% (this codebase's percent fields store the fraction).
    public float healthRegenPercent = 2f;
    [SerializeField] private float tickInterval = 0.25f;

    // Baseline heal-per-second at 100% HealPower (the crit/HealPower multiply itself isn't included since
    // it varies per-tick, not a stat to preview) for AugmentDescriptionFormatter's {LiveHealPerSecondRatio}
    // token.
    public string LiveHealPerSecondRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(healBase + stats.HealthRegen * healthRegenPercent)} "
            : "";

    // Tracks how much has actually been granted via Stats.Heal so far — mutated only from UpdateView() off
    // verified state, so the delta applied each time is always correct regardless of how many times a given
    // tick gets resimulated. Same role as HealthToHealthRegenAugment's own _appliedAmount.
    private float _appliedAmount;

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
        Stats.AddSurgeEndedListener(OnSurgeEnded);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats == null) return;
        Stats.RemoveSurgeStartedListener(OnSurgeStarted);
        Stats.RemoveSurgeEndedListener(OnSurgeEnded);
    }

    private void OnSurgeStarted()
    {
        currentState.desiredTotalHealed += TickAmount(tickInterval);
        currentState.tickTimer = tickInterval;
    }

    private void OnSurgeEnded()
    {
        // Whatever fraction of the current interval had actually elapsed since the last tick — see class
        // doc comment.
        float elapsedSinceLastTick = tickInterval - currentState.tickTimer;
        if (elapsedSinceLastTick > 0f)
            currentState.desiredTotalHealed += TickAmount(elapsedSinceLastTick);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;
        if (!Stats.IsSurging) return;

        state.tickTimer -= delta;
        if (state.tickTimer > 0f) return;

        state.tickTimer += tickInterval;
        state.desiredTotalHealed += TickAmount(tickInterval);
    }

    private float TickAmount(float duration) => (healBase + Stats.HealthRegen * healthRegenPercent) * duration;

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desired = verified.Value.desiredTotalHealed;
        if (Mathf.Approximately(desired, _appliedAmount)) return;

        Stats.Heal(desired - _appliedAmount);
        _appliedAmount = desired;
    }
}
