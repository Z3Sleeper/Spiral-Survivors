using PurrNet.Prediction;
using UnityEngine;

// Rogue Vanish-specific augment (2026-10-05). This augment's own .asset must set requiredAbilities =
// [RogueVanish] — scripts-only convention, that part is Editor-side, not here.
//
// Two separate heals, same "healBase + BonusHealthRegen * percent" formula shape every Health-Regen-scaled
// heal in this codebase uses (SurgeHealAugment/DashHealAugment/RogueSpinHealShieldAugment), just two
// different cadences:
//  1. On cast: healFlat + BonusHealthRegen * bonusHealthRegenPercent (10 + 1000% default) — a one-shot,
//     fired directly from the shared CharacterStatsPD._onAbilityStart event (AddAbilityStartListener,
//     filtered by ability.Identity == RogueVanish), same safety class as RogueSpinHealShieldAugment's own
//     identical one-shot (PredictedEvent handlers already guarantee exactly-once-per-real-event firing).
//  2. While RogueVanishAbilityPD.IsActive specifically (its own 3s active window — NOT the possibly-LONGER
//     invisibility duration RogueVanishBlinkAugment/RogueVanishHighrollAugment's own
//     invisibilityDurationBonusPercent can extend; "during the ability active time" per Stuart's own
//     framing): healPerSecondFlat + BonusHealthRegen * healPerSecondRegenPercent (5 + 500% default) PER
//     SECOND, applied in discrete tickInterval (0.25s) chunks — each chunk is 1/4 of that amount, not a
//     smooth per-frame trickle. Same exact chunked-heal-over-time shape KnightBerserkHealAugment already
//     established: Simulate() only ever accumulates a monotonically-growing desiredTotalHealed into this
//     augment's OWN State (safe — a plain running total, same class as any ability's own cooldownTimer
//     tick), including a pro-rated FINAL partial chunk if the active window ends mid-tick. The actual
//     Stats.Heal() call for THIS half only ever fires from UpdateView(), off verified state, for the delta
//     since last applied — see KnightBerserkHealAugment's own doc comment for the full "why" (a discrete
//     one-shot Heal() call is NOT safe called repeatedly/directly from Simulate()).
//
// Also grants +invisibilityDurationBonusPercent (50% default) to the invisibility duration itself — same
// shared field RogueVanishBlinkAugment/RogueVanishHighrollAugment already use, stacking additively with
// both (all three now default to 50% each, so having all three stacks to +150% total).
public class RogueVanishHealAugment : AugmentEffectPD<RogueVanishHealAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Time accumulated toward the next full tickInterval chunk while the ability is active — flushed
        // as a pro-rated partial chunk the instant it ends (see wasActive below), never carried into a
        // later active window.
        public float tickTimer;
        // Previous tick's RogueVanishAbilityPD.IsActive — lets Simulate() detect the exact falling edge
        // (the active window just ended) to fire the partial-chunk flush exactly once.
        public bool  wasActive;
        // Monotonically-growing total amount the OVER-TIME half has EARNED so far (never decreases) — the
        // real Stats.Heal() application is deferred to UpdateView() instead of happening right where this
        // grows. The ON-CAST half (OnAbilityStart) is a separate, direct one-shot and never touches this.
        public float desiredTotalHealed;
        public void Dispose() { }
    }

    [Header("On Cast")]
    public float healFlat = 10f;
    public float bonusHealthRegenPercent = 10f;

    [Header("Over Time (while active)")]
    public float healPerSecondFlat = 5f;
    public float healPerSecondRegenPercent = 5f;
    // Chunk size — NOT exposed as a description token (implementation/feel detail, not a stat the player
    // is picking for), same "private tickInterval" shape KnightBerserkHealAugment/ArcherArrowRainAbilityPD
    // already use.
    [SerializeField] private float tickInterval = 0.25f;

    [Header("Duration")]
    public float invisibilityDurationBonusPercent = 0.5f;

    // Baseline heal amounts at 100% HealPower (the crit/HealPower multiply itself isn't included since it
    // varies per-heal, not a stat to preview) for AugmentDescriptionFormatter's {LiveOnCastHealRatio}/
    // {LiveHealPerSecondRatio} tokens — same shape as SurgeHealAugment's own LiveHealPerSecondRatio.
    public string LiveOnCastHealRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(healFlat + stats.BonusHealthRegen * bonusHealthRegenPercent)} "
            : "";

    public string LiveHealPerSecondRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(healPerSecondFlat + stats.BonusHealthRegen * healPerSecondRegenPercent)} "
            : "";

    private RogueVanishAbilityPD _vanish;
    // Tracks how much the OVER-TIME half has actually been granted via Stats.Heal so far — mutated only
    // from UpdateView() off verified state, same role as KnightBerserkHealAugment's own _appliedAmount.
    private float _appliedAmount;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _vanish = Stats.GetComponent<RogueVanishAbilityPD>();
    }

    protected override void OnApply()
    {
        Stats.AddAbilityStartListener(OnAbilityStart);
        if (_vanish != null) _vanish.invisibilityDurationBonusPercent += invisibilityDurationBonusPercent;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAbilityStartListener(OnAbilityStart);
    }

    private void OnAbilityStart(IAbilityPD ability)
    {
        if (Stats == null || Stats.IsDead || ability.Identity != AbilityIdentity.RogueVanish) return;
        Stats.Heal(healFlat + Stats.BonusHealthRegen * bonusHealthRegenPercent);
        // Heal VFX — safe to call directly here, same as RogueSpinHealShieldAugment/KnightBuffStunAbilityPD's
        // own OnAoECastComplete: PlayHealVfx has its own per-target cooldown dedup built in (VFXManager's own
        // healVfxCooldown), so a direct call from this discrete, event-triggered call site is fine.
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }

    // seconds * (healPerSecondFlat + BonusHealthRegen * healPerSecondRegenPercent) — a full tickInterval
    // chunk and the final partial chunk both just call this with however many seconds actually elapsed.
    private float OverTimeHealAmount(float seconds) =>
        (healPerSecondFlat + Stats.BonusHealthRegen * healPerSecondRegenPercent) * seconds;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (_vanish == null || Stats == null) { state.wasActive = false; return; }

        bool isActive = _vanish.IsActive;
        if (isActive)
        {
            state.tickTimer += delta;
            while (state.tickTimer >= tickInterval)
            {
                state.tickTimer -= tickInterval;
                state.desiredTotalHealed += OverTimeHealAmount(tickInterval);
            }
        }
        else if (state.wasActive && state.tickTimer > 0f)
        {
            state.desiredTotalHealed += OverTimeHealAmount(state.tickTimer);
            state.tickTimer = 0f;
        }

        state.wasActive = isActive;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desired = verified.Value.desiredTotalHealed;
        if (Mathf.Approximately(desired, _appliedAmount)) return;

        Stats.Heal(desired - _appliedAmount);
        _appliedAmount = desired;

        // Heal VFX — see class doc comment / OnAbilityStart's own identical call for why this is safe
        // called directly off this exact verified-state delta-apply.
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
