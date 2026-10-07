using PurrNet.Prediction;
using UnityEngine;

// Universal "all energy sources" percent bump — doubles passive Energy Regen AND every discrete energy
// restore (OnAbilityStartRefillEnergyAugment, OnStrikeEnergyRegenAugment, OnDashStartedGainEnergyAugment)
// uniformly, via CharacterStatsPD.bonusEnergyGainPercent (folded into both EnergyRegen itself and
// GainEnergy() directly — see that property's own doc comment for the full reasoning). Deliberately
// separate from EnergyRegenAugment, which only ever scales the passive regen stat specifically — this one
// is the broader complement, covering every energy source at once rather than just regen.
//
// 100% (2026-09-03) — since bonusEnergyGainPercent starts at its own neutral 1f baseline, +100% here means
// this doubles whatever energy the player would otherwise gain from any source.
public class EnergyGainAugment : AugmentEffectPD<EnergyGainAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 1f = 100% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float energyGainPercent = 1f;
    // Flat, unconditional Max Energy grant (2026-10-03) — same shape as every KnightBerserk augment's own.
    public float bonusMaxEnergyFlat = 25f;

    protected override void OnApply()
    {
        Stats.AddEnergyGainPercent(energyGainPercent);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }
}
