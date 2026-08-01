using PurrNet.Prediction;
using UnityEngine;

// +1 AreaEffectCount (Stats.AddAreaEffectCount) plus +30% AreaEffectPower (Stats.AddAreaEffectPowerPercent) —
// same compensating-bonus pattern as WeaponCountAugment/BladeweaviestBladeweaverAugment, offsetting
// AreaEffectPower's own auto-divide-by-AreaEffectCount.
public class AreaEffectCountAugment : AugmentEffectPD<AreaEffectCountAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int   bonusAreaEffectCount        = 1;
    [SerializeField] private float bonusAreaEffectPowerPercent = 0.3f;

    protected override void OnApply()
    {
        Stats.AddAreaEffectCount(bonusAreaEffectCount);
        Stats.AddAreaEffectPowerPercent(bonusAreaEffectPowerPercent);
    }
}
