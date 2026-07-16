using PurrNet.Prediction;
using UnityEngine;

public class HealthAugment : AugmentEffectPD<HealthAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float healthBonusFlat = 50f;
    [SerializeField] private float healthBonusPercent = 0.2f;

    protected override void OnApply()
    {
        Stats.AddMaxHealthFlat(healthBonusFlat);
        Stats.AddMaxHealthPercent(healthBonusPercent);
    }
}
