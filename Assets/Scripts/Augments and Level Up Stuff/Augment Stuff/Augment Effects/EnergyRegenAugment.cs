using PurrNet.Prediction;
using UnityEngine;

// Generic 20-point stat augment: +5 flat energy regen (5pts) + 15% energy regen (15pts).
public class EnergyRegenAugment : AugmentEffectPD<EnergyRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusEnergyRegenFlat    = 5f;
    [SerializeField] private float bonusEnergyRegenPercent = 0.15f;

    protected override void OnApply()
    {
        Stats.AddEnergyRegenFlat(bonusEnergyRegenFlat);
        Stats.AddEnergyRegenPercent(bonusEnergyRegenPercent);
    }
}
