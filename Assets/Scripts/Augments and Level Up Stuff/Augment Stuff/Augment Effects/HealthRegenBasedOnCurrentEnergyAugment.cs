using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — Energy/Health Regen pairing (the two otherwise have zero interaction with each
// other, so nothing currently builds toward both at once). Persistent, live-tracked relationship — NOT a
// one-shot snapshot. Threshold-gated: grants a flat Health Regen amount (maxFlatBonus, default 2.4) whenever
// Current Energy is at or above energyThreshold (default 75%) of Max Energy, and NOTHING at all below it —
// an on/off gate, not a smooth ramp. Simplified (2026-08-29, explicit design) from an earlier version that
// also granted a percent bonus scaling the flat amount — now a pure flat grant, no percent component at all.
// Adds another layer of thought to Surging/spending Energy — draining Energy below the threshold via Surge
// cuts this whole contribution off entirely, recovering the instant Energy regenerates back up past it.
//
// Recomputed every tick in Simulate() (deterministic, replay-safe — CurrentEnergy/MaxEnergy are themselves
// already correctly predicted State reads off Stats) into state.desiredFlat, but the actual
// Stats.AddHealthRegenFlat delta only ever fires off a CONFIRMED transition in verified state (UpdateView) —
// a raw Simulate()-computed value isn't safe to apply directly (double-fire risk during a client's own
// in-place resimulation pass, see AugmentEffectPD's own doc comment). _appliedFlat tracks exactly how much
// has been granted so far so only the DELTA between the old and new desired amount is ever applied, never
// the whole value re-added on top of itself — same pattern HealthToHealthRegenAugment already uses for its
// own live-tracked bonus.
public class HealthRegenBasedOnCurrentEnergyAugment : AugmentEffectPD<HealthRegenBasedOnCurrentEnergyAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Live-recomputed every tick in Simulate() — see class doc comment.
        public float desiredFlat;
        public void Dispose() { }
    }

    [SerializeField] private float maxFlatBonus = 2.4f;
    // Fraction of Max Energy Current Energy must be at or above for the bonus to apply at all — below this,
    // the grant is 0. 0.75 = 75% (this codebase's percent fields store the fraction).
    [SerializeField] private float energyThreshold = 0.75f;

    // For AugmentDescriptionFormatter's {LiveHealthRegenMaxBonus} token — the flat grant this augment gives
    // once Current Energy is at/above energyThreshold, not a live-tracked progress number — it's binary (on
    // or off), so there's nothing in between to preview.
    public string LiveHealthRegenMaxBonus(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(maxFlatBonus)}"
            : "0";

    // Tracked only from UpdateView() off verified state, so the delta applied each time is always correct
    // regardless of how many times a given tick gets resimulated — see class doc comment.
    private float _appliedFlat;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;
        if (Stats.MaxEnergy <= 0f) return;

        bool aboveThreshold = Stats.CurrentEnergy / Stats.MaxEnergy >= energyThreshold;
        state.desiredFlat = aboveThreshold ? maxFlatBonus : 0f;
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
    }
}
