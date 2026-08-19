using PurrNet.Prediction;
using UnityEngine;

// +0.25 Luck Ceiling, directly additive against the ceiling value itself (AddLuckCeilingFlat) — at base
// stats, 1.5 -> 1.75. NOT AddLuckCeilingPercent, which would instead scale the 1.5 baseline multiplicatively
// (1.5 * 1.25 = 1.875).
public class LuckCeilingAugment : AugmentEffectPD<LuckCeilingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusLuckCeilingFlat = 0.15f;

    protected override void OnApply()
    {
        Stats.AddLuckCeilingFlat(bonusLuckCeilingFlat);
    }
}
