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
//  2. While RogueVanishAbilityPD.IsActive specifically (its own active window — 2026-10-14 rework: this is
//     now the SAME number durationBonusPercent extends, since that field was folded directly into the
//     ability's real duration instead of only scaling the invisibility contribution separately — see
//     durationBonusPercent's own doc comment on RogueVanishAbilityPD. So this heal-over-time genuinely runs
//     longer too when Blink/Highroll/this augment's own duration bonus are picked, not just the original 3s
//     baseline — explicit, intended consequence of unifying the number, not a bug): healPerSecondFlat +
//     BonusHealthRegen * healPerSecondRegenPercent (5 + 500% default) PER
//     SECOND, applied in discrete tickInterval (0.25s) chunks — each chunk is 1/4 of that amount, not a
//     smooth per-frame trickle. Same exact chunked-heal-over-time shape KnightBerserkHealAugment already
//     established: Simulate() accumulates a monotonically-growing desiredTotalHealed into this augment's OWN
//     State (safe — a plain running total, same class as any ability's own cooldownTimer tick), including a
//     pro-rated FINAL partial chunk if the active window ends mid-tick. The actual Stats.Heal() call for
//     THIS half ALSO now fires from Simulate() (2026-10-16 — was deferred to UpdateView(); see
//     KnightBerserkHealAugment's own doc comment for the full "why" this moved, found the same day), guarded
//     by comparing against appliedAmount in State so it stays idempotent across an in-place double-call.
//
// Also grants +durationBonusPercent (50% default) to the ability's WHOLE duration (renamed from
// invisibilityDurationBonusPercent, 2026-10-14 rework — see that field's own doc comment on
// RogueVanishAbilityPD) — same shared field RogueVanishBlinkAugment/RogueVanishHighrollAugment already use,
// stacking additively with both (all three now default to 50% each, so having all three stacks to +150%
// total).
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
        // Monotonically-growing total amount the OVER-TIME half has EARNED so far (never decreases). The
        // ON-CAST half (OnAbilityStart) is a separate, direct one-shot and never touches this.
        public float desiredTotalHealed;
        // How much of desiredTotalHealed has actually been granted via Stats.Heal() so far (2026-10-16,
        // moved from a plain instance field into State) — see class doc comment.
        public float appliedAmount;
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
    public float durationBonusPercent = 0.5f;

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
    // VFX-dedup only now (2026-10-16) — see KnightBerserkHealAugment's own _vfxFiredAmount doc comment; the
    // actual heal-applied tracking moved into State.appliedAmount.
    private float _vfxFiredAmount;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _vanish = Stats.GetComponent<RogueVanishAbilityPD>();
    }

    protected override void OnApply()
    {
        Stats.AddAbilityStartListener(OnAbilityStart);
        if (_vanish != null) _vanish.durationBonusPercent += durationBonusPercent;
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

        // Applies the real heal the instant this tick's accumulation grows past what's already been
        // granted — see class doc comment. Comparing against appliedAmount (State, not a plain field) keeps
        // this idempotent across an in-place double-call of this same tick.
        if (state.desiredTotalHealed > state.appliedAmount)
        {
            Stats.Heal(state.desiredTotalHealed - state.appliedAmount);
            state.appliedAmount = state.desiredTotalHealed;
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float applied = verified.Value.appliedAmount;
        if (Mathf.Approximately(applied, _vfxFiredAmount)) return;
        _vfxFiredAmount = applied;

        // Heal VFX only now — the actual Stats.Heal() call moved into Simulate() above (see class doc
        // comment). OnAbilityStart's own on-cast heal still calls PlayHealVfx directly, unaffected.
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
