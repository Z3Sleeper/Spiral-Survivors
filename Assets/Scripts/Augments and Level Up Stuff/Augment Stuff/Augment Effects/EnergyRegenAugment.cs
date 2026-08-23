using PurrNet.Prediction;
using UnityEngine;

// Flat Total-tier percent bump — the ONLY way to invest in Energy Regen at all (the old generic per-tier
// "+Energy Regen" level-up stat card was removed from the shop), so this is a single unique-effect pick
// instead of a stackable numeric stat.
//
// 100% (2026-08-23, lowered from 200% the same day EnergyRegen itself was reworked from a flat units/sec
// stat into a fraction of MaxEnergy per second — see CharacterStatsPD.baseEnergyRegen's own doc comment).
// Since totalEnergyRegenPercent starts at its own neutral 1f baseline, +100% here means this doubles
// whatever the player's current base+shop regen percentage already is, exactly matching the "just double
// this percentage bonus" design intent — no longer the "~30% for a unique effect" point-budget exception
// the old +200% version was.
public class EnergyRegenAugment : AugmentEffectPD<EnergyRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 1f = 100% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float energyRegenTotalPercent = 1f;

    protected override void OnApply()
    {
        Stats.AddEnergyRegenTotalPercent(energyRegenTotalPercent);
    }
}
