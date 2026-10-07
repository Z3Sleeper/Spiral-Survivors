using PurrNet.Prediction;
using UnityEngine;

// Knight's class-passive "Unique Behavior" starting augment (2026-10-07) — see
// [[project_class_passive_convention]]. Persistent, live-tracked relationship, NOT a one-shot snapshot:
// this augment's own Max Energy contribution continuously tracks bonusHealthToMaxEnergyPercent (10%
// default) of the character's current BonusHealth, MINUS Stats.ConvertedHealth (the portion of BonusHealth
// that itself came from some OTHER stat-conversion augment, e.g. a hypothetical HealthRegenToHealthAugment)
// — same "read X - ConvertedX, never raw X" anti-feedback-loop shape HealthToHealthRegenAugment already
// established (see [[project_stat_conversion_augment_pattern]] for the full reasoning). Picking more Max
// Health later updates the grant live, not just at pick time.
//
// Also writes its own output into a brand-new CharacterStatsPD.ConvertedMaxEnergy/AddConvertedMaxEnergy
// pair (mirrors MaxEnergy's own (base + bonusFlat) * bonusPercent shape, minus the base term — same
// pattern ConvertedHealth/ConvertedHealthRegen already use) — pure futureproofing until some future
// augment converts something INTO Max Energy and needs to exclude this one's own contribution.
//
// Recomputed every tick in Simulate() (deterministic, replay-safe), but the actual Stats.AddMaxEnergyFlat/
// AddConvertedMaxEnergy deltas only ever fire off a CONFIRMED transition in verified state (UpdateView) —
// same deferred-delta reasoning HealthToHealthRegenAugment's own doc comment gives (a raw Simulate()-
// computed value isn't safe to apply directly). _appliedAmount tracks exactly how much has been granted so
// far so only the DELTA between the old and new desired amount is ever applied, never the whole value
// re-added on top of itself.
public class KnightStartingAugment : AugmentEffectPD<KnightStartingAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Live-recomputed every tick in Simulate() — see class doc comment.
        public float desiredAmount;
        public void Dispose() { }
    }

    // "Percent" suffix at the END — AugmentDescriptionFormatter auto-*100s it for description tokens.
    public float bonusHealthToMaxEnergyPercent = 0.1f;

    // For AugmentDescriptionFormatter's {LiveMaxEnergyRatio} token — mirrors Simulate()'s own
    // state.desiredAmount computation exactly.
    public string LiveMaxEnergyRatio(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "0";
        float convertibleHealth = Mathf.Max(0f, stats.BonusHealth - stats.ConvertedHealth);
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(convertibleHealth * bonusHealthToMaxEnergyPercent)}";
    }

    // Tracks how much has actually been granted via Stats.AddMaxEnergyFlat/AddConvertedMaxEnergy so far —
    // mutated only from UpdateView() off verified state, so the delta applied each time is always correct
    // regardless of how many times a given tick gets resimulated.
    private float _appliedAmount;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null) return;

        // ConvertedHealth excluded — see class doc comment for why (anti-feedback-loop bookkeeping).
        float convertibleHealth = Mathf.Max(0f, Stats.BonusHealth - Stats.ConvertedHealth);
        state.desiredAmount = convertibleHealth * bonusHealthToMaxEnergyPercent;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desired = verified.Value.desiredAmount;
        if (Mathf.Approximately(desired, _appliedAmount)) return;

        float delta = desired - _appliedAmount;
        Stats.AddMaxEnergyFlat(delta);
        // Identical delta into the conversion-source tracker, every time, so it never drifts out of sync
        // with the real grant above — see class doc comment for why this exists.
        Stats.AddConvertedMaxEnergy(delta);
        _appliedAmount = desired;
    }
}
