using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

public static class ProgressionManager
{
    private const int    CurrentSaveVersion = 2;
    private const string SaveFileName       = "progression.json";

    private const int   BaseFee       = 50;
    private const float FeeMultiplier = 1.1f;
    private const long  PointsCap     = 1_000_000_000_000_000_000L;

    private static readonly int[] WeaponDamageCosts =
        { 0, 500, 1000 };
    private static readonly int[] DashChargesCosts =
        { 0, 250, 500, 750 };
    private static readonly int[] LuckCosts =
        { 0, 500, 1000, 2000, 2500 };
    private static readonly int[] EnergyRegenCosts =
        { 0, 500, 1000 };
    private static readonly int[] DashHasteCosts =
        { 0, 250, 500, 750 };
    private static readonly int[] CritChanceCosts =
        { 0, 250, 500, 750 };
    private static readonly int[] MaxShieldCosts =
        { 0, 1000, 2000 };
    private static readonly int[] HealthRegenCosts =
        { 0, 500, 1000, 1500 };
    private static readonly int[] MaxHealthCosts =
        { 500, 1000, 1500, 2000, 2500 };
    private static readonly int[] RevivePowerCosts =
        { 500, 750, 1000, 1250, 1500 };
    private static readonly int[] CollectionRadiusCosts =
        { 500, 1000, 1500, 2000, 2500 };
    private static readonly int[] MaxEnergyCosts =
        { 500, 1000, 1500, 2000, 2500 };
    private static readonly int[] WeaponRangeCosts =
        { 500, 1000 };
    private static readonly int[] ReviveTimePenaltyCosts =
        { 1000, 2000 };
    private static readonly int[] AttackSpeedCosts =
        { 1500, 3000 };
    private static readonly int[] ShieldRechargeTimeCosts =
        { 1500, 3000, 4500, 6000 };
    private static readonly int[] MoveSpeedCosts =
        { 2000, 4000 };
    private static readonly int[] LevelUpStatsCosts =
        { 4000, 8000, 12000, 16000, 20000, 24000, 28000, 32000, 36000, 40000 };
    private static readonly int[] XPMultiplierCosts =
        { 8000, 16000, 24000, 32000, 40000, 48000, 56000, 64000, 72000, 80000 };
    private static readonly int[] LevelUpOptionsCosts =
        { 10000, 20000 };
    private static readonly int[] DamagePerLevelCosts =
        { 100000, 150000, 200000, 250000, 300000 };
    private static readonly int[] WeaponDamagePercentCosts =
        { 250000, 500000, 750000, 1000000, 1250000 };

    private static string SavePath => Path.Combine(Application.persistentDataPath, SaveFileName);

    private class SaveData
    {
        public int                     saveVersion    = CurrentSaveVersion;
        public long                    totalPoints    = 0;
        public long                    spentPoints    = 0;
        public int                     boughtUpgrades = 0;
        public Dictionary<string, int> statTiers      = new();
    }

    private static SaveData _data;

