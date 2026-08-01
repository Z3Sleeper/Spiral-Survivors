using PurrNet.Prediction;
using UnityEngine;

// Archer's class-passive "Unique Behavior" starting augment — grants the fire rate/range/projectile speed
// baseline that used to live as hardcoded fields on WeaponRangedPD directly (see
// CharacterStatsPD.FireRate/WeaponRange/ProjectileSpeed).
public class ArcherStartingAugment : AugmentEffectPD<ArcherStartingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float fireRateTotalPercent = 11f;
    [SerializeField] private float bonusWeaponRangeFlat  = 10f;
    // +19 against ProjectileSpeed's 1f-neutral TotalPercent baseline = a flat x20 multiplier.
    [SerializeField] private float projectileSpeedTotalPercent = 19f;

    protected override void OnApply()
    {
        Stats.AddFireRateTotalPercent(fireRateTotalPercent);
        Stats.AddWeaponRangeFlat(bonusWeaponRangeFlat);
        Stats.AddProjectileSpeedTotalPercent(projectileSpeedTotalPercent);
    }
}
