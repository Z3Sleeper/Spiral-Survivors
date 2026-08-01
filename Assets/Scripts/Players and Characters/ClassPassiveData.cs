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
    // Shown on the character-select screen's detail panel (CharacterSelectUI.RefreshCharacterDetails) —
    // not used anywhere in actual gameplay/stat math, purely flavor/explanatory text per class.
    [Header("Character Select Description")]
    [TextArea(3, 6)] public string classDescription;

    [Header("Move Speed")]
    public float bonusMoveSpeedFlat = 0f;
    public float bonusMoveSpeedPercent = 0f;
    public float moveSpeedPenalty = 0f;
    public float totalMoveSpeedPercent = 0f;
    public float totalMoveSpeedPenalty = 0f;

    [Header("Strength")]
    public float bonusStrengthFlat = 0f;
    public float bonusStrengthPercent = 0f;
    public float bonusStrengthPenalty = 0f;
    public float totalStrengthPercent = 0f;
    public float totalStrengthPenalty = 0f;

    [Header("Weapon Radius")]
    public float bonusWeaponRadiusFlat = 0f;
    public float bonusWeaponRadiusPercent = 0f;
    public float totalWeaponRadiusPercent = 0f;
    public float totalWeaponRadiusPenalty = 0f;

    [Header("Attack Speed")]
    // Renamed from attackSpeedPercent to match CharacterStatsPD's own rename — this is a flat, in-units
    // addition to baseAttackSpeed (despite the old name), not a genuine percent multiplier.
    public float attackSpeedFlat = 0f;
    public float attackSpeedTotalPercent = 0f;
    public float attackSpeedPenalty = 0f;

    [Header("Weapon Count")]
    public int bonusWeaponCountFlat = 0;
    public int bonusWeaponCountMultiplier = 1;

    [Header("Weapon Range")]
    public float bonusWeaponRangeFlat = 0f;
    public float bonusWeaponRangePercent = 0f;
    public float weaponRangePenalty = 0f;
    public float totalWeaponRangePercent = 0f;
    public float totalWeaponRangePenalty = 0f;

    [Header("Character Size")]
    // Additive stacking (matches CharacterStatsPD.AddSizePercent) — e.g. +0.25 = +25%.
    public float bonusSizePercent = 0f;

    [Header("Crit Chance")]
    public float bonusCritChanceFlat = 0f;
    public float bonusCritChancePercent = 0f;
    public float totalCritChancePercent = 0f;
    public float totalCritChancePenalty = 0f;

    [Header("Crit Damage")]
    public float bonusCritDamageFlat = 0f;
    public float bonusCritDamagePercent = 0f;

    [Header("Ricochet")]
    public float bonusRicochetFlat = 0f;
    public float bonusRicochetPercent = 0f;
    public float totalRicochetPercent = 0f;
    public float totalRicochetPenalty = 0f;

    [Header("Multishot")]
    public float bonusMultishotFlat = 0f;
    public float bonusMultishotPercent = 0f;
    public float totalMultishotPercent = 0f;
    public float totalMultishotPenalty = 0f;

    [Header("Projectile Dropoff")]
    public float projectileDamageDropoffReduction = 0f;
    public float projectileOnHitDropoffReduction = 0f;

    [Header("Fire Rate")]
    public float fireRateFlat = 0f;
    public float fireRateTotalPercent = 0f;
    public float fireRateTotalPenalty = 0f;

    [Header("Projectile Speed")]
    public float projectileSpeedFlat = 0f;
    public float projectileSpeedTotalPercent = 0f;
    public float projectileSpeedTotalPenalty = 0f;

    [Header("Max Health")]
    public float bonusMaxHealthFlat = 0f;
    public float bonusMaxHealthPercent = 0f;
    public float totalMaxHealthPercent = 0f;
    public float totalMaxHealthPenalty = 0f;

    [Header("Health Regen")]
    public float bonusHealthRegenFlat = 0f;
    public float bonusHealthRegenPercent = 0f;
    public float totalHealthRegenPercent = 0f;
    public float totalHealthRegenPenalty = 0f;

    [Header("Max Shield")]
    public float bonusMaxShieldAdditive = 0f;
    public float totalMaxShieldPercent = 0f;
    public float totalMaxShieldPenalty = 0f;

    [Header("Max Energy")]
    public float bonusMaxEnergyFlat = 0f;
    public float bonusMaxEnergyPercent = 0f;

    [Header("Energy Regen")]
    public float bonusEnergyRegenFlat = 0f;
    public float bonusEnergyRegenPercent = 0f;
    public float energyRegenPenalty = 0f;
    public float totalEnergyRegenPercent = 0f;
    public float totalEnergyRegenPenalty = 0f;

    [Header("Surge")]
    public float bonusSurgeCostFlat = 0f;
    public float bonusSurgeCostPercent = 0f;
    public float surgeAttackSpeedPenalty = 0f;

    // dashHaste plays the "base+flat" role by itself (no separate base field — see CharacterStatsPD's own
    // declaration comment), so bonusDashHastePercent/totalDashHastePercent/totalDashHastePenalty are the
    // rest of the same 5-field convention as every other split stat, just without a distinct bonusFlat.
    [Header("Dash")]
    public float dashHaste = 0f;
    public float bonusDashHastePercent = 0f;
    public float totalDashHastePercent = 0f;
    public float totalDashHastePenalty = 0f;

    // Same shape as Dash's own dashHaste block above — see its comment for the "no separate base field"
    // reasoning, identical here for abilityHaste.
    [Header("Ability Haste")]
    public float abilityHaste = 0f;
    public float bonusAbilityHastePercent = 0f;
    public float totalAbilityHastePercent = 0f;
    public float totalAbilityHastePenalty = 0f;

    [Header("Luck")]
    public float bonusLuckFlat = 0f;
    public float bonusLuckPercent = 0f;
    public float luckPenalty = 0f;
    public float totalLuckPercent = 0f;
    public float totalLuckPenalty = 0f;
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
    public float totalOnHitPowerPercent = 0f;
    public float totalOnHitPowerPenalty = 0f;

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

    // The escape hatch for a passive that needs actual behavior, not just numbers — a real AugmentData,
    // the same kind offered as a level-up pick, just granted directly instead of drawn from the pool.
    // AugmentManagerPD registers each of these as a genuine PlayerAugmentRecord the moment this player's
    // CharacterStatsPD becomes available (see AugmentManagerPD.RegisterClassPassiveAugments), which is what
    // lets the normal pick-pool machinery treat it exactly like a real pick: it won't be re-offered, and
    // any augment listing it (or a class passive's augment) in incompatibleAugments is automatically
    // excluded too — e.g. a class passive using GremlinAugment would make TitanAugment unavailable to that
    // player for free, no class-passive-specific exclusion logic needed. Supports multiple simultaneous
    // effects, same as the old effectPrefabs[] this replaces.
    [Header("Unique Behavior")]
    public AugmentData[] passiveAugments;
}
