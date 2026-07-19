using PurrNet.Prediction;
using UnityEngine;

// +1 Weapon Count (Stats.AddWeaponCountFlat) plus +20% Weapon Damage (Stats.AddWeaponDamagePercent) — same
// compensating-bonus pattern as BladeweaviestBladeweaverAugment/MultiShotAugment, offsetting WeaponDamage's
// own auto-divide-by-WeaponCount.
public class WeaponCountAugment : AugmentEffectPD<WeaponCountAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int   weaponCountBonus = 1;
    [SerializeField] private float damageBonus      = 0.2f;

    protected override void OnApply()
    {
        Stats.AddWeaponCountFlat(weaponCountBonus);
        Stats.AddWeaponDamagePercent(damageBonus);
    }
}
