using PurrNet.Prediction;
using UnityEngine;

public class HealthAugment : AugmentEffectPD<HealthAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusMaxHealthFlat = 60f;
    [SerializeField] private float bonusMaxHealthPercent = 0f;

    protected override void OnApply()
    {
        Stats.AddMaxHealthFlat(bonusMaxHealthFlat);
        Stats.AddMaxHealthPercent(bonusMaxHealthPercent);
    }
}
