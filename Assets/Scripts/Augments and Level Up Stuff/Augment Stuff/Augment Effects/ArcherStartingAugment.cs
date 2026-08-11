using PurrNet.Prediction;
using UnityEngine;

// Archer's class-passive "Unique Behavior" starting augment — grants the fire rate/projectile speed baseline
// that used to live as hardcoded fields on WeaponRangedPD directly (see
// CharacterStatsPD.FireRate/ProjectileSpeed). No longer grants a WeaponRange bonus (removed 2026-08-10) —
// that used to inflate the raw WeaponRange stat itself, which every "AoE Effects" radius formula now also
// reads directly (see DashExplosionAugment's own doc comment), silently blowing those radii up for Archer
// specifically. Archer's actual bow range boost now lives entirely on the WeaponRangedPD prefab itself
// (isRanged + rangeMultiplier — see WeaponBasePD.EffectiveWeaponRange's own doc comment), scoped to just the
// weapon's own firing/aim/projectile-range behavior instead of the shared stat.
public class ArcherStartingAugment : AugmentEffectPD<ArcherStartingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float fireRateTotalPercent = 11f;
    // +19 against ProjectileSpeed's 1f-neutral TotalPercent baseline = a flat x20 multiplier.
    [SerializeField] private float projectileSpeedTotalPercent = 19f;

    protected override void OnApply()
    {
        Stats.AddFireRateTotalPercent(fireRateTotalPercent);
        Stats.AddProjectileSpeedTotalPercent(projectileSpeedTotalPercent);
    }
}