    private static SaveData Data
    {
        get
        {
            if (_data == null) Load();
            return _data;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        CleanLegacyPlayerPrefs();
        Load();
        if (_data.saveVersion != CurrentSaveVersion)
        {
            long savedPoints = _data.totalPoints;
            var savedTiers  = _data.statTiers;
            _data = new SaveData { totalPoints = savedPoints };

            foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
            {
                string key = stat.ToString();
                if (!savedTiers.TryGetValue(key, out int savedTier) || savedTier <= 0) continue;

                int[] costs      = GetStatCosts(stat);
                int   targetTier = Mathf.Min(savedTier, costs.Length);

                for (int t = 0; t < targetTier; t++)
                {
                    int  feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, _data.boughtUpgrades));
                    long cost    = costs[t] + feePart;
                    if (_data.totalPoints - _data.spentPoints < cost) break;
                    _data.statTiers[key] = t + 1;
                    _data.boughtUpgrades++;
                    _data.spentPoints   += cost;
                }
            }

            Save();
        }
    }

    private static void CleanLegacyPlayerPrefs()
    {
        string[] legacyKeys =
        {
            "TotalPoints", "SpentPoints", "BoughtUpgrades", "SaveVersion", "LevelUpTier",
            "BaseStatTier_WeaponDamage", "BaseStatTier_MaxHealth",    "BaseStatTier_MoveSpeed",
            "BaseStatTier_AttackSpeed",  "BaseStatTier_HealthRegen",  "BaseStatTier_EnergyRegen",
            "BaseStatTier_DashHaste",    "BaseStatTier_CritChance",   "BaseStatTier_WeaponRange",
            "BaseStatTier_CollectionRadius", "BaseStatTier_MaxEnergy", "BaseStatTier_DashCharges",
            "BaseStatTier_RevivePower",  "BaseStatTier_ReviveTimePenalty", "BaseStatTier_XPMultiplier",
            "BaseStatTier_LevelUpOptions", "BaseStatTier_LevelUpStats", "BaseStatTier_Pierce"
        };
        bool anyFound = false;
        foreach (var key in legacyKeys)
            if (PlayerPrefs.HasKey(key)) { anyFound = true; break; }
        if (!anyFound) return;
        foreach (var key in legacyKeys)
            PlayerPrefs.DeleteKey(key);
        PlayerPrefs.Save();
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(SavePath))
            {
                _data = JsonConvert.DeserializeObject<SaveData>(File.ReadAllText(SavePath));
                if (_data != null) return;
            }
        }
        catch { }
        _data = new SaveData();
    }

    private static void Save()
    {
        try { File.WriteAllText(SavePath, JsonConvert.SerializeObject(_data, Formatting.Indented)); }
        catch (System.Exception e) { Debug.LogError($"[ProgressionManager] Failed to save: {e.Message}"); }
    }

    private static int[] GetStatCosts(StatType statType) => statType switch
    {
        StatType.MaxHealth         => MaxHealthCosts,
        StatType.WeaponDamage      => WeaponDamageCosts,
        StatType.MoveSpeed         => MoveSpeedCosts,
        StatType.AttackSpeed       => AttackSpeedCosts,
        StatType.HealthRegen       => HealthRegenCosts,
        StatType.EnergyRegen       => EnergyRegenCosts,
        StatType.DashHaste         => DashHasteCosts,
        StatType.CritChance        => CritChanceCosts,
        StatType.WeaponRange       => WeaponRangeCosts,
        StatType.CollectionRadius  => CollectionRadiusCosts,
        StatType.MaxEnergy         => MaxEnergyCosts,
        StatType.DashCharges       => DashChargesCosts,
        StatType.RevivePower       => RevivePowerCosts,
        StatType.ReviveTimePenalty => ReviveTimePenaltyCosts,
        StatType.XPMultiplier      => XPMultiplierCosts,
        StatType.LevelUpOptions    => LevelUpOptionsCosts,
        StatType.LevelUpStats       => LevelUpStatsCosts,
        StatType.WeaponDamagePercent => WeaponDamagePercentCosts,
        StatType.Luck                => LuckCosts,
        StatType.DamagePerLevel      => DamagePerLevelCosts,
        StatType.MaxShield           => MaxShieldCosts,
        StatType.ShieldRechargeTime  => ShieldRechargeTimeCosts,
        _                           => System.Array.Empty<int>()
    };

    public static int GetMaxBaseTier(StatType statType) => GetStatCosts(statType).Length;

    public static long TotalPoints     => Data.totalPoints;
    public static long SpentPoints     => Data.spentPoints;
    public static long AvailablePoints => Data.totalPoints - Data.spentPoints;
    public static int  BoughtUpgrades  => Data.boughtUpgrades;

    public static bool IsShopUnlocked => TotalPoints >= 50;

    public static bool IsStatUnlocked(StatType statType)
    {
        if (statType == StatType.WeaponDamage) return true;
        int[] costs = GetStatCosts(statType);
        return costs.Length > 0 && TotalPoints >= costs[0] * 5 + BaseFee;
    }

    public static int GetBaseStatTier(StatType statType) =>
        Data.statTiers.TryGetValue(statType.ToString(), out int tier) ? tier : 0;

    public static float GetBaseStatValuePerTier(StatType statType) => statType switch
    {
        StatType.WeaponDamage      => 5f,
        StatType.MaxHealth         => 10f,
        StatType.MoveSpeed         => 1f,
        StatType.AttackSpeed       => 15f,
        StatType.HealthRegen       => 0.5f,
        StatType.EnergyRegen       => 2f,
        StatType.DashHaste         => 10f,
        StatType.CritChance        => 5f,
        StatType.WeaponRange       => 1f,
        StatType.CollectionRadius  => 1f,
        StatType.MaxEnergy         => 10f,
        StatType.DashCharges       => 1f,
        StatType.RevivePower       => 0.1f,
        StatType.ReviveTimePenalty => -25f,
        StatType.XPMultiplier      => 10f,
        StatType.LevelUpOptions    => 1f,
        StatType.LevelUpStats       => 10f,
        StatType.WeaponDamagePercent => 0.1f,
        StatType.Luck                => 10f,
        StatType.DamagePerLevel      => 0.5f,
        StatType.MaxShield           => 0.05f,
        StatType.ShieldRechargeTime  => -0.5f,
        _                           => 0f
    };

    public static float GetBaseStatBonus(StatType statType) =>
        Mathf.Min(GetBaseStatTier(statType), GetMaxBaseTier(statType)) * GetBaseStatValuePerTier(statType);

    public static int GetBaseStatUpgradeCost(StatType statType)
    {
        int[] costs = GetStatCosts(statType);
        int   tier  = GetBaseStatTier(statType);
        if (tier >= costs.Length) return int.MaxValue;
        int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, BoughtUpgrades));
        return costs[tier] + feePart;
    }

    public static bool CanUpgradeBaseStat(StatType statType) =>
        GetBaseStatTier(statType) < GetMaxBaseTier(statType) && AvailablePoints >= GetBaseStatUpgradeCost(statType);

    public static bool TryUpgradeBaseStat(StatType statType)
    {
        if (!CanUpgradeBaseStat(statType)) return false;
        int cost = GetBaseStatUpgradeCost(statType);
        Data.statTiers[statType.ToString()] = GetBaseStatTier(statType) + 1;
        Data.boughtUpgrades++;
        Data.spentPoints += cost;
        Save();
        return true;
    }

    public static float GetStatMultiplier(StatType statType) =>
        1f + GetBaseStatTier(StatType.LevelUpStats) * 0.10f;

    public static long GetBuyAllCost(StatType statType)
    {
        int[] costs     = GetStatCosts(statType);
        int   tier      = GetBaseStatTier(statType);
        long  total     = 0;
        int   simBought = BoughtUpgrades;
        for (int t = tier; t < costs.Length; t++)
        {
            int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, simBought));
            total += costs[t] + feePart;
            simBought++;
        }
        return total;
    }

    public static long GetRefundAmount(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        if (tier == 0) return 0;
        int[] costs = GetStatCosts(statType);
        long refund = 0;
        int bought = BoughtUpgrades;
        for (int t = tier - 1; t >= 0; t--)
        {
            int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, Mathf.Max(0, bought - 1)));
            refund += costs[t] + feePart;
            bought--;
        }
        return refund;
    }

    public static void ResetBaseStat(StatType statType)
    {
        int tier = GetBaseStatTier(statType);
        if (tier == 0) return;
        int[] costs  = GetStatCosts(statType);
        long  refund = 0;
        int   bought = BoughtUpgrades;
        for (int t = tier - 1; t >= 0; t--)
        {
            int feePart = Mathf.RoundToInt(BaseFee * Mathf.Pow(FeeMultiplier, Mathf.Max(0, bought - 1)));
            refund += costs[t] + feePart;
            bought--;
        }
        Data.statTiers.Remove(statType.ToString());
        Data.boughtUpgrades = Mathf.Max(0, Data.boughtUpgrades - tier);
        Data.spentPoints    = System.Math.Max(0L, Data.spentPoints - refund);
        Save();
    }

    public static void CommitPoints(long sessionPoints)
    {
        bool shopWasUnlocked = IsShopUnlocked;
        var prevUnlocked = new HashSet<StatType>();
        foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
            if (IsStatUnlocked(stat)) prevUnlocked.Add(stat);

        Data.totalPoints = System.Math.Min(Data.totalPoints + sessionPoints, PointsCap);
        Save();

        if (!shopWasUnlocked && IsShopUnlocked)
            UnlockManager.Queue("Upgrade Shop");

        foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
            if (!prevUnlocked.Contains(stat) && IsStatUnlocked(stat))
                UnlockManager.Queue(GetStatDisplayName(stat) + " Upgrade Shop Item");
    }

    private static string GetStatDisplayName(StatType stat) => stat switch
    {
        StatType.MaxHealth         => "Max Health",
        StatType.WeaponDamage      => "Damage",
        StatType.MoveSpeed         => "Move Speed",
        StatType.AttackSpeed       => "Attack Speed",
        StatType.HealthRegen       => "Health Regen",
        StatType.EnergyRegen       => "Energy Regen",
        StatType.DashHaste         => "Dash Haste",
        StatType.CritChance        => "Crit Chance",
        StatType.WeaponRange       => "Range",
        StatType.CollectionRadius  => "Collection Radius",
        StatType.MaxEnergy         => "Max Energy",
        StatType.DashCharges       => "Dash Charges",
        StatType.RevivePower       => "Revive Power",
        StatType.ReviveTimePenalty => "Revive Time Penalty",
        StatType.XPMultiplier      => "XP Multiplier",
        StatType.LevelUpOptions    => "Level Up Options",
        StatType.LevelUpStats       => "Level Up Stats",
        StatType.WeaponDamagePercent => "% Damage",
        StatType.Luck                => "Luck",
        StatType.DamagePerLevel      => "Damage Per Level",
        StatType.MaxShield           => "Max Shield",
        StatType.ShieldRechargeTime  => "Shield Recharge Time",
        _                           => stat.ToString()
    };

    public static string FormatPoints(long value) => value switch
    {
        >= 1_000_000_000_000_000_000L => $"{value / 1_000_000_000_000_000_000f:F2}quint",
        >= 1_000_000_000_000_000L     => $"{value / 1_000_000_000_000_000f:F2}quad",
        >= 1_000_000_000_000L         => $"{value / 1_000_000_000_000f:F2}tril",
        >= 1_000_000_000L             => $"{value / 1_000_000_000f:F2}bil",
        >= 1_000_000L                 => $"{value / 1_000_000f:F2}mil",
        >= 1_000L                     => $"{value / 1_000f:F2}k",
        _                             => value.ToString()
    };

    public static void SpendPoints(int amount)
    {
        if (amount > AvailablePoints) return;
        Data.spentPoints += amount;
        Save();
    }

    public static void RefundPoints()
    {
        Data.spentPoints    = 0;
        Data.boughtUpgrades = 0;
        Data.statTiers.Clear();
        Save();
    }

    public static void AddDebugPoints()
    {
        Data.totalPoints = System.Math.Min(Data.totalPoints + 10_000L, PointsCap);
        Save();
    }

    public static void HardReset()
    {
        _data = new SaveData();
        Save();
    }
}
