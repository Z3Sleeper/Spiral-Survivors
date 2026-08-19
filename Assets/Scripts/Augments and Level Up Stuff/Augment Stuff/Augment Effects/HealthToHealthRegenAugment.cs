using PurrNet.Prediction;
using UnityEngine;

// Persistent, live-tracked relationship — NOT a one-shot snapshot. This augment's own Health Regen
// contribution continuously tracks 5% of the character's current BonusHealth (MaxHealth minus
// baseMaxHealth — see that property's own doc comment) MINUS Stats.ConvertedHealth (2026-08-19 — the
// portion of BonusHealth that itself came from some OTHER stat-conversion augment, e.g. a hypothetical
// HealthRegenToHealthAugment), doubling to 10% while current Health is below 50% of Max Health. Excluding
// ConvertedHealth is what makes this safe to chain with a future mirror-image conversion augment without
// creating an infinite feedback loop — see ConvertedHealth's own doc comment on CharacterStatsPD for the
// full reasoning (this augment reads Health as its SOURCE, so it excludes any Health that's itself
// conversion-derived; it also WRITES its own HealthRegen output to ConvertedHealthRegen below, so a future
// augment converting FROM HealthRegen can just as safely exclude this augment's own contribution). Picking
// more Max Health later (or health crossing the half-health line) updates the grant live, not just at pick
// time. Also grants a flat maxHealthGrant (20) Max Health once, on pickup (see OnApply()) — otherwise a
// build with zero BonusHealth investment would get literally nothing from this augment until Max Health
// happened to come from somewhere else.
//
// Recomputed every tick in Simulate() (deterministic, replay-safe — BonusHealth/ConvertedHealth/
// CurrentHealth/MaxHealth are themselves already correctly predicted State reads off Stats) into
// state.desiredAmount, but the actual Stats.AddHealthRegenFlat(...)/AddConvertedHealthRegen(...) deltas only
// ever fire off a CONFIRMED transition in verified state (UpdateView) — a raw Simulate()-computed value
// isn't safe to apply directly (double-fire risk during a client's own in-place resimulation pass, see
// AugmentEffectPD's own doc comment). _appliedAmount tracks exactly how much has been granted so far so only
// the DELTA between the old and new desired amount is ever applied, never the whole value re-added on top of
// itself — the identical delta is applied to BOTH the real HealthRegen grant and the ConvertedHealthRegen
// tracker, every time, so the two never drift out of sync with each other.
public class HealthToHealthRegenAugment : AugmentEffectPD<HealthToHealthRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Live-recomputed every tick in Simulate() — see class doc comment.
        public float desiredAmount;
        public void Dispose() { }
    }

    public float bonusHealthToHealthRegenPercent = 0.02f;
    [SerializeField] private float belowHalfHealthMultiplier       = 2f;
    // Granted once on pickup (2026-08-12, playtesting: this augment did literally nothing for a build with
    // zero BonusHealth investment) — a flat Max Health grant so the live-tracked HealthRegen conversion
    // below has something to work off immediately, not just whenever Max Health happens to get picked up
    // later. Routes through AddMaxHealthFlat (same entry point HealthAugment uses), which already tops
    // current health up by the same delta — see CharacterStatsPD.ApplyStatChange's own doc comment.
    [SerializeField] private float maxHealthGrant = 20f;

    // For AugmentDescriptionFormatter's {LiveBonusHealthRegenRatio} token — mirrors Simulate()'s own
    // state.desiredAmount computation exactly, just recomputed directly off live Stats rather than reading
    // predicted state (this only ever runs off-simulation, from the description formatter, so there's no
    // rollback/determinism concern reading Stats directly here the way Simulate() itself has to be careful
    // about).
    public string LiveBonusHealthRegenRatio(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        bool belowHalfHealth = stats.CurrentHealth < stats.MaxHealth * 0.5f;
        float convertibleHealth = Mathf.Max(0f, stats.BonusHealth - stats.ConvertedHealth);
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(convertibleHealth * bonusHealthToHealthRegenPercent * (belowHalfHealth ? belowHalfHealthMultiplier : 1f))} ";
    }

    // Tracks how much has actually been granted via Stats.AddHealthRegenFlat/AddConvertedHealthRegen so far
    // — mutated only from UpdateView() off verified state, so the delta applied each time is always correct
    // regardless of how many times a given tick gets resimulated.
    private float _appliedAmount;

    protected override void OnApply()
    {
        Stats.AddMaxHealthFlat(maxHealthGrant);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null) return;

        bool belowHalfHealth = Stats.CurrentHealth < Stats.MaxHealth * 0.5f;
        // ConvertedHealth excluded — see class doc comment for why (anti-feedback-loop bookkeeping).
        float convertibleHealth = Mathf.Max(0f, Stats.BonusHealth - Stats.ConvertedHealth);
        state.desiredAmount = convertibleHealth * bonusHealthToHealthRegenPercent * (belowHalfHealth ? belowHalfHealthMultiplier : 1f);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desired = verified.Value.desiredAmount;
        if (Mathf.Approximately(desired, _appliedAmount)) return;

        float delta = desired - _appliedAmount;
        Stats.AddHealthRegenFlat(delta);
        // Identical delta into the conversion-source tracker, every time, so it never drifts out of sync
        // with the real grant above — see class doc comment for why this exists.
        Stats.AddConvertedHealthRegen(delta);
        _appliedAmount = desired;
    }
}
