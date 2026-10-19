using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. While the player is Surging, heals them (healBase + healthRegenPercent * HealthRegen)
// per second — REWORKED (2026-09-26, was a real-time tick every tickInterval/0.25s) to trigger off energy
// SPENT instead: every energySpentPerHeal (20) energy spent grants one heal chunk, via the same
// CharacterStatsPD._onEnergySpent event SurgeEnergySpentMoveSpeedAugment/SurgeInterruptAoEAugment's own gates
// already use ("the only energy-spend source in this codebase" — Surge's own activation cost AND its ongoing
// per-tick drain both route through it). No heal-NUMBER change needed: 20 energy spent already tracks ~1 real
// second of Surging at Surge's own drain rate, so HealAmount() below is still effectively "per second" in
// practice, just denominated in energy now instead of real time — this reads as part of the "actively
// Surging costs energy, energy funds the heal" loop rather than a free-running background timer. A single
// large spend (e.g. Surge's own activation cost) can cross energySpentPerHeal more than once in one event —
// handled via the while loop in OnEnergySpent below, each crossing granting its own separate chunk.
//
// Routes every heal through the shared Heal() choke point (see its own doc comment), so this correctly
// picks up HealPower/CritHealPower/the always-on crit roll for free — and deliberately does NOT roll luck
// variance, since heals never do (2026-08-12 reversal, see feedback_all_damage_rolls_luck memory). Callers
// pass the RAW per-chunk amount; Heal() itself applies the multiplier.
//
// OnEnergySpent (a PredictedEvent handler, Simulate()-context) only ever accumulates a monotonically-growing
// desiredTotalHealed total in this augment's OWN predicted State (safe — exactly like
// SurgeInterruptAoEAugment's own currentState.stackCount). The actual Stats.Heal() call now fires from this
// augment's own Simulate() (2026-10-16 — was deferred to UpdateView(), off verified state, for just the
// delta since last applied). That deferral was there because a discrete one-shot += is not safe called
// UNGUARDED from Simulate() (risks double-firing during PurrDiction's own in-place/jump resimulation pass —
// see KnightBerserkHealAugment's class doc comment for the full mechanism). But Heal() also rolls GameRandom
// (RollHealMultiplier, whenever CritHealPower != 1f) and fires the _onHeal PredictedEvent, both of which need
// to run deterministically through Simulate(), not from UpdateView()'s per-machine render-cadence timing —
// see CharacterStatsPD.RaiseOnEnemyDeathDeterministic's doc comment for the fuller "why", found the same day.
// Avoided here the same way: appliedAmount lives in predicted State alongside desiredTotalHealed, so
// comparing them is naturally idempotent across an in-place double-call. UpdateView() now only fires the
// heal VFX (2026-09-26, user-curated per-augment, non-persistent heals only), off verified.appliedAmount
// change-detection.
public class SurgeHealAugment : AugmentEffectPD<SurgeHealAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Running total of energy spent since the last qualifying heal chunk — same shape as
        // SurgeEnergySpentMoveSpeedAugment's own energySpentSinceLastTrigger. Crosses energySpentPerHeal to
        // grant one heal chunk; never needs a "seed to float.MaxValue for first-use" trick the way that
        // augment's ONE-SHOT gate does, since this is an ongoing repeating heal, not a single buff toggle —
        // starting at 0 just means the very first heal chunk waits for the first energySpentPerHeal actually
        // spent, same as every subsequent one.
        public float energySpentSinceLastHeal;
        // Monotonically-growing total amount this augment has EARNED so far (never decreases) — grows by one
        // HealAmount() every time energySpentSinceLastHeal crosses energySpentPerHeal (possibly more than
        // once per single large spend).
        public float desiredTotalHealed;
        // How much of desiredTotalHealed has actually been granted via Stats.Heal() so far (2026-10-16,
        // moved from a plain instance field into State) — see class doc comment.
        public float appliedAmount;
        public void Dispose() { }
    }

    public float healBase = 0.4f;
    // 2f = 200% (this codebase's percent fields store the fraction). Scales off Stats.BonusHealthRegen
    // (2026-09-14, was Stats.HealthRegen, the full/total stat) — see that property's own doc comment. Renamed
    // from healthRegenPercent to bonusHealthRegenPercent for family-wide naming consistency with every other
    // "percent of Bonus HealthRegen" healing augment (DashHealAugment/OnDodgeHealHealthRegenAugment/
    // OnHitHealHealthRegenAugment/OnStrikeHealAllyAugment/OnEnemyDeathHealthRegenAugment).
    public float bonusHealthRegenPercent = 2f;
    // Was tickInterval (0.25s, real-time-based) — see class doc comment for the energy-spent rework. Public,
    // not [SerializeField] private (same "AugmentDescriptionFormatter's reflection only checks public
    // members" reasoning SurgeInterruptAoEAugment.energySpentRequirement's own doc comment gives), so
    // {energySpentPerHeal} can be referenced directly in this augment's description text.
    public float energySpentPerHeal = 20f;
    // Same flat, unconditional +25 Max Energy every other Surge-family augment grants (SurgeWeaponCountAugment/
    // SurgeStackingAttackSpeedAugment/FullEnergyBerserkSurgeAugment/DeathPreventionBerserkAugment) — "more
    // Surge uptime per full-to-empty cycle," granted once in OnApply(), same shape as those.
    public float bonusMaxEnergyFlat = 25f;

    // Baseline heal-per-second at 100% HealPower (the crit/HealPower multiply itself isn't included since
    // it varies per-tick, not a stat to preview) for AugmentDescriptionFormatter's {LiveHealPerSecondRatio}
    // token.
    public string LiveHealPerSecondRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(healBase + stats.BonusHealthRegen * bonusHealthRegenPercent)} "
            : "";

    // VFX-dedup only now (2026-10-16) — see KnightBerserkHealAugment's own _vfxFiredAmount doc comment for
    // the full reasoning; the actual heal-applied tracking moved into State.appliedAmount.
    private float _vfxFiredAmount;

    protected override void OnApply()
    {
        Stats.AddEnergySpentListener(OnEnergySpent);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats == null) return;
        Stats.RemoveEnergySpentListener(OnEnergySpent);
    }

    // See class doc comment — while loop handles a single spend (e.g. Surge's own activation cost) crossing
    // energySpentPerHeal more than once at once, granting one HealAmount() per crossing rather than just one.
    private void OnEnergySpent(EnergySpentData data)
    {
        currentState.energySpentSinceLastHeal += data.amount;
        while (currentState.energySpentSinceLastHeal >= energySpentPerHeal)
        {
            currentState.energySpentSinceLastHeal -= energySpentPerHeal;
            currentState.desiredTotalHealed += HealAmount();
        }
    }

    private float HealAmount() => healBase + Stats.BonusHealthRegen * bonusHealthRegenPercent;

    // New override (2026-10-16) — this augment's accumulation happens via the OnEnergySpent event handler
    // above, not a per-tick poll, but the actual Heal() application still needs its own per-tick Simulate()
    // presence so it runs deterministically every tick regardless of whether THIS tick happened to spend any
    // energy. See class doc comment.
    protected override void Simulate(ref State state, float delta)
    {
        if (Stats == null) return;
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

        // Heal VFX opt-in (2026-09-26, user-curated per-augment, non-persistent heals only) — the actual
        // Stats.Heal() call moved to Simulate() (see class doc comment); VFXManager.PlayHealVfx has its own
        // per-target cooldown for the anti-spam side of this.
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
