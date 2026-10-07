using PurrNet.Prediction;
using UnityEngine;

// Persistent, LIVE-tracked relationship — not a one-way accumulator like OnEnemyDeathHealthStackingAugment/
// SurgeStackingAttackSpeedAugment (whose stacks only ever grow over a run). Grants perStackMoveSpeedPercent
// (8%, Total-tier via AddMoveSpeedTotalPercent, 2026-09-30 — was Bonus-tier via AddMoveSpeedPercent, migrated
// project-wide the same day that whole Bonus-tier percent pair was removed from CharacterStatsPD, see
// bonusMoveSpeedFlat's own doc comment there) Move Speed for every perStackMissingHealthPercent(15%) of CURRENT
// missing health (1 - CurrentHealth/MaxHealth), in discrete whole-stack THRESHOLD steps, not a smooth
// gradient — e.g. exactly 20% missing health grants only 1 stack (20% cleared the first 15% threshold, not
// yet the second 30% one), not a fractional 1.33 stacks. Both perStackMoveSpeedPercent and
// perStackMissingHealthPercent are deliberately named with "Percent" as the actual trailing suffix, not
// buried mid-name — AugmentDescriptionFormatter's *100 auto-scale only fires on an EndsWith("Percent") check
// (same "PerUnit vs Percent" trap OnHitDamageDistanceAugment's own perUnitDistancePercent rename fixed
// 2026-09-13 for the identical reason). Healing back up loses stacks live, the instant CurrentHealth crosses
// back below a threshold — same "recomputed every tick, not earned-and-kept" shape HealthToAttackSpeedAugment
// already uses for its own live Health-derived bonus (see that class's own doc comment for the full
// architecture reasoning: Simulate() recomputes state.desiredAmount deterministically every tick off
// already-predicted Stats reads, but the actual Stats.AddMoveSpeedTotalPercent delta only ever fires off a
// CONFIRMED transition in verified state via UpdateView, so a mid-resimulation pass can never double-apply).
//
// Stack count itself is capped at maxStacks(5) — 5 stacks * 15%/stack = 75% missing health (25% Health
// remaining) reaches the cap; going lower than 25% Health grants no further stacks. StackPower, however,
// multiplies the OUTPUT (perStackMoveSpeedPercent * stackCount * Stats.StackPower), uncapped — the 5-stack
// ceiling bounds how many discrete thresholds can ever be "earned," not the final Move Speed number a
// high-StackPower build can reach from those same 5 stacks, same "cap the stacks, not the scaled total"
// shape every other StackPower-scaled stacking augment in this family already uses.
public class MissingHealthMoveSpeedStackingAugment : AugmentEffectPD<MissingHealthMoveSpeedStackingAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Live-recomputed every tick in Simulate() — see class doc comment.
        public float desiredAmount;
        public void Dispose() { }
    }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {perStackMoveSpeedPercent}/{perStackMissingHealthPercent}/{maxStacks} tokens directly in the
    // description text.
    public float perStackMoveSpeedPercent = 0.08f;
    public float perStackMissingHealthPercent = 0.15f;
    public int   maxStacks = 5;

    // Current live stack count (0-maxStacks) and total Move Speed PERCENT this augment is granting RIGHT NOW
    // (perStackMoveSpeedPercent * stackCount * StackPower) for AugmentDescriptionFormatter's {LiveStackCount}/
    // {LiveMoveSpeedRatio} tokens — reads the passed-in `stats` directly (not currentState), same convention
    // every other Live*Ratio method in this codebase uses so this also resolves correctly for a not-yet-picked
    // level-up preview against a live character. Falls back to "0" outside a live match/with no character
    // context, matching every other Live*Ratio's own convention.
    private int LiveStackCountOf(CharacterStatsPD stats)
    {
        if (stats == null || stats.MaxHealth <= 0f) return 0;
        float missingHealthPercent = Mathf.Clamp01(1f - stats.CurrentHealth / stats.MaxHealth);
        return Mathf.Min(maxStacks, Mathf.FloorToInt(missingHealthPercent / perStackMissingHealthPercent));
    }

    public string LiveStackCount(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null ? $"{LiveStackCountOf(stats)}" : "0";

    public string LiveMoveSpeedRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(perStackMoveSpeedPercent * LiveStackCountOf(stats) * stats.StackPower * 100f)}"
            : "0";

    // Tracks how much has actually been granted via Stats.AddMoveSpeedTotalPercent so far — mutated only from
    // UpdateView() off verified state, so the delta applied each time is always correct regardless of how
    // many times a given tick gets resimulated. Can go negative-delta (stacks lost) just as easily as
    // positive (stacks gained) — see class doc comment.
    private float _appliedAmount;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null) return;

        float missingHealthPercent = Stats.MaxHealth > 0f ? Mathf.Clamp01(1f - Stats.CurrentHealth / Stats.MaxHealth) : 0f;
        int stackCount = Mathf.Min(maxStacks, Mathf.FloorToInt(missingHealthPercent / perStackMissingHealthPercent));
        state.desiredAmount = perStackMoveSpeedPercent * stackCount * Stats.StackPower;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desired = verified.Value.desiredAmount;
        if (Mathf.Approximately(desired, _appliedAmount)) return;

        float delta = desired - _appliedAmount;
        Stats.AddMoveSpeedTotalPercent(delta);
        _appliedAmount = desired;
    }
}
