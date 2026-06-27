using UnityEngine;

public static class ProgressionManager
{
    private const string TotalPointsKey    = "TotalPoints";
    private const string SpentPointsKey    = "SpentPoints";
    private const string BoughtUpgradesKey = "BoughtUpgrades";

    private const int   LevelUpMaxTier           = 4;
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

    public static int GetStatTier(StatType statType) =>
        PlayerPrefs.GetInt($"StatTier_{statType}", 0);

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

    public static float GetStatMultiplier(StatType statType) => statType switch
    {
        StatType.WeaponDamage => 1f + GetStatTier(StatType.WeaponDamage) * 0.25f,
        StatType.MaxHealth    => 1f + GetStatTier(StatType.MaxHealth)    * 0.25f,
        StatType.MoveSpeed    => 1f + GetStatTier(StatType.MoveSpeed)    * 0.25f,
        StatType.AttackSpeed  => 1f + GetStatTier(StatType.AttackSpeed)  * 0.25f,
        StatType.HealthRegen  => 1f + GetStatTier(StatType.HealthRegen)  * 0.25f,
        StatType.EnergyRegen  => 1f + GetStatTier(StatType.EnergyRegen)  * 0.25f,
        StatType.DashHaste => 1f + GetStatTier(StatType.DashHaste) * 0.25f,
        StatType.CritChance   => 1f + GetStatTier(StatType.CritChance)   * 0.25f,
        StatType.WeaponRange  => 1f + GetStatTier(StatType.WeaponRange)  * 0.25f,
        _                     => 1f
    };

    public static int GetUpgradeCost(StatType statType)
    {
        int currentTier = GetStatTier(statType);
        if (currentTier >= LevelUpMaxTier) return int.MaxValue;
        int basePart = LevelUpStatCost * (1 + currentTier);
        int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, BoughtUpgrades));
        return basePart + feePart;
    }

    public static bool CanUpgrade(StatType statType) =>
        GetStatTier(statType) < LevelUpMaxTier && AvailablePoints >= GetUpgradeCost(statType);

    public static bool TryUpgrade(StatType statType)
    {
        if (!CanUpgrade(statType)) return false;
        int cost = GetUpgradeCost(statType);
        PlayerPrefs.SetInt($"StatTier_{statType}", GetStatTier(statType) + 1);
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
        foreach (var s in _upgradeableStats)
        {
            PlayerPrefs.DeleteKey($"StatTier_{s}");
            PlayerPrefs.DeleteKey($"BaseStatTier_{s}");
        }
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
