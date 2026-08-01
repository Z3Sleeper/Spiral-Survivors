using PurrNet.Prediction;
using UnityEngine;

// +30% AreaEffectSize (Stats.AddAreaEffectSizePercent) plus +30% AreaEffectPower (Stats.AddAreaEffectPowerPercent)
// — scales radius (and matching VFX) and damage/heal on every "AoE Effects" augment. Also the only other
// source of AreaEffectPower now that AreaEffectPowerAugment has been removed (AreaEffectCountAugment grants
// the rest, compensating for its own auto-divide).
public class AreaEffectSizeAugment : AugmentEffectPD<AreaEffectSizeAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusAreaEffectSizePercent  = 0.3f;
    [SerializeField] private float bonusAreaEffectPowerPercent = 0.3f;

    protected override void OnApply()
    {
        Stats.AddAreaEffectSizePercent(bonusAreaEffectSizePercent);
        Stats.AddAreaEffectPowerPercent(bonusAreaEffectPowerPercent);
    }
}
