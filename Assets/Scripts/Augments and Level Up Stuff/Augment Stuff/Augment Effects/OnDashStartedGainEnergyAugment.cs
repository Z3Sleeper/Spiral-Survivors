using PurrNet.Prediction;
using UnityEngine;

// Gains a PERCENT of MaxEnergy the instant a dash starts (energyPerDashPercent, renamed 2026-09-14 from
// energyPerDash — that name was kept unchanged when the field switched from flat to percent on 2026-09-03,
// per explicit request at the time, but not ending in "Percent" meant AugmentDescriptionFormatter's *100
// auto-scale never applied to its own description token, silently showing "0.1% Energy" instead of "10%
// Energy"; the rename resets the Inspector value back to this field's own default, 0.1, matching the value
// already set) — same "percent of MaxEnergy" shift OnAbilityStartRefillEnergyAugment/OnHitEnergyRegenAugment
// both went through. Subscribes directly to this player's own
// PlayerControllerPD._onDashStarted (same per-owner-instance cross-player-safety reasoning as
// DashHealAugment/PhantomBladeOnDashAugment's identical _controller caching, no shared/static channel for
// another player's dash to leak through). Fires on a real dash AND on any dash-like ability movement that
// calls PlayerControllerPD.NotifyDashStarted() (RogueSpinAbilityPD's leap, BWBlinkAbilityPD) — see that
// method's own doc comment, same convention DashExplosionAugment/DashHealAugment already ride.
//
// No Surge-specific reduction (2026-09-03, was a surgeEnergyGainMultiplier — deliberately weaker while
// Surging) — see CharacterStatsPD.GainEnergy's own doc comment: the infinite-Surge-loop concern this used
// to guard against is now handled centrally there (a hard zero specifically during Berserk/forced Surge),
// not per-augment, and a normal Surge was never actually the problem.
//
// No luck roll — energy restores aren't damage/healing, so this doesn't get luck variance by default (see
// feedback_luckroll_scope memory).
public class OnDashStartedGainEnergyAugment : AugmentEffectPD<OnDashStartedGainEnergyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.10f = 10% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float energyPerDashPercent = 0.10f;
    // Same flat, unconditional +25 Max Energy every other Surge-family energy augment grants
    // (SurgeWeaponCountAugment/SurgeStackingAttackSpeedAugment/SurgeHealAugment/FullEnergyBerserkSurgeAugment/
    // DeathPreventionBerserkAugment) — 2026-09-17, granted once in OnApply(), same shape as those.
    public float bonusMaxEnergyFlat = 25f;

    private PlayerControllerPD _controller;

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null) _controller.AddDashStartedListener(OnDashStarted);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashStartedListener(OnDashStarted);
    }

    private void OnDashStarted()
    {
        if (Stats == null || Stats.IsDead) return;

        Stats.GainEnergy(energyPerDashPercent * Stats.MaxEnergy);
    }
}
