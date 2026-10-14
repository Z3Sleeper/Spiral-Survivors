using PurrNet.Prediction;
using UnityEngine;

// Same shape as OnDamageTakenReduceAbilityCDAugment, for Energy instead of ability cooldowns — subscribes to
// CharacterStatsPD._onDamaged (fires from TakeDamage on any positive damage taken, excluding a killing blow),
// same trigger PhantomBladeOnDamagedAugment/OnDamageTakenReduceAbilityCDAugment both use. Scales off
// DamageTakenData.preMitigationDamage deliberately, not postMitigationDamage — same reasoning as those two:
// the refill should reflect how hard the player was actually hit, not get diluted by their own shield/
// DamageReduction.
//
// energyPercent is a percent of MaxEnergy PER POINT of pre-mitigation damage taken, same "per-point" shape
// OnDamageTakenReduceAbilityCDAugment's own reductionPercent uses. Default 0.001 (0.1%) means taking 100
// damage in one hit refills 10% of MaxEnergy (100 * 0.001 = 0.1 = 10%).
//
// No Surge-specific reduction — see CharacterStatsPD.GainEnergy's own doc comment: the infinite-Surge-loop
// concern is handled centrally there (a hard zero specifically during Berserk/forced Surge), not per-augment.
// No luck roll — energy restores aren't damage/healing, so this doesn't get luck variance by default (see
// feedback_all_damage_rolls_luck memory).
public class OnDamageTakenRefillEnergyAugment : AugmentEffectPD<OnDamageTakenRefillEnergyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {energyPercent} directly in the description text — see OnDamageTakenReduceAbilityCDAugment's own doc
    // comment (only public fields/properties/methods are checked).
    public float energyPercent = 0.001f;
    // Same flat, unconditional +25 Max Energy every other Surge-family/energy-gain augment grants
    // (SurgeWeaponCountAugment/SurgeStackingAttackSpeedAugment/SurgeHealAugment/FullEnergyBerserkSurgeAugment/
    // DeathPreventionBerserkAugment/OnDashStartedGainEnergyAugment/OnHitEnergyRegenAugment/
    // OnAbilityStartRefillEnergyAugment) — 2026-09-30, granted once in OnApply(), same shape as those.
    public float bonusMaxEnergyFlat = 25f;

    // Shows energyPercent of MaxEnergy PER 1 POINT of pre-mitigation damage taken, for
    // AugmentDescriptionFormatter's {LiveEnergyGain} token — same "per 1 point" baseline-coefficient
    // convention OnDamageTakenReduceAbilityCDAugment.LiveCooldownReduction uses, since the trigger's own
    // per-event damage amount varies hit-to-hit and isn't known ahead of a real hit.
    public string LiveEnergyGain(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "0 ";
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(energyPercent * stats.MaxEnergy)} ";
    }

    protected override void OnApply()
    {
        Stats.AddDamagedListener(OnDamaged);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDamagedListener(OnDamaged);
    }

    private void OnDamaged(DamageTakenData data)
    {
        if (Stats == null || Stats.IsDead) return;

        float gain = energyPercent * Stats.MaxEnergy * data.preMitigationDamage;
        if (gain > 0f) Stats.GainEnergy(gain);
    }
}
