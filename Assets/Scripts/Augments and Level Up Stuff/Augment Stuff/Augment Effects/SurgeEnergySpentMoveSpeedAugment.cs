using PurrNet.Prediction;
using UnityEngine;

// Reworked (2026-09-17, was SurgeEndMoveSpeedAugment) — the old "on Surge End" trigger meant this was
// functionally free, since Surge itself was already something you barely had to think about; the fix moves
// the trigger to the same energy-SPENT accumulator gate SurgeInterruptAoEAugment already uses (see that
// class's own doc comment #1 for the full mechanic this mirrors), minus the "only on Surge End" part —
// this fires the MOMENT the running total reaches energySpentRequirement, mid-Surge or not, not gated
// behind Surge ending at all. currentState.energySpentSinceLastTrigger accumulates every
// CharacterStatsPD._onEnergySpent event (Surge's own activation cost AND its ongoing per-tick drain both
// route through this one event — "the only energy-spend source in this codebase," see EnergySpentData's own
// doc comment) and resets to 0 the instant it reaches energySpentRequirement (default 50). Seeded to
// float.MaxValue by GetInitialState() below, not 0, so the very first qualifying spend after picking this
// augment always triggers regardless of energy-spent history — same "no awkward dead window right after
// picking it" reasoning SurgeInterruptAoEAugment's own seed already established.
//
// Also dropped the old "a new Surge cuts the bonus short immediately" behavior entirely — per explicit
// design, nothing about Surge starting/ending touches this buff anymore, only the energy-spent gate does.
// Retriggering the gate WHILE the buff is already active doesn't stack or reapply the stat bonus (it's
// already granted) — it just refreshes buffTimer back to buffDuration, extending the window instead of
// double-dipping.
//
// TWO separate buff icons (2026-09-17) — this codebase's first augment with two. One PERMANENT icon
// (energyTrackerIcon/_energyTrackerIconInstance), always visible once picked, tracking gate PROGRESS as
// "x/energySpentRequirement" — same permanent-icon shape SurgeInterruptAoEAugment's own gate-progress icon
// already uses, EXCEPT no "READY" text swap at the threshold: unlike that augment (which waits for a
// separate Surge End to actually consume a satisfied gate, so "READY" can sit on screen for a while), this
// one consumes and resets the gate in the exact same OnEnergySpent call that satisfies it — the counter is
// never actually observably AT the requirement for more than an instant, so a "READY" state would rarely if
// ever be seen and isn't worth the extra branch. The second, SEPARATE icon (buffIcon/_moveSpeedBuffIconInstance)
// is the ORIGINAL transient one, unchanged in behavior — added when the Move Speed buff activates, removed
// when it ends, sweeping fill as buffTimer counts down.
public class SurgeEnergySpentMoveSpeedAugment : AugmentEffectPD<SurgeEnergySpentMoveSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // > 0 while the buff is active, ticking down toward 0 in Simulate(). Reset back to buffDuration on
        // every qualifying trigger, including one that lands while the buff is already active (a refresh).
        public float buffTimer;
        // Running total of energy spent since the last qualifying trigger — see class doc comment for the
        // full gate mechanic. Seeded to float.MaxValue by GetInitialState() below, not 0, so the very first
        // qualifying spend after picking this augment always triggers.
        public float energySpentSinceLastTrigger;
        // State-backed (2026-09-26 fix), not a plain field — EndBonus() is called from Simulate() itself,
        // which genuinely gets resimulated multiple times per tick under ordinary client-side prediction
        // (not a rare edge case). A plain field here got stuck on its FIRST speculative pass's value forever
        // — every later replay of that same tick saw the field already false and silently skipped
        // Stats.AddMoveSpeedTotalPercent(-moveSpeedPercent) via EndBonus()'s own guard, even though
        // CharacterStatsPD's own State correctly rolled the bonus back in before that replay. Net effect
        // without this fix: the Move Speed buff, once granted, never actually gets removed again — a
        // permanent, silent buff instead of a 3-second one.
        public bool bonusActive;
        public void Dispose() { }
    }

    protected override State GetInitialState()
    {
        var s = default(State);
        s.energySpentSinceLastTrigger = float.MaxValue;
        return s;
    }

    // Whole numbers only (2026-09-17, was a plain float) — Surge Cost itself is always a whole number in
    // practice, and this gate is denominated in the same units, so this is now enforced at the type level
    // rather than left as an unenforced convention. SurgeInterruptAoEAugment's own energySpentRequirement got
    // the identical treatment, same day, same reasoning.
    //
    // Public, not [SerializeField] private (2026-09-17 fix) — same "public so AugmentDescriptionFormatter's
    // reflection can resolve it directly as a {token}" reasoning every other description-referenced augment
    // field in this codebase already follows (e.g. SurgeInterruptAoEAugment.maxEnergyPerStack's own doc
    // comment) — only public fields/properties/methods are checked, a private field silently can't be
    // referenced from the description text at all. Still Inspector-serializable exactly the same either way.
    [Header("Activation Gate")]
    public int energySpentRequirement = 50;

    [Header("Move Speed Buff")]
    // Total-tier percent, via AddMoveSpeedTotalPercent (2026-09-30 — was Bonus-tier via AddMoveSpeedPercent;
    // that tier only ever scaled bonusMoveSpeedFlat, which every class passive authored to 0, so this augment
    // was granting a near-permanent no-op. Migrated to Total the same day that whole Bonus-tier percent pair
    // was removed from CharacterStatsPD, see bonusMoveSpeedFlat's own doc comment there).
    public float moveSpeedPercent = 0.30f;
    public float buffDuration  = 3f;

    [Header("Buff Bar")]
    // Permanent — added once in OnApply(), never removed, tracks gate progress. See class doc comment.
    [SerializeField] private Sprite energyTrackerIcon;
    // Transient — added/removed with the Move Speed buff's own active window. See class doc comment.
    [SerializeField] private Sprite buffIcon;

    private BuffIconPD _energyTrackerIconInstance;
    private BuffIconPD _moveSpeedBuffIconInstance;

    protected override void OnApply()
    {
        Stats.AddEnergySpentListener(OnEnergySpent);

        if (isOwner && BuffBarPD.instance != null && _energyTrackerIconInstance == null)
            _energyTrackerIconInstance = BuffBarPD.instance.AddBuffIcon(energyTrackerIcon);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveEnergySpentListener(OnEnergySpent);
        if (BuffBarPD.instance != null)
        {
            if (_energyTrackerIconInstance != null) BuffBarPD.instance.RemoveBuffIcon(_energyTrackerIconInstance);
            if (_moveSpeedBuffIconInstance != null) BuffBarPD.instance.RemoveBuffIcon(_moveSpeedBuffIconInstance);
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        // Same "fill sweeps up toward ready, x/requirement text" shape SurgeInterruptAoEAugment's own
        // gate-progress icon uses — clamped so the seeded-float.MaxValue first-use state reads as fully
        // ready (empty sweep) instead of overflowing into nonsense. No "READY" text swap — see class doc
        // comment for why.
        if (_energyTrackerIconInstance != null)
        {
            float requirement = Mathf.Max(0.0001f, energySpentRequirement);
            float clampedSpent = Mathf.Clamp(viewState.energySpentSinceLastTrigger, 0f, requirement);
            _energyTrackerIconInstance.SetCooldownFill(1f - clampedSpent / requirement);
            _energyTrackerIconInstance.SetStackText($"{Mathf.RoundToInt(clampedSpent)}/{energySpentRequirement}");
        }

        // Inverted from a typical cooldown sweep (which starts full and empties) — this fills UP from 0 as
        // the buff's remaining time counts down, so it reads as "how much of the buff has elapsed" rather
        // than "how much is left."
        if (_moveSpeedBuffIconInstance != null)
            _moveSpeedBuffIconInstance.SetCooldownFill(1f - viewState.buffTimer / buffDuration);
    }

    // Accumulates toward energySpentRequirement — see class doc comment for the full gate mechanic. Triggers
    // (and resets the accumulator) the instant the running total reaches the requirement, with no separate
    // "wait for Surge to end" step. Retriggering while the buff is already active just refreshes buffTimer —
    // see class doc comment.
    private void OnEnergySpent(EnergySpentData data)
    {
        currentState.energySpentSinceLastTrigger += data.amount;
        if (currentState.energySpentSinceLastTrigger < energySpentRequirement) return;

        currentState.energySpentSinceLastTrigger = 0f;
        currentState.buffTimer = buffDuration;

        if (currentState.bonusActive) return;

        currentState.bonusActive = true;
        Stats.AddMoveSpeedTotalPercent(moveSpeedPercent);

        if (isOwner && BuffBarPD.instance != null && _moveSpeedBuffIconInstance == null)
            _moveSpeedBuffIconInstance = BuffBarPD.instance.AddBuffIcon(buffIcon);
    }

    // Only reverts the stat/icon — Simulate()'s natural-expiry path has already clamped buffTimer to 0 by
    // the time this runs. Takes ref State (2026-09-26 fix) — see State.bonusActive's own doc comment.
    private void EndBonus(ref State state)
    {
        if (!state.bonusActive) return;
        state.bonusActive = false;
        Stats.AddMoveSpeedTotalPercent(-moveSpeedPercent);

        if (_moveSpeedBuffIconInstance != null && BuffBarPD.instance != null)
        {
            BuffBarPD.instance.RemoveBuffIcon(_moveSpeedBuffIconInstance);
            _moveSpeedBuffIconInstance = null;
        }
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.buffTimer <= 0f) return;

        state.buffTimer = Mathf.Max(0f, state.buffTimer - delta);
        if (state.buffTimer <= 0f)
            EndBonus(ref state);
    }
}
