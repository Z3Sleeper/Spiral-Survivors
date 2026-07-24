using PurrNet.Prediction;
using UnityEngine;

// +1 Weapon Count (Stats.AddWeaponCountFlat) plus +20% Damage (Stats.AddStrengthTotalPercent) — same
// compensating-bonus pattern as BladeweaviestBladeweaverAugment/MultiShotAugment, offsetting Strength's
// own auto-divide-by-WeaponCount.
public class WeaponCountAugment : AugmentEffectPD<WeaponCountAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int   bonusWeaponCountFlat  = 1;
    [SerializeField] private float totalStrengthPercent  = 0.2f;
    [SerializeField] private float totalOnHitPercent  = 0.2f;

    protected override void OnApply()
    {
        Stats.AddWeaponCountFlat(bonusWeaponCountFlat);
        Stats.AddStrengthTotalPercent(totalStrengthPercent);
        Stats.AddOnHitPowerTotalPercent(totalOnHitPercent);
    }
}
