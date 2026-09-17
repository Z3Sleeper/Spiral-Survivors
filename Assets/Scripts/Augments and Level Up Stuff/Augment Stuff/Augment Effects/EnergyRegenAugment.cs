using PurrNet.Prediction;
using UnityEngine;

// Flat Total-tier percent bump — the ONLY way to invest in Energy Regen at all (the old generic per-tier
// "+Energy Regen" level-up stat card was removed from the shop), so this is a single unique-effect pick
// instead of a stackable numeric stat.
//
// 200% (2026-09-03, raised from 100% — was lowered there on 2026-08-23 the same day EnergyRegen itself was
// reworked from a flat units/sec stat into a fraction of MaxEnergy per second, see
// CharacterStatsPD.baseEnergyRegen's own doc comment). Since totalEnergyRegenPercent starts at its own
// neutral 1f baseline, +200% here means this TRIPLES whatever the player's current base+shop regen
// percentage already is, exactly matching the "triple this percentage bonus" design intent.
public class EnergyRegenAugment : AugmentEffectPD<EnergyRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 1f = 100% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float energyRegenTotalPercent = 2f;

    protected override void OnApply()
    {
        Stats.AddEnergyRegenTotalPercent(energyRegenTotalPercent);
    }
}
