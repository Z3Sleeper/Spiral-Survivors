using UnityEngine;

public static class ProgressionManager
{
    private const string TotalPointsKey    = "TotalPoints";
    private const string SpentPointsKey    = "SpentPoints";
    private const string BoughtUpgradesKey = "BoughtUpgrades";
    private const string LevelUpTierKey    = "LevelUpTier";

    private const int   LevelUpMaxTier           = 10;
    private const int   BaseMaxTier              = 4;
    private const int   DamageMaxTier            = 7;
    private const int   MoveSpeedMaxTier         = 5;
    private const int   LevelUpStatCost          = 950;
    private const int   BaseStatCost             = 100;
    private const int   CoreStatBaseCost         = 450;
    private const int   CollectionRadiusBaseCost = 450;
    private const int   BaseFee                  = 50;
    private const float FeeMultiplier            = 1.1f;
    private const long  PointsCap               = 1_000_000_000_000_000_000L;

    public static int GetMaxBaseTier(StatType statType) => statType switch
    {
        StatType.WeaponDamage     => DamageMaxTier,
        StatType.MoveSpeed        => MoveSpeedMaxTier,
        StatType.AttackSpeed      => 2,
        StatType.CollectionRadius => 5,
        StatType.EnergyRegen      => 3,
        StatType.WeaponRange      => 2,
        _                         => BaseMaxTier
    };

    public static int LevelUpStatMaxTier => LevelUpMaxTier;

    private static long GetLong(string key) =>
        long.TryParse(PlayerPrefs.GetString(key, "0"), out long v) ? v : 0L;

    private static void SetLong(string key, long value) =>
        PlayerPrefs.SetString(key, value.ToString());

    public static long TotalPoints     => GetLong(TotalPointsKey);
    public static long SpentPoints     => GetLong(SpentPointsKey);
    public static long AvailablePoints => TotalPoints - SpentPoints;
    public static int  BoughtUpgrades  => PlayerPrefs.GetInt(BoughtUpgradesKey, 0);

    public static int GetLevelUpTier() =>
        PlayerPrefs.GetInt(LevelUpTierKey, 0);

    public static int GetBaseStatTier(StatType statType) =>
        PlayerPrefs.GetInt($"BaseStatTier_{statType}", 0);

    public static float GetBaseStatValuePerTier(StatType statType) => statType switch
    {
        StatType.WeaponDamage      => 5f,
        StatType.MaxHealth         => 5f,
        StatType.MoveSpeed         => 1f,
        StatType.AttackSpeed       => 15f,
        StatType.HealthRegen       => 0.5f,
        StatType.EnergyRegen       => 5f,
        StatType.DashHaste         => 10f,
        StatType.CritChance        => 5f,
        StatType.WeaponRange       => 1f,
        StatType.CollectionRadius  => 1f,
        _                          => 0f
    };

    public static float GetBaseStatBonus(StatType statType) =>
        GetBaseStatTier(statType) * GetBaseStatValuePerTier(statType);

    public static int GetBaseStatUpgradeCost(StatType statType)
    {
        int currentTier = GetBaseStatTier(statType);
        if (currentTier >= GetMaxBaseTier(statType)) return int.MaxValue;
        int cost = statType switch
        {
            StatType.WeaponDamage     => CoreStatBaseCost,
            StatType.MoveSpeed        => CoreStatBaseCost,
            StatType.AttackSpeed      => CoreStatBaseCost,
            StatType.WeaponRange      => CoreStatBaseCost,
            StatType.HealthRegen      => CoreStatBaseCost,
            StatType.CollectionRadius => CollectionRadiusBaseCost,
            _                         => BaseStatCost
        };
        int basePart = cost * (1 + currentTier);
        int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, BoughtUpgrades));
        return basePart + feePart;
    }

    public static bool CanUpgradeBaseStat(StatType statType) =>
        GetBaseStatTier(statType) < GetMaxBaseTier(statType) && AvailablePoints >= GetBaseStatUpgradeCost(statType);

    public static bool TryUpgradeBaseStat(StatType statType)
    {
        if (!CanUpgradeBaseStat(statType)) return false;
        int cost = GetBaseStatUpgradeCost(statType);
        PlayerPrefs.SetInt($"BaseStatTier_{statType}", GetBaseStatTier(statType) + 1);
        PlayerPrefs.SetInt(BoughtUpgradesKey, BoughtUpgrades + 1);
        SetLong(SpentPointsKey, SpentPoints + cost);
        PlayerPrefs.Save();
        return true;
    }

    public static float GetStatMultiplier(StatType statType) =>
        1f + GetLevelUpTier() * 0.10f;

    public static int GetUpgradeCost()
    {
        int currentTier = GetLevelUpTier();
        if (currentTier >= LevelUpMaxTier) return int.MaxValue;
        int basePart = LevelUpStatCost * (1 + currentTier);
        int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, BoughtUpgrades));
        return basePart + feePart;
    }

    public static bool CanUpgrade() =>
        GetLevelUpTier() < LevelUpMaxTier && AvailablePoints >= GetUpgradeCost();

    public static bool TryUpgrade()
    {
        if (!CanUpgrade()) return false;
        int cost = GetUpgradeCost();
        PlayerPrefs.SetInt(LevelUpTierKey, GetLevelUpTier() + 1);
        PlayerPrefs.SetInt(BoughtUpgradesKey, BoughtUpgrades + 1);
        SetLong(SpentPointsKey, SpentPoints + cost);
        PlayerPrefs.Save();
        return true;
    }

    public static void CommitPoints(long sessionPoints)
    {
        SetLong(TotalPointsKey, System.Math.Min(TotalPoints + sessionPoints, PointsCap));
        PlayerPrefs.Save();
    }

    public static void SpendPoints(int amount)
    {
        if (amount > AvailablePoints) return;
        SetLong(SpentPointsKey, SpentPoints + amount);
        PlayerPrefs.Save();
    }

    private static readonly StatType[] _upgradeableStats =
    {
        StatType.WeaponDamage, StatType.MaxHealth, StatType.MoveSpeed, StatType.AttackSpeed,
        StatType.HealthRegen,  StatType.EnergyRegen, StatType.DashHaste,
        StatType.CritChance,   StatType.WeaponRange, StatType.CollectionRadius
    };

    private static void DeleteAllStatTiers()
    {
        PlayerPrefs.DeleteKey(LevelUpTierKey);
        foreach (var s in _upgradeableStats)
            PlayerPrefs.DeleteKey($"BaseStatTier_{s}");
    }

    public static void RefundPoints()
    {
        PlayerPrefs.DeleteKey(SpentPointsKey);
        PlayerPrefs.DeleteKey(BoughtUpgradesKey);
        DeleteAllStatTiers();
        PlayerPrefs.Save();
    }

    public static void AddDebugPoints()
    {
        SetLong(TotalPointsKey, System.Math.Min(TotalPoints + 10_000L, PointsCap));
        PlayerPrefs.Save();
    }

    public static void HardReset()
    {
        PlayerPrefs.DeleteKey(TotalPointsKey);
        PlayerPrefs.DeleteKey(SpentPointsKey);
        PlayerPrefs.DeleteKey(BoughtUpgradesKey);
        DeleteAllStatTiers();
        PlayerPrefs.Save();
    }
}
