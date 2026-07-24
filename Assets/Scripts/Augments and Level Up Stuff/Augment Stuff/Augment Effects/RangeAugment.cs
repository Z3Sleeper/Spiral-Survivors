using PurrNet.Prediction;
using UnityEngine;

// Renamed from ArcheriestArcherAugment — no longer class-exclusive, generically available. Tuned to the
// user's 20-point budget (flat/percent points taken 1:1 from StatAugmentDatabase's per-tier values, this
// isn't tracked in code): +2.5 flat range (5pts) + 15% range (15pts) = 20pts.
public class RangeAugment : AugmentEffectPD<RangeAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusWeaponRangeFlat    = 2.5f;
    [SerializeField] private float bonusWeaponRangePercent = 0.15f;

    protected override void OnApply()
    {
        Stats.AddWeaponRangeFlat(bonusWeaponRangeFlat);
        Stats.AddWeaponRangePercent(bonusWeaponRangePercent);
    }
}
