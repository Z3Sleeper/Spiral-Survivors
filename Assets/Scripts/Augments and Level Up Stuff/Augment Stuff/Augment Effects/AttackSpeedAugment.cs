using PurrNet.Prediction;
using UnityEngine;

// Generic 20-point stat augment: +25% attack speed (5pts, via attackSpeedPercent — added directly to base,
// same as the progression system's own AttackSpeed stat conversion) + 15% total attack speed (15pts, via
// attackSpeedTotalPercent — the genuine multiplier).
public class AttackSpeedAugment : AugmentEffectPD<AttackSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusAttackSpeedPercent      = 0.25f;
    [SerializeField] private float bonusAttackSpeedTotalPercent = 0.15f;

    protected override void OnApply()
    {
        Stats.AddAttackSpeedPercent(bonusAttackSpeedPercent);
        Stats.AddAttackSpeedTotalPercent(bonusAttackSpeedTotalPercent);
    }
}
