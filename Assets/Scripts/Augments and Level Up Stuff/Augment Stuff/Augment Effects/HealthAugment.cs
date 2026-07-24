using PurrNet.Prediction;
using UnityEngine;

public class HealthAugment : AugmentEffectPD<HealthAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusMaxHealthFlat = 25f;
    [SerializeField] private float bonusMaxHealthPercent = 0.15f;

    protected override void OnApply()
    {
        Stats.AddMaxHealthFlat(bonusMaxHealthFlat);
        Stats.AddMaxHealthPercent(bonusMaxHealthPercent);
    }
}
