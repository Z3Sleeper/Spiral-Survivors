using PurrNet.Prediction;
using UnityEngine;

// Renamed from SurgeCostIncreaseAugment (2026-08-24) — the Surge Cost increase half is gone entirely, this
// is now purely a Surge Attack Speed multiplier. Multiplies SurgeAttackSpeedMultiplier by
// surgeAttackSpeedMultiplier (1.3x, lowered from 1.5x in the same pass — no longer needs the "go big or go
// home" tradeoff pricing now that there's no accompanying cost increase to offset). One-shot permanent
// grant, no active window/State needed, same shape as any other permanent flat-stat augment (e.g.
// LuckAugment).
public class SurgeAttackSpeedMultiplierAugment : AugmentEffectPD<SurgeAttackSpeedMultiplierAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float surgeAttackSpeedMultiplier = 1.3f;

    protected override void OnApply()
    {
        Stats.MultiplySurgeAttackSpeedPenalty(surgeAttackSpeedMultiplier);
    }
}
