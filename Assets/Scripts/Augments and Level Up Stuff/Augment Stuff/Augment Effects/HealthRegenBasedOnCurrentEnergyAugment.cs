using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — Energy/Health Regen pairing (the two otherwise have zero interaction with each
// other, so nothing currently builds toward both at once). Persistent, live-tracked relationship — NOT a
// one-shot snapshot. Threshold-gated (2026-08-19, reworked from a linear 0-to-max ratio): grants BOTH a flat
// Health Regen amount AND a percent bonus that scales it, at their full maxFlatBonus/maxPercentBonus values
// (default 1 flat / +60%) whenever Current Energy is at or above energyThreshold (default 50%) of Max
// Energy, and NOTHING at all below it — an on/off gate, not a smooth ramp. The flat grant naturally gets
// multiplied by the percent bonus through HealthRegen's own formula ((baseHealthRegen + bonusHealthRegenFlat
// * bonusHealthRegenPercent) * ...), so at base values this contributes 0 below the threshold and
// 1 * 1.6 = 1.6 Health Regen at/above it, not just a flat 1. Adds another layer of thought to
// Surging/spending Energy — draining Energy below the threshold via Surge cuts this whole contribution off
// entirely (both the flat grant AND its own percent multiplier), recovering the instant Energy regenerates
// back up past it.
//
// Recomputed every tick in Simulate() (deterministic, replay-safe — CurrentEnergy/MaxEnergy are themselves
// already correctly predicted State reads off Stats) into state.desiredFlat/state.desiredPercentBonus, but
// the actual Stats.AddHealthRegenFlat/AddHealthRegenPercent deltas only ever fire off a CONFIRMED transition
// in verified state (UpdateView) — a raw Simulate()-computed value isn't safe to apply directly (double-fire
// risk during a client's own in-place resimulation pass, see AugmentEffectPD's own doc comment).
// _appliedFlat/_appliedPercentBonus track exactly how much of each has been granted so far so only the DELTA
// between the old and new desired amount is ever applied, never the whole value re-added on top of itself —
// same pattern HealthToHealthRegenAugment already uses for its own live-tracked bonus, just doubled up since
// this augment drives two stats off the same gate instead of one.
public class HealthRegenBasedOnCurrentEnergyAugment : AugmentEffectPD<HealthRegenBasedOnCurrentEnergyAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Live-recomputed every tick in Simulate() — see class doc comment.
        public float desiredFlat;
        public float desiredPercentBonus;
        public void Dispose() { }
    }

    [SerializeField] private float maxFlatBonus = 1f;
    [SerializeField] private float maxPercentBonus = 0.6f;
    // Fraction of Max Energy Current Energy must be at or above for the bonus to apply at all — below this,
    // both grants are 0. 0.5 = 50% (this codebase's percent fields store the fraction).
    [SerializeField] private float energyThreshold = 0.5f;

    // For AugmentDescriptionFormatter's {LiveHealthRegenMaxBonus} token — the full grant this augment gives
    // once Current Energy is at/above energyThreshold (maxFlatBonus * its own max percent multiplier), not a
    // live-tracked progress number — it's binary (on or off), so there's nothing in between to preview.
    public string LiveHealthRegenMaxBonus(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(maxFlatBonus * (1f + maxPercentBonus))} "
            : "0 ";

    // Tracked only from UpdateView() off verified state, so the delta applied each time is always correct
    // regardless of how many times a given tick gets resimulated — see class doc comment.
    private float _appliedFlat;
    private float _appliedPercentBonus;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;
        if (Stats.MaxEnergy <= 0f) return;

        bool aboveThreshold = Stats.CurrentEnergy / Stats.MaxEnergy >= energyThreshold;
        state.desiredFlat = aboveThreshold ? maxFlatBonus : 0f;
        state.desiredPercentBonus = aboveThreshold ? maxPercentBonus : 0f;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desiredFlat = verified.Value.desiredFlat;
        if (!Mathf.Approximately(desiredFlat, _appliedFlat))
        {
            Stats.AddHealthRegenFlat(desiredFlat - _appliedFlat);
            _appliedFlat = desiredFlat;
        }

        float desiredPercentBonus = verified.Value.desiredPercentBonus;
        if (!Mathf.Approximately(desiredPercentBonus, _appliedPercentBonus))
        {
            Stats.AddHealthRegenPercent(desiredPercentBonus - _appliedPercentBonus);
            _appliedPercentBonus = desiredPercentBonus;
        }
    }
}
