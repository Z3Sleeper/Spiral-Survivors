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
// Per AugmentEffectPD's own doc comment, a discrete one-shot Stats.Heal() call is NOT safe directly from
// Simulate() or from a PredictedEvent handler that only mutates cross-identity state (risks double-firing
// during the client's own resimulation/reconciliation pass — same reasoning SurgeStackingAttackSpeedAugment's
// own doc comment gives for why IT defers Stats.AddAttackSpeedFlat to UpdateView() instead of calling it from
// Simulate()). So this augment only ever accumulates a monotonically-growing desiredTotalHealed total in its
// OWN predicted State (safe — the _onEnergySpent handler may freely mutate this augment's own currentState,
// exactly like SurgeInterruptAoEAugment's own currentState.stackCount already does) — the actual
// Stats.Heal() call only ever fires from UpdateView(), off verified state, for just the DELTA since last
// applied, same "recompute fully, apply only the delta" shape HealthToHealthRegenAugment already uses. The
// heal VFX (2026-09-26, user-curated per-augment, non-persistent heals only) piggybacks on that exact same
// verified-state UpdateView() call, so it's exactly as safe as the Heal() call it sits next to.
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
        // once per single large spend). See class doc comment for why the real Stats.Heal() application is
        // deferred to UpdateView() instead of happening right where this grows.
        public float desiredTotalHealed;
        public void Dispose() { }
    }

    [SerializeField] private float healBase = 0.4f;
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
    [SerializeField] private float bonusMaxEnergyFlat = 25f;

    // Baseline heal-per-second at 100% HealPower (the crit/HealPower multiply itself isn't included since
    // it varies per-tick, not a stat to preview) for AugmentDescriptionFormatter's {LiveHealPerSecondRatio}
    // token.
    public string LiveHealPerSecondRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(healBase + stats.BonusHealthRegen * bonusHealthRegenPercent)} "
            : "";

    // Tracks how much has actually been granted via Stats.Heal so far — mutated only from UpdateView() off
    // verified state, so the delta applied each time is always correct regardless of how many times a given
    // tick gets resimulated. Same role as HealthToHealthRegenAugment's own _appliedAmount.
    private float _appliedAmount;

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

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float desired = verified.Value.desiredTotalHealed;
        if (Mathf.Approximately(desired, _appliedAmount)) return;

        Stats.Heal(desired - _appliedAmount);
        _appliedAmount = desired;

        // Heal VFX opt-in (2026-09-26, user-curated per-augment, non-persistent heals only) — piggybacks on
        // this exact same verified-state delta-apply, so it's exactly as safe as the Heal() call above (see
        // VFXManager.PlayHealVfx's own per-target cooldown for the anti-spam side of this).
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
