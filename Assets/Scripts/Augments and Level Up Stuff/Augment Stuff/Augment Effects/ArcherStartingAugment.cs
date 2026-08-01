using PurrNet.Prediction;
using UnityEngine;

// Archer's class-passive "Unique Behavior" starting augment — grants the fire rate/range/projectile speed
// baseline that used to live as hardcoded fields on WeaponRangedPD directly (see
// CharacterStatsPD.FireRate/WeaponRange/ProjectileSpeed).
public class ArcherStartingAugment : AugmentEffectPD<ArcherStartingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float fireRateTotalPercent = 11f;
    // +1 against WeaponRange's 1f-neutral TotalPercent baseline = +100% (doubles range). Swapped from a
    // flat grant (AddWeaponRangeFlat) so it scales multiplicatively with whatever else is boosting range,
    // same Total-tier convention as fireRateTotalPercent/projectileSpeedTotalPercent below.
    [SerializeField] private float weaponRangeTotalPercent = 1f;
    // +19 against ProjectileSpeed's 1f-neutral TotalPercent baseline = a flat x20 multiplier.
    [SerializeField] private float projectileSpeedTotalPercent = 19f;

    protected override void OnApply()
    {
        Stats.AddFireRateTotalPercent(fireRateTotalPercent);
        Stats.AddWeaponRangeTotalPercent(weaponRangeTotalPercent);
        Stats.AddProjectileSpeedTotalPercent(projectileSpeedTotalPercent);
    }
}
