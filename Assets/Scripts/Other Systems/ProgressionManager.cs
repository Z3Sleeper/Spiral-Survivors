using UnityEngine;

public static class ProgressionManager
{
    private const string TotalPointsKey    = "TotalPoints";
    private const string SpentPointsKey    = "SpentPoints";
    private const string BoughtUpgradesKey = "BoughtUpgrades";

    private const int   MaxTier                  = 5;
    private const int   LevelUpStatCost          = 950;
    private const int   BaseStatCost             = 200;
    private const int   CollectionRadiusBaseCost = 450;
    private const int   BaseFee                  = 50;
    private const float FeeMultiplier            = 1.1f;

    public static int TotalPoints     => PlayerPrefs.GetInt(TotalPointsKey, 0);
    public static int SpentPoints     => PlayerPrefs.GetInt(SpentPointsKey, 0);
    public static int AvailablePoints => TotalPoints - SpentPoints;
    public static int BoughtUpgrades  => PlayerPrefs.GetInt(BoughtUpgradesKey, 0);

    public static int GetStatTier(StatType statType) =>
        PlayerPrefs.GetInt($"StatTier_{statType}", 0);

    public static int GetBaseStatTier(StatType statType) =>
        PlayerPrefs.GetInt($"BaseStatTier_{statType}", 0);

    public static float GetBaseStatValuePerTier(StatType statType) => statType switch
    {
        StatType.WeaponDamage      => 3f,
        StatType.MaxHealth         => 5f,
        StatType.MoveSpeed         => 0.25f,
        StatType.AttackSpeed       => 5f,
        StatType.HealthRegen       => 0.25f,
        StatType.EnergyRegen       => 5f,
        StatType.DashCooldown      => 10f,
        StatType.CritChance        => 5f,
        StatType.WeaponRange       => 0.5f,
        StatType.CollectionRadius  => 0.5f,
        _                          => 0f
    };

    public static float GetBaseStatBonus(StatType statType) =>
        GetBaseStatTier(statType) * GetBaseStatValuePerTier(statType);

    public static int GetBaseStatUpgradeCost(StatType statType)
    {
        int currentTier = GetBaseStatTier(statType);
        if (currentTier >= MaxTier) return int.MaxValue;
        int cost = statType == StatType.CollectionRadius ? CollectionRadiusBaseCost : BaseStatCost;
        int basePart = cost * (1 + currentTier);
        int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, BoughtUpgrades));
        return basePart + feePart;
    }

    public static bool CanUpgradeBaseStat(StatType statType) =>
        GetBaseStatTier(statType) < MaxTier && AvailablePoints >= GetBaseStatUpgradeCost(statType);

    public static bool TryUpgradeBaseStat(StatType statType)
    {
        if (!CanUpgradeBaseStat(statType)) return false;
        int cost = GetBaseStatUpgradeCost(statType);
        PlayerPrefs.SetInt($"BaseStatTier_{statType}", GetBaseStatTier(statType) + 1);
        PlayerPrefs.SetInt(BoughtUpgradesKey, BoughtUpgrades + 1);
        PlayerPrefs.SetInt(SpentPointsKey, SpentPoints + cost);
        PlayerPrefs.Save();
        return true;
    }

    public static float GetStatMultiplier(StatType statType) => statType switch
    {
        StatType.WeaponDamage => 1f + GetStatTier(StatType.WeaponDamage) * 0.2f,
        StatType.MaxHealth    => 1f + GetStatTier(StatType.MaxHealth)    * 0.2f,
        StatType.MoveSpeed    => 1f + GetStatTier(StatType.MoveSpeed)    * 0.2f,
        StatType.AttackSpeed  => 1f + GetStatTier(StatType.AttackSpeed)  * 0.2f,
        StatType.HealthRegen  => 1f + GetStatTier(StatType.HealthRegen)  * 0.2f,
        StatType.EnergyRegen  => 1f + GetStatTier(StatType.EnergyRegen)  * 0.2f,
        StatType.DashCooldown => 1f + GetStatTier(StatType.DashCooldown) * 0.2f,
        StatType.CritChance   => 1f + GetStatTier(StatType.CritChance)   * 0.2f,
        StatType.WeaponRange  => 1f + GetStatTier(StatType.WeaponRange)  * 0.2f,
        StatType.Pierce       => 1f + GetStatTier(StatType.WeaponRange)  * 0.2f,
        _                     => 1f
    };

    public static int GetUpgradeCost(StatType statType)
    {
        int currentTier = GetStatTier(statType);
        if (currentTier >= MaxTier) return int.MaxValue;
        int basePart = LevelUpStatCost * (1 + currentTier);
        int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, BoughtUpgrades));
        return basePart + feePart;
    }

    public static bool CanUpgrade(StatType statType) =>
        GetStatTier(statType) < MaxTier && AvailablePoints >= GetUpgradeCost(statType);

    public static bool TryUpgrade(StatType statType)
    {
        if (!CanUpgrade(statType)) return false;

        int cost = GetUpgradeCost(statType);
        PlayerPrefs.SetInt($"StatTier_{statType}", GetStatTier(statType) + 1);
        PlayerPrefs.SetInt(BoughtUpgradesKey, BoughtUpgrades + 1);
        PlayerPrefs.SetInt(SpentPointsKey, SpentPoints + cost);
        PlayerPrefs.Save();
        return true;
    }

    public static void CommitPoints(int sessionPoints)
    {
        PlayerPrefs.SetInt(TotalPointsKey, TotalPoints + sessionPoints);
        PlayerPrefs.Save();
    }

    public static void SpendPoints(int amount)
    {
        if (amount > AvailablePoints) return;
        PlayerPrefs.SetInt(SpentPointsKey, SpentPoints + amount);
        PlayerPrefs.Save();
    }

    private static readonly StatType[] _upgradeableStats =
    {
        StatType.WeaponDamage, StatType.MaxHealth, StatType.MoveSpeed, StatType.AttackSpeed,
        StatType.HealthRegen,  StatType.EnergyRegen, StatType.DashCooldown,
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
        PlayerPrefs.SetInt(TotalPointsKey, TotalPoints + 1000);
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
