using PurrNet.Prediction;
using UnityEngine;

// Renamed from ExecutioneriestExecutionerAugment — no longer class-exclusive, generically available.
// Tuned to the user's 20-point budget: +25% flat crit chance (5pts) + 15% bonus crit chance (15pts) = 20pts.
// Renamed again from CritAugment to CritChanceAugment to match naming convention (this grants CritChance,
// not CritDamage) — same .cs/.meta file (preserved GUID), so the existing CritAugment.asset/CritAugment.prefab
// script references keep resolving correctly.
public class CritChanceAugment : AugmentEffectPD<CritChanceAugment.State>
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
