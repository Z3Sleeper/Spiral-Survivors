using PurrNet.Prediction;
using UnityEngine;

public class HealthAugment : AugmentEffectPD<HealthAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float healthBonus = 100f;

    protected override void OnApply() => Stats.AddMaxHealthFlat(healthBonus);
}
