using PurrNet.Prediction;
using UnityEngine;

// Renamed from ExecutioneriestExecutionerAugment — no longer class-exclusive, generically available.
// Tuned to the user's 20-point budget: +25% flat crit chance (5pts) + 15% total crit chance (15pts) = 20pts.
public class CritAugment : AugmentEffectPD<CritAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusCritChanceFlat   = 0.25f;
    [SerializeField] private float bonusCritChancePercent = 0.15f;

    protected override void OnApply()
    {
        Stats.AddCritChanceFlat(bonusCritChanceFlat);
        Stats.AddCritChancePercent(bonusCritChancePercent);
    }
}
