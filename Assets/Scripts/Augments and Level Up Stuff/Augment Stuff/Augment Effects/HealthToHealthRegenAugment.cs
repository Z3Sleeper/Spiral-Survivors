using PurrNet.Prediction;
using UnityEngine;

// Persistent, live-tracked relationship — NOT a one-shot snapshot. This augment's own Health Regen
// contribution continuously tracks 5% of the character's current BonusHealth (MaxHealth minus
// baseMaxHealth — see that property's own doc comment), doubling to 10% while current Health is below 50%
// of Max Health. Picking more Max Health later (or health crossing the half-health line) updates the grant
// live, not just at pick time.
//
// Recomputed every tick in Simulate() (deterministic, replay-safe — BonusHealth/CurrentHealth/MaxHealth are
// themselves already correctly predicted State reads off Stats) into state.desiredAmount, but the actual
// Stats.AddHealthRegenFlat(...) delta only ever fires off a CONFIRMED transition in verified state
// (UpdateView) — a raw Simulate()-computed value isn't safe to apply directly (double-fire risk during a
// client's own in-place resimulation pass, see AugmentEffectPD's own doc comment). _appliedAmount tracks
// exactly how much has been granted so far so only the DELTA between the old and new desired amount is ever
// applied, never the whole value re-added on top of itself.
public class HealthToHealthRegenAugment : AugmentEffectPD<HealthToHealthRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Live-recomputed every tick in Simulate() — see class doc comment.
        public float desiredAmount;
        public void Dispose() { }
    }

    public float bonusHealthToHealthRegenPercent = 0.05f;
    [SerializeField] private float belowHalfHealthMultiplier       = 2f;

    // For AugmentDescriptionFormatter's {LiveBonusHealthRegenRatio} token — mirrors Simulate()'s own
    // state.desiredAmount computation exactly, just recomputed directly off live Stats rather than reading
    // predicted state (this only ever runs off-simulation, from the description formatter, so there's no
    // rollback/determinism concern reading Stats directly here the way Simulate() itself has to be careful
    // about).
    public string LiveBonusHealthRegenRatio(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        bool belowHalfHealth = stats.CurrentHealth < stats.MaxHealth * 0.5f;
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.BonusHealth * bonusHealthToHealthRegenPercent * (belowHalfHealth ? belowHalfHealthMultiplier : 1f))} ";
    }

    // Tracks how much has actually been granted via Stats.AddHealthRegenFlat so far — mutated only from
    // UpdateView() off verified state, so the delta applied each time is always correct regardless of how
    // many times a given tick gets resimulated.
    private float _appliedAmount;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null) return;

        bool belowHalfHealth = Stats.CurrentHealth < Stats.MaxHealth * 0.5f;
        state.desiredAmount = Stats.BonusHealth * bonusHealthToHealthRegenPercent * (belowHalfHealth ? belowHalfHealthMultiplier : 1f);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desired = verified.Value.desiredAmount;
        if (Mathf.Approximately(desired, _appliedAmount)) return;

        Stats.AddHealthRegenFlat(desired - _appliedAmount);
        _appliedAmount = desired;
    }
}
