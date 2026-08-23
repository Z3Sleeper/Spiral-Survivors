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

    // Author-facing as a direct multiplier (e.g. 8 = x8 Fire Rate) rather than the raw "Total tier" delta
    // CharacterStatsPD's shared fireRateTotalPercent accumulator actually stores — converted to that delta
    // (multiplier - 1f) in OnApply() below. NOT read/touched by ArcherFocusFireAbilityPD (2026-08-21) — that
    // ability's own fire-rate penalty is a separate, standalone multiplicative term
    // (focusFireRateMultiplier) applied against the fully-computed FireRate, specifically so it always takes
    // its full intended percentage off regardless of how large THIS augment's own additive contribution is.
    [SerializeField] private float fireRateMultiplier = 8f;
    [SerializeField] private float projectileSpeedMultiplier = 20f;

    protected override void OnApply()
    {
        Stats.AddFireRateTotalPercent(fireRateMultiplier - 1f);
        Stats.AddProjectileSpeedTotalPercent(projectileSpeedMultiplier - 1f);
    }
}
