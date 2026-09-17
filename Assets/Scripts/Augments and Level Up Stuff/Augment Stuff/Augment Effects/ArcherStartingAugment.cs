using PurrNet.Prediction;
using UnityEngine;

// Archer's class-passive "Unique Behavior" starting augment — grants the fire rate/projectile speed baseline
// that used to live as hardcoded fields on WeaponRangedPD directly (see
// CharacterStatsPD.FireRate/ProjectileSpeed). No longer grants a WeaponRange bonus (removed 2026-08-10) —
// that used to inflate the raw WeaponRange stat itself, which every "AoE Effects" radius formula now also
// reads directly (see DashExplosionAugment's own doc comment), silently blowing those radii up for Archer
// specifically.
//
// weaponRangeScalingMultiplier (2026-09-14) — Archer's actual bow range boost used to live entirely as a
// hardcoded isRanged/rangeMultiplier pair authored directly on the WeaponRangedPD prefab itself, scoped to
// just that weapon's own firing/aim/projectile-range behavior. Moved onto this augment instead, via the
// shared CharacterStatsPD.weaponRangeScalingBonusPercent stat (see its own doc comment) — same underlying
// mechanism BladeweaverStartingAugment's/OuterTrackAugment's own reach reductions now use too (on the
// separate, multiplicative Penalty tier, not this one), unified onto one proper, augmentable character-stat
// pair instead of several one-off approaches to the same "scale the weapon's own geometry without
// inflating/deflating the shared WeaponRange stat any other augment reads" problem. Same
// author-as-a-direct-multiplier convention as fireRateMultiplier/projectileSpeedMultiplier below (2.5 = the
// weapon reaches x2.5 as far, matching this class's previous hardcoded prefab value), converted to the
// underlying additive-to-1 delta in OnApply() below.
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
    [SerializeField] private float weaponRangeScalingMultiplier = 2.5f;

    protected override void OnApply()
    {
        Stats.AddFireRateTotalPercent(fireRateMultiplier - 1f);
        Stats.AddProjectileSpeedTotalPercent(projectileSpeedMultiplier - 1f);
        Stats.AddWeaponRangeScalingBonusPercent(weaponRangeScalingMultiplier - 1f);
    }
}
