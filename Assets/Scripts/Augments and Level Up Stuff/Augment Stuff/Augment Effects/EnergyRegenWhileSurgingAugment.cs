using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. Lets the player regenerate energy through the standard passive EnergyRegen tick
// while Surging, at a reduced rate — normally fully suppressed (CharacterStatsPD.Simulate's own
// !state.isSurging gate), since Surge is meant to be a resource you spend down and refill only after
// ending it. No state of its own beyond adding to energyRegenWhileSurgingPercent.
public class EnergyRegenWhileSurgingAugment : AugmentEffectPD<EnergyRegenWhileSurgingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float energyRegenPercent = 0.25f;

    protected override void OnApply()
    {
        Stats.AddEnergyRegenWhileSurgingPercent(energyRegenPercent);
    }
}
