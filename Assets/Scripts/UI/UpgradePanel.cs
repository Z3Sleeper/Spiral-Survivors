using TMPro;
using UnityEngine;

public class UpgradePanel : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI pointsText;

    [Header("Level-Up Upgrade")]
    [SerializeField] private TextMeshProUGUI levelUpStatsButtonText;

    [Header("Base Stat Upgrades")]
    [SerializeField] private TextMeshProUGUI damageFlatButtonText;
    [SerializeField] private TextMeshProUGUI maxHealthFlatButtonText;
    [SerializeField] private TextMeshProUGUI moveSpeedFlatButtonText;
    [SerializeField] private TextMeshProUGUI attackSpeedFlatButtonText;
    [SerializeField] private TextMeshProUGUI healthRegenFlatButtonText;
    [SerializeField] private TextMeshProUGUI energyRegenFlatButtonText;
    [SerializeField] private TextMeshProUGUI dashHasteFlatButtonText;
    [SerializeField] private TextMeshProUGUI critChanceFlatButtonText;
    [SerializeField] private TextMeshProUGUI rangeFlatButtonText;
    [SerializeField] private TextMeshProUGUI collectionRadiusFlatButtonText;
    [SerializeField] private TextMeshProUGUI maxEnergyFlatButtonText;
    [SerializeField] private TextMeshProUGUI dashChargesButtonText;
    [SerializeField] private TextMeshProUGUI revivePowerButtonText;
    [SerializeField] private TextMeshProUGUI reviveTimePenaltyButtonText;
    [SerializeField] private TextMeshProUGUI xpMultiplierButtonText;
    [SerializeField] private TextMeshProUGUI levelUpOptionsButtonText;

    private void OnEnable() => Refresh();

    public void Refresh()
    {
        pointsText.text = $"{FormatPoints(ProgressionManager.AvailablePoints)} pts\n{FormatPoints(ProgressionManager.SpentPoints)} pts";

        RefreshAllStatsButtonText();

        RefreshBaseButtonText(StatType.WeaponDamage,      "Damage",             "",  damageFlatButtonText);
        RefreshBaseButtonText(StatType.MaxHealth,          "Max Health",         "",  maxHealthFlatButtonText);
        RefreshBaseButtonText(StatType.MoveSpeed,          "Move Speed",         "",  moveSpeedFlatButtonText);
        RefreshBaseButtonText(StatType.AttackSpeed,        "Attack Speed",       "%", attackSpeedFlatButtonText);
        RefreshBaseButtonText(StatType.HealthRegen,        "Health Regen",       "",  healthRegenFlatButtonText);
        RefreshBaseButtonText(StatType.EnergyRegen,        "Energy Regen",       "",  energyRegenFlatButtonText);
        RefreshBaseButtonText(StatType.DashHaste,          "Dash Haste",         "",  dashHasteFlatButtonText);
        RefreshBaseButtonText(StatType.CritChance,         "Crit Chance",        "%", critChanceFlatButtonText);
        RefreshBaseButtonText(StatType.WeaponRange,        "Range",              "",  rangeFlatButtonText);
        RefreshBaseButtonText(StatType.CollectionRadius,   "Collection Radius",  "",  collectionRadiusFlatButtonText);
        RefreshBaseButtonText(StatType.MaxEnergy,          "Max Energy",         "",  maxEnergyFlatButtonText);
        RefreshBaseButtonText(StatType.DashCharges,        "Dash Charges",       "",   dashChargesButtonText);
        RefreshBaseButtonText(StatType.RevivePower,        "Revive Power",       "",   revivePowerButtonText);
        RefreshBaseButtonText(StatType.ReviveTimePenalty,  "Revive Time Penalty", "%",  reviveTimePenaltyButtonText);
        RefreshBaseButtonText(StatType.XPMultiplier,        "XP",                  "%",  xpMultiplierButtonText);
        RefreshBaseButtonText(StatType.LevelUpOptions,      "Level Up Options",    "",   levelUpOptionsButtonText);
    }

    private void RefreshAllStatsButtonText()
    {
        if (levelUpStatsButtonText == null) return;
        int tier    = ProgressionManager.GetLevelUpTier();
        int maxTier = ProgressionManager.LevelUpStatMaxTier;
        int current = tier * 10;
        levelUpStatsButtonText.text = tier >= maxTier
            ? $"({tier}/{maxTier}) Level Up Stats\n+10% (+{current}%)\nMAXED"
            : $"({tier}/{maxTier}) Level Up Stats\n+10% (+{current}%)\n{FormatPoints(ProgressionManager.GetUpgradeCost())} pts";
    }

    private void RefreshBaseButtonText(StatType statType, string label, string unit, TextMeshProUGUI buttonLabel)
    {
        if (buttonLabel == null) return;
        int tier      = ProgressionManager.GetBaseStatTier(statType);
        int maxTier   = ProgressionManager.GetMaxBaseTier(statType);
        float perTier = ProgressionManager.GetBaseStatValuePerTier(statType);
        float total   = perTier * tier;
        string sign   = perTier >= 0f ? "+" : "";
        buttonLabel.text = tier >= maxTier
            ? $"({tier}/{maxTier}) {label}\n{sign}{perTier}{unit} ({sign}{total}{unit})\nMAXED"
            : $"({tier}/{maxTier}) {label}\n{sign}{perTier}{unit} ({sign}{total}{unit})\n{FormatPoints(ProgressionManager.GetBaseStatUpgradeCost(statType))} pts";
    }

    public void OnLevelUpStatsClicked() { ProgressionManager.TryUpgrade(); Refresh(); }

    // Base stat buttons
    public void OnUpgradeDamageFlatClicked()              { ProgressionManager.TryUpgradeBaseStat(StatType.WeaponDamage);      Refresh(); }
    public void OnUpgradeMaxHealthFlatClicked()           { ProgressionManager.TryUpgradeBaseStat(StatType.MaxHealth);         Refresh(); }
    public void OnUpgradeMoveSpeedFlatClicked()           { ProgressionManager.TryUpgradeBaseStat(StatType.MoveSpeed);         Refresh(); }
    public void OnUpgradeAttackSpeedFlatClicked()         { ProgressionManager.TryUpgradeBaseStat(StatType.AttackSpeed);       Refresh(); }
    public void OnUpgradeHealthRegenFlatClicked()         { ProgressionManager.TryUpgradeBaseStat(StatType.HealthRegen);       Refresh(); }
    public void OnUpgradeEnergyRegenFlatClicked()         { ProgressionManager.TryUpgradeBaseStat(StatType.EnergyRegen);       Refresh(); }
    public void OnUpgradeDashHasteFlatClicked()           { ProgressionManager.TryUpgradeBaseStat(StatType.DashHaste);         Refresh(); }
    public void OnUpgradeCritChanceFlatClicked()          { ProgressionManager.TryUpgradeBaseStat(StatType.CritChance);        Refresh(); }
    public void OnUpgradeRangeFlatClicked()               { ProgressionManager.TryUpgradeBaseStat(StatType.WeaponRange);       Refresh(); }
    public void OnUpgradeCollectionRadiusFlatClicked()    { ProgressionManager.TryUpgradeBaseStat(StatType.CollectionRadius);  Refresh(); }
    public void OnUpgradeMaxEnergyFlatClicked()           { ProgressionManager.TryUpgradeBaseStat(StatType.MaxEnergy);          Refresh(); }
    public void OnUpgradeDashChargesClicked()             { ProgressionManager.TryUpgradeBaseStat(StatType.DashCharges);        Refresh(); }
    public void OnUpgradeRevivePowerClicked()             { ProgressionManager.TryUpgradeBaseStat(StatType.RevivePower);        Refresh(); }
    public void OnUpgradeReviveTimePenaltyClicked()       { ProgressionManager.TryUpgradeBaseStat(StatType.ReviveTimePenalty);  Refresh(); }
    public void OnUpgradeXPMultiplierClicked()            { ProgressionManager.TryUpgradeBaseStat(StatType.XPMultiplier);        Refresh(); }
    public void OnUpgradeLevelUpOptionsClicked()          { ProgressionManager.TryUpgradeBaseStat(StatType.LevelUpOptions);      Refresh(); }

    private static string FormatPoints(long value) => value switch
    {
        >= 1_000_000_000_000_000_000L => $"{value / 1_000_000_000_000_000_000f:F2}quint",
        >= 1_000_000_000_000_000L     => $"{value / 1_000_000_000_000_000f:F2}quad",
        >= 1_000_000_000_000L         => $"{value / 1_000_000_000_000f:F2}tril",
        >= 1_000_000_000L             => $"{value / 1_000_000_000f:F2}bil",
        >= 1_000_000L                 => $"{value / 1_000_000f:F2}mil",
        >= 1_000L                     => $"{value / 1_000f:F2}k",
        _                             => value.ToString()
    };

    public void OnRefundClicked()         { ProgressionManager.RefundPoints();    Refresh(); }
    public void OnHardResetClicked()      { ProgressionManager.HardReset();       Refresh(); }
    public void OnAddDebugPointsClicked() { ProgressionManager.AddDebugPoints();  Refresh(); }
}
