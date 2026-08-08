using PurrNet.Prediction;
using UnityEngine;

// Halves Surge Cost — a flat -50% grant against CharacterStatsPD's own bonusSurgeCostPercent tier (neutral
// 1f baseline, so -0.5 lands it at 0.5 = half — see Stats.AddSurgeCostPercent's own doc comment). One-shot
// flat grant, no active window/State needed, same shape as any other permanent flat-stat augment (e.g.
// LuckAugment).
public class SurgeCostReductionAugment : AugmentEffectPD<SurgeCostReductionAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float surgeCostPercentReduction = 0.5f;

    protected override void OnApply() => Stats.AddSurgeCostPercent(-surgeCostPercentReduction);
}
