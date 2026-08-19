using PurrNet.Prediction;
using UnityEngine;

// Generic 20-point stat augment: +1.25 flat health regen (5pts) + 15% health regen (15pts).
public class HealthRegenAugment : AugmentEffectPD<HealthRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusHealthRegenFlat    = 1.2f;
    [SerializeField] private float bonusHealthRegenPercent = 0f;

    protected override void OnApply()
    {
        Stats.AddHealthRegenFlat(bonusHealthRegenFlat);
        Stats.AddHealthRegenPercent(bonusHealthRegenPercent);
    }
}
