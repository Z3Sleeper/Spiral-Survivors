using UnityEngine;

// Every field here mirrors one of CharacterStatsPD's [HideInInspector] bonus fields 1:1 (StatAugmentEntry
// only covers the 9 stat types offered as level-up picks — nowhere near everything a passive might want to
// touch). All fields default to a neutral no-op value, so a class only needs to fill in the handful it
// actually uses and can leave the rest untouched.
//
// Combination convention (matches how CharacterStatsPD already combines each field elsewhere):
//  - Fields whose CharacterStatsPD counterpart starts at 0 (flat bonuses, dashHaste, etc.) are additive —
//    default 0 here, added directly.
//  - Fields whose counterpart starts at 1 and is named "...Percent" are additive against that 1f baseline
//    (e.g. +0.2 = +20%, matching AddMaxHealthPercent/ApplyProgressionBonuses/ApplySingleStatEntry) —
//    default 0 here (0 = no change), added directly.
//  - Fields named "...Multiplier", plus reviveTimePenaltyReduction (the one confirmed *= case in
//    ApplyProgressionBonuses), are true multiplicative stacking — default 1 here, multiplied in.
// The "Penalty" fields haven't been observed being combined anywhere in this codebase yet (they're not
// touched by ApplyProgressionBonuses or ApplySingleStatEntry), so their treatment here (additive against a
// 1f baseline, same as the "...Percent" fields) is a best guess, not a confirmed convention — sanity-check
// any of those once you actually use one.
[CreateAssetMenu(fileName = "NewClassPassive", menuName = "Spiral Survivors/Class Passive")]
public class ClassPassiveData : ScriptableObject
{
    [Header("Move Speed")]
    public float bonusMoveSpeedFlat = 0f;
    public float bonusMoveSpeedPercent = 0f;
    public float moveSpeedPenalty = 0f;

    [Header("Weapon Damage")]
    public float bonusWeaponDamageFlat = 0f;
    public float bonusWeaponDamagePercent = 0f;
    public float bonusWeaponDamagePenalty = 0f;

    [Header("Weapon Radius")]
    public float bonusWeaponRadiusFlat = 0f;
    public float bonusWeaponRadiusPercent = 0f;

    [Header("Attack Speed")]
    public float attackSpeedPercent = 0f;
    public float attackSpeedTotalPercent = 0f;
    public float attackSpeedPenalty = 0f;

    [Header("Weapon Count")]
    public int bonusWeaponCountFlat = 0;
    public int bonusWeaponCountMultiplier = 1;

    [Header("Weapon Range")]
    public float bonusWeaponRangeFlat = 0f;
    public float bonusWeaponRangePercent = 0f;
    public float weaponRangePenalty = 0f;

    [Header("Weapon Size")]
    public float bonusWeaponSizePercent = 0f;

    [Header("Character Size")]
    // Additive stacking (matches CharacterStatsPD.AddSizePercent) — e.g. +0.25 = +25%.
    public float bonusSizePercent = 0f;

    [Header("Contact")]
    public bool canContactAllies = false;
    public bool canDamageAllies = false;

    [Header("Crit Chance")]
    public float bonusCritChanceFlat = 0f;
    public float bonusCritChancePercent = 0f;

    [Header("Crit Damage")]
    public float bonusCritDamageFlat = 0f;
    public float bonusCritDamagePercent = 0f;

    [Header("Ricochet")]
    public float bonusRicochetFlat = 0f;
    public float bonusRicochetPercent = 0f;

    [Header("Multishot")]
    public float bonusMultishotFlat = 0f;
    public float bonusMultishotPercent = 0f;

    [Header("Projectile Dropoff")]
    public float projectileDamageDropoffReduction = 0f;
    public float projectileOnHitDropoffReduction = 0f;

    [Header("Max Health")]
    public float bonusMaxHealthFlat = 0f;
    public float bonusMaxHealthPercent = 0f;

    [Header("Health Regen")]
    public float bonusHealthRegenFlat = 0f;
    public float bonusHealthRegenPercent = 0f;

    [Header("Max Shield")]
    public float bonusMaxShieldFlat = 0f;
    public float bonusMaxShieldPercent = 0f;

    [Header("Max Energy")]
    public float bonusMaxEnergyFlat = 0f;
    public float bonusMaxEnergyPercent = 0f;

    [Header("Energy Regen")]
    public float bonusEnergyRegenFlat = 0f;
    public float bonusEnergyRegenPercent = 0f;
    public float energyRegenPenalty = 0f;

    [Header("Surge")]
    public float bonusSurgeCostFlat = 0f;
    public float bonusSurgeCostPercent = 0f;
    public float surgeEffectiveness = 1f;
    public float surgeEffectivenessPenalty = 0f;
    public float surgeAttackSpeedPenalty = 0f;

    [Header("Dash")]
    public float dashHaste = 0f;

    [Header("Max SP")]
    public float bonusMaxSPFlat = 0f;
    public float bonusMaxSPPercent = 0f;

    [Header("Luck")]
    public float bonusLuckFlat = 0f;
    public float bonusLuckPercent = 0f;
    public float luckPenalty = 0f;
    public float bonusLuckFloorPercent = 0f;
    public float luckFloorPenalty = 0f;
    public float bonusLuckCeilingPercent = 0f;
    public float luckCeilingPenalty = 0f;
    public float bonusHighrollPercent = 0f;
    public float highrollPenalty = 0f;
    public float bonusHighrollBonusPercent = 0f;
    public float highrollBonusPenalty = 0f;

    [Header("Knockback")]
    public float knockbackMultiplier = 1f;
    public float knockbackPenalty = 0f;

    [Header("On-Hit Power")]
    public float bonusOnHitPowerMultiplier = 1f;
    public float bonusOnHitPowerPenalty = 0f;

    [Header("Stack Power")]
    public float stackPowerPercent = 0f;
    public float stackPowerPenalty = 0f;

    [Header("XP")]
    public float xpMultiplier = 0f;
    public float xpPenalty = 0f;

    [Header("XP Radius")]
    public float bonusXPRadiusFlat = 0f;
    public float bonusXPRadiusPercent = 0f;

    [Header("Revive")]
    public float reviveTimePenalty = 0f;
    // Multiplicative — the one confirmed *= case (see ApplyProgressionBonuses).
    public float reviveTimePenaltyReduction = 1f;
    public float revivePower = 0f;

    [Header("Damage Reduction")]
    public float damageReductionPercent = 0f;
    public float damageReductionPenalty = 0f;

    // The escape hatch for a passive that needs actual behavior, not just numbers — same
    // AugmentEffectPD<TState> spawn-once-and-apply base class/pattern as augment effects (HealthAugment,
    // TitanAugment), just never offered as a level-up pick. Supports multiple simultaneous effects.
    [Header("Unique Behavior")]
    public GameObject[] effectPrefabs;
}
