using PurrNet.Prediction;
using UnityEngine;

// Bladeweaver's class-passive "Unique Behavior" starting augment — extra orbiting blades traded for reach:
// more weapons and a bigger orbit radius, more strength to compensate for the extra weapon count's own
// auto-divide, and a much shorter range so the class stays a close-range brawler.
public class BladeweaverStartingAugment : AugmentEffectPD<BladeweaverStartingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int   bonusWeaponCountFlat  = 2;
    [SerializeField] private float bonusWeaponRadiusFlat  = 3f;
    [SerializeField] private float totalStrengthPercent   = 0.25f;
    [SerializeField] private float totalWeaponRangePercent = -0.5f;

    protected override void OnApply()
    {
        Stats.AddWeaponCountFlat(bonusWeaponCountFlat);
        Stats.AddWeaponRadiusFlat(bonusWeaponRadiusFlat);
        Stats.AddStrengthTotalPercent(totalStrengthPercent);
        Stats.AddWeaponRangeTotalPercent(totalWeaponRangePercent);
    }
}
