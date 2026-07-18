using PurrNet.Prediction;
using UnityEngine;

// Generic 20-point stat augment: +25 flat Luck (5pts) + 15% Luck (15pts).
public class LuckAugment : AugmentEffectPD<LuckAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusLuckFlat    = 25f;
    [SerializeField] private float bonusLuckPercent = 0.15f;

    protected override void OnApply()
    {
        Stats.AddLuckFlat(bonusLuckFlat);
        Stats.AddLuckPercent(bonusLuckPercent);
    }
}
