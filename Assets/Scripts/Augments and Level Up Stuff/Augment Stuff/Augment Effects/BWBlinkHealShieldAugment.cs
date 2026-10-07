using PurrNet.Prediction;
using UnityEngine;

// Bladeweaver BWBlink-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [BWBlink] — scripts-only convention, that part is Editor-side, not here.
//
// On cast: instantly recharges Shield to full (Stats.RechargeShield — same instant-refill-and-fire-the-
// recharge-edge-event entry point DeathPreventionShieldDRAugment's own death save/RogueSpinHealShieldAugment
// use) PLUS healFlat + BonusHealthRegen * bonusHealthRegenPercent (5 + 500% default) — same
// "healBase + BonusHealthRegen * percent" formula shape every Health-Regen-scaled heal in this codebase
// uses. Fired directly from the shared CharacterStatsPD._onAbilityStart event (AddAbilityStartListener,
// filtered by ability.Identity == BWBlink), same safety class as RogueSpinHealShieldAugment's own identical
// one-shot (PredictedEvent handlers already guarantee exactly-once-per-real-event firing).
//
// While BWBlinkAbilityPD.IsActive specifically (its own Move Speed buff window): healPerSecondFlat +
// BonusHealthRegen * healPerSecondRegenPercent (2 + 200% default) PER SECOND, applied in discrete
// tickInterval (0.25s) chunks — same exact chunked-heal-over-time shape KnightBerserkHealAugment/
// RogueVanishHealAugment already established: Simulate() only ever accumulates a monotonically-growing
// desiredTotalHealed into this augment's OWN State (safe — a plain running total), including a pro-rated
// FINAL partial chunk if the active window ends mid-tick. The actual Stats.Heal() call for THIS half only
// ever fires from UpdateView(), off verified state, for the delta since last applied — a discrete one-shot
// Heal() call is NOT safe called repeatedly/directly from Simulate().
public class BWBlinkHealShieldAugment : AugmentEffectPD<BWBlinkHealShieldAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Time accumulated toward the next full tickInterval chunk while the ability is active — flushed
        // as a pro-rated partial chunk the instant it ends (see wasActive below), never carried into a
        // later active window.
        public float tickTimer;
        // Previous tick's BWBlinkAbilityPD.IsActive — lets Simulate() detect the exact falling edge (the
        // active window just ended) to fire the partial-chunk flush exactly once.
        public bool  wasActive;
        // Monotonically-growing total amount the OVER-TIME half has EARNED so far (never decreases) — the
        // real Stats.Heal() application is deferred to UpdateView() instead of happening right where this
        // grows. The ON-CAST half (OnAbilityStart) is a separate, direct one-shot and never touches this.
        public float desiredTotalHealed;
        public void Dispose() { }
    }

    [Header("On Cast")]
    public float healFlat = 5f;
    public float bonusHealthRegenPercent = 5f;

    [Header("Over Time (while active)")]
    public float healPerSecondFlat = 2f;
    public float healPerSecondRegenPercent = 2f;
    // Chunk size — NOT exposed as a description token (implementation/feel detail, not a stat the player
    // is picking for), same "private tickInterval" shape KnightBerserkHealAugment/RogueVanishHealAugment
    // already use.
    [SerializeField] private float tickInterval = 0.25f;

    // Baseline heal amounts at 100% HealPower (the crit/HealPower multiply itself isn't included since it
    // varies per-heal, not a stat to preview) for AugmentDescriptionFormatter's {LiveOnCastHealRatio}/
    // {LiveHealPerSecondRatio} tokens — same shape as RogueVanishHealAugment's own identical pair.
    public string LiveOnCastHealRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(healFlat + stats.BonusHealthRegen * bonusHealthRegenPercent)} "
            : "";

    public string LiveHealPerSecondRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(healPerSecondFlat + stats.BonusHealthRegen * healPerSecondRegenPercent)} "
            : "";

    private BWBlinkAbilityPD _blink;
    // Tracks how much the OVER-TIME half has actually been granted via Stats.Heal so far — mutated only
    // from UpdateView() off verified state, same role as KnightBerserkHealAugment's own _appliedAmount.
    private float _appliedAmount;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _blink = Stats.GetComponent<BWBlinkAbilityPD>();
    }

    protected override void OnApply()
    {
        Stats.AddAbilityStartListener(OnAbilityStart);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAbilityStartListener(OnAbilityStart);
    }

    private void OnAbilityStart(IAbilityPD ability)
    {
        if (Stats == null || Stats.IsDead || ability.Identity != AbilityIdentity.BWBlink) return;
        Stats.RechargeShield();
        Stats.Heal(healFlat + Stats.BonusHealthRegen * bonusHealthRegenPercent);
        // Heal VFX — safe to call directly here, same as RogueSpinHealShieldAugment/RogueVanishHealAugment's
        // own OnAbilityStart: PlayHealVfx has its own per-target cooldown dedup built in, so a direct call
        // from this discrete, event-triggered call site is fine.
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }

    // seconds * (healPerSecondFlat + BonusHealthRegen * healPerSecondRegenPercent) — a full tickInterval
    // chunk and the final partial chunk both just call this with however many seconds actually elapsed.
    private float OverTimeHealAmount(float seconds) =>
        (healPerSecondFlat + Stats.BonusHealthRegen * healPerSecondRegenPercent) * seconds;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (_blink == null || Stats == null) { state.wasActive = false; return; }

        bool isActive = _blink.IsActive;
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
