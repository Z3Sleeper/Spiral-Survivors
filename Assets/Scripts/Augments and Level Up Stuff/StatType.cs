public enum StatType
{
    MaxHealth    = 0,
    Strength = 1,
    MoveSpeed    = 2,
    WeaponRange  = 3,
    AttackSpeed  = 4,
    HealthRegen  = 5,
    EnergyRegen  = 7,
    DashHaste = 8,
    CritChance        = 9,
    CollectionRadius  = 11,
    MaxEnergy         = 12,
    DashCharges       = 13,
    RevivePower       = 14,
    ReviveTimePenalty = 15,
    XPMultiplier      = 16,
    LevelUpOptions    = 17,
    LevelUpStats      = 18,
    Luck                = 19,
    // 20 (TotalStrengthPercent) removed 2026-09-30 — permanent-soft-disable shop item scrapped entirely per
    // explicit request, never coming back. Gap preserved, not reused — see project_totalstrengthpercent_removal_planned memory.
    StrengthPerLevel    = 21,
    MaxShield           = 22,
    ShieldRechargeTime  = 23,
    AbilityHaste        = 24,
    DeathPrevention     = 25,
    // 26 (MoveSpeedPercent) removed 2026-09-30 — same treatment/reasoning as TotalStrengthPercent above.
    RerollTokens        = 27,
    SurgeMoveSpeedPenaltyReduction = 28,
    RerollTokenLevelsPerGrant = 29,
}