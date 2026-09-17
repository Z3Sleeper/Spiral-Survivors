using PurrNet.Prediction;
using UnityEngine;

// +50% AreaEffectSize (Stats.AddAreaEffectSizePercent) — scales radius (and matching VFX) on every "AoE
// Effects" augment/ability. Used to also grant AreaEffectPower — removed entirely (2026-09-13, along with
// AreaEffectCountAugment, deleted outright, and the AreaEffectPower/AreaEffectCount stats themselves) —
// bumped 30% -> 50% in the same pass now that this is the augment's sole effect.
public class AreaEffectSizeAugment : AugmentEffectPD<AreaEffectSizeAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusAreaEffectSizePercent = 0.5f;

    protected override void OnApply()
    {
        Stats.AddAreaEffectSizePercent(bonusAreaEffectSizePercent);
    }
}
