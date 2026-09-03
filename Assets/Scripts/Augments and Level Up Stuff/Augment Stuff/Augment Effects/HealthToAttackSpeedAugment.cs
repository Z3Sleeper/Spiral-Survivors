using PurrNet.Prediction;
using UnityEngine;

// Persistent, live-tracked relationship — NOT a one-shot snapshot. Same shape as HealthToHealthRegenAugment
// (see its own doc comment for the full architecture reasoning), just against Attack Speed instead of Health
// Regen: continuously tracks bonusHealthToAttackSpeedPercent (0.5%) of the character's current BonusHealth
// (MaxHealth minus baseMaxHealth) MINUS Stats.ConvertedHealth (the portion of BonusHealth that itself came
// from some OTHER stat-conversion augment), doubling while current Health is below 50% of Max Health.
// Excluding ConvertedHealth is what makes this safe to chain with a future mirror-image conversion augment
// without creating an infinite feedback loop — this augment reads Health as its SOURCE, so it excludes any
// Health that's itself conversion-derived; it also WRITES its own Attack Speed output to
// Stats.ConvertedAttackSpeed below, so a future augment converting FROM Attack Speed could just as safely
// exclude this augment's own contribution. Picking more Max Health later (or Health crossing the half-health
// line) updates the grant live, not just at pick time.
//
// Also grants a flat maxHealthGrant (20) Max Health once, on pickup (see OnApply()) — same reasoning
// HealthToHealthRegenAugment's own identical grant has: otherwise a build with zero BonusHealth investment
// would get literally nothing from this augment until Max Health happened to come from somewhere else.
// Routes through AddMaxHealthFlat (same entry point HealthAugment uses), which already tops current health
// up by the same delta — see CharacterStatsPD.ApplyStatChange's own doc comment.
//
// Recomputed every tick in Simulate() (deterministic, replay-safe — BonusHealth/ConvertedHealth/
// CurrentHealth/MaxHealth are themselves already correctly predicted State reads off Stats) into
// state.desiredAmount, but the actual Stats.AddAttackSpeedFlat(...)/AddConvertedAttackSpeed(...) deltas only
// ever fire off a CONFIRMED transition in verified state (UpdateView) — a raw Simulate()-computed value isn't
// safe to apply directly (double-fire risk during a client's own in-place resimulation pass, see
// AugmentEffectPD's own doc comment). _appliedAmount tracks exactly how much has been granted so far so only
// the DELTA between the old and new desired amount is ever applied, never the whole value re-added on top of
// itself — the identical delta is applied to BOTH the real Attack Speed grant and the ConvertedAttackSpeed
// tracker, every time, so the two never drift out of sync with each other. AddAttackSpeedFlat internally
// multiplies its argument by baseAttackSpeed (despite the "Flat" name it takes an intuitive fraction, e.g.
// 0.005 for +0.5%) and AddConvertedAttackSpeed mirrors that exact same multiply, so passing the identical raw
// delta to both keeps them in matched absolute units.
public class HealthToAttackSpeedAugment : AugmentEffectPD<HealthToAttackSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Live-recomputed every tick in Simulate() — see class doc comment.
        public float desiredAmount;
        public void Dispose() { }
    }

    // 1f = 100% (this codebase's percent fields store the fraction) — 0.005f = 0.5% Attack Speed per 1 Bonus
    // Health.
    public float bonusHealthToAttackSpeedPercent = 0.005f;
    [SerializeField] private float belowHalfHealthMultiplier = 2f;
    // Granted once on pickup (see OnApply()) — see class doc comment for why.
    [SerializeField] private float maxHealthGrant = 20f;

    // For AugmentDescriptionFormatter's {LiveAttackSpeedRatio} token — mirrors Simulate()'s own
    // state.desiredAmount computation, EXCEPT deliberately excludes the belowHalfHealthMultiplier (2026-09-01,
    // explicit design) — the description should always read as the base amount, not whatever the live
    // doubled-or-not state happens to be at the moment the description is rendered. The real grant (Simulate/
    // UpdateView below) still applies the multiplier live as normal; only this preview token ignores it.
    public string LiveAttackSpeedRatio(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "0";
        float convertibleHealth = Mathf.Max(0f, stats.BonusHealth - stats.ConvertedHealth);
        return $"{AugmentDescriptionFormatter.FormatLivePercent(convertibleHealth * bonusHealthToAttackSpeedPercent)}";
    }

    // Tracks how much has actually been granted via Stats.AddAttackSpeedFlat/AddConvertedAttackSpeed so far —
    // mutated only from UpdateView() off verified state, so the delta applied each time is always correct
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
        state.desiredAmount = convertibleHealth * bonusHealthToAttackSpeedPercent * (belowHalfHealth ? belowHalfHealthMultiplier : 1f);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desired = verified.Value.desiredAmount;
        if (Mathf.Approximately(desired, _appliedAmount)) return;

        float delta = desired - _appliedAmount;
        Stats.AddAttackSpeedFlat(delta);
        // Identical delta into the conversion-source tracker, every time, so it never drifts out of sync
        // with the real grant above — see class doc comment for why this exists.
        Stats.AddConvertedAttackSpeed(delta);
        _appliedAmount = desired;
    }
}
