using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UpgradePanel : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI pointsText;

    [Header("Upgrade Buttons")]
    [SerializeField] private TextMeshProUGUI levelUpStatsButtonText;
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
    [SerializeField] private TextMeshProUGUI weaponDamagePercentButtonText;
    [SerializeField] private TextMeshProUGUI luckButtonText;
    [SerializeField] private TextMeshProUGUI damagePerLevelButtonText;

    [Header("Selection Bar")]
    [SerializeField] private Image selectedIcon;
    [SerializeField] private TextMeshProUGUI refundText;
    [SerializeField] private TextMeshProUGUI selectedDescriptionText;
    [SerializeField] private TextMeshProUGUI currentPriceText;
    [SerializeField] private TextMeshProUGUI buyAllPriceText;

    [System.Serializable]
    private struct UpgradeVisuals
    {
        public StatType statType;
        public Sprite icon;
    }
    [SerializeField] private List<UpgradeVisuals> upgradeVisuals;

    [System.Serializable]
    private struct UpgradeButtonEntry { public StatType statType; public GameObject buttonObject; }
    [SerializeField] private List<UpgradeButtonEntry> upgradeButtons;

    private readonly Dictionary<StatType, UpgradeVisuals> _visualsMap = new();
    private StatType? _selectedStat;

    private void Awake()
    {
        foreach (var v in upgradeVisuals)
            _visualsMap[v.statType] = v;
    }

    private void OnEnable()
    {
        if (_selectedStat == null)
            Select(StatType.WeaponDamage);
        Refresh();
    }

    public void Refresh()
    {
        pointsText.text = $"{FormatPoints(ProgressionManager.AvailablePoints)} pts\n{FormatPoints(ProgressionManager.SpentPoints)} pts";

        RefreshButtonText(StatType.LevelUpStats,      "Level Up Stats",      levelUpStatsButtonText);
        RefreshButtonText(StatType.WeaponDamage,      "Damage",              damageFlatButtonText);
        RefreshButtonText(StatType.MaxHealth,          "Max Health",          maxHealthFlatButtonText);
        RefreshButtonText(StatType.MoveSpeed,          "Move Speed",          moveSpeedFlatButtonText);
        RefreshButtonText(StatType.AttackSpeed,        "Attack Speed",        attackSpeedFlatButtonText);
        RefreshButtonText(StatType.HealthRegen,        "Health Regen",        healthRegenFlatButtonText);
        RefreshButtonText(StatType.EnergyRegen,        "Energy Regen",        energyRegenFlatButtonText);
        RefreshButtonText(StatType.DashHaste,          "Dash Haste",          dashHasteFlatButtonText);
        RefreshButtonText(StatType.CritChance,         "Crit Chance",         critChanceFlatButtonText);
        RefreshButtonText(StatType.WeaponRange,        "Range",               rangeFlatButtonText);
        RefreshButtonText(StatType.CollectionRadius,   "Collection Radius",   collectionRadiusFlatButtonText);
        RefreshButtonText(StatType.MaxEnergy,          "Max Energy",          maxEnergyFlatButtonText);
        RefreshButtonText(StatType.DashCharges,        "Dash Charges",        dashChargesButtonText);
        RefreshButtonText(StatType.RevivePower,        "Revive Power",        revivePowerButtonText);
        RefreshButtonText(StatType.ReviveTimePenalty,  "Revive Time Penalty", reviveTimePenaltyButtonText);
        RefreshButtonText(StatType.XPMultiplier,       "XP",                  xpMultiplierButtonText);
        RefreshButtonText(StatType.LevelUpOptions,      "Level Up Options",    levelUpOptionsButtonText);
        RefreshButtonText(StatType.WeaponDamagePercent, "% Damage",            weaponDamagePercentButtonText);
        RefreshButtonText(StatType.Luck,                "Luck",                luckButtonText);
        RefreshButtonText(StatType.DamagePerLevel,      "Damage Per Level",       damagePerLevelButtonText);

        foreach (var entry in upgradeButtons)
            if (entry.buttonObject != null)
                entry.buttonObject.SetActive(ProgressionManager.IsStatUnlocked(entry.statType));

        RefreshSelectionBar();
    }

    private static void RefreshButtonText(StatType statType, string label, TextMeshProUGUI buttonLabel)
    {
        if (buttonLabel == null) return;
        int tier    = ProgressionManager.GetBaseStatTier(statType);
        int maxTier = ProgressionManager.GetMaxBaseTier(statType);
        buttonLabel.text = $"{label}\n({tier}/{maxTier})";
    }

    private static string GetUnit(StatType statType) => statType switch
    {
        StatType.AttackSpeed       => "%",
        StatType.CritChance        => "%",
        StatType.ReviveTimePenalty => "%",
        StatType.XPMultiplier      => "%",
        StatType.LevelUpStats        => "%",
        StatType.WeaponDamagePercent => "%",
        StatType.RevivePower       => "%",
        _                          => ""
    };

    private static string GetDescription(StatType stat)
    {
        int tier      = ProgressionManager.GetBaseStatTier(stat);
        int maxTier   = ProgressionManager.GetMaxBaseTier(stat);
        float perTier = ProgressionManager.GetBaseStatValuePerTier(stat);
        float displayScale = (stat == StatType.RevivePower || stat == StatType.WeaponDamagePercent) ? 100f : 1f;
        float current = Mathf.Abs(perTier * tier)   * displayScale;
        float max     = Mathf.Abs(perTier * maxTier) * displayScale;
        float absPer  = Mathf.Abs(perTier)           * displayScale;
        string unit   = GetUnit(stat);
        bool reduces  = perTier < 0f;
        string sign   = reduces ? "-" : "+";

        return stat switch
        {
            StatType.LevelUpStats      => $"Increases stat gains from level up rewards by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.WeaponDamage      => $"Increases your Damage by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nWeapon Strikes randomly deal between 80% to 120% of your Damage.",
            StatType.MaxHealth         => $"Increases your Max Health by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.MoveSpeed         => $"Increases your Move Speed by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.AttackSpeed       => $"Increases your Weapon Attack Speed by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nAttack Speed is equivalent to revolutions per second.",
            StatType.HealthRegen       => $"Increases your Health Regen per second by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.EnergyRegen       => $"Increases your Energy Regen per second by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nEnergy is used to activate Surge, reducing your Move Speed by 50% to double your Attack Speed.",
            StatType.DashHaste         => $"Increases your Dash Haste by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nEach 1 Dash Haste speeds up your Dash Charge cooldown by 1%.",
            StatType.CritChance        => $"Increases the chance for your Weapon Strikes to Critically Strike by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.WeaponRange       => $"Increases your Weapon Range by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.CollectionRadius  => $"Increases the distance you collect XP Orbs by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.MaxEnergy         => $"Increases your Max Energy by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.DashCharges       => $"Increases your maximum Dash Charges by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.RevivePower       => $"(Multiplayer Only) Increases the speed you are revived by and revive allies by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.ReviveTimePenalty => $"(Multiplayer Only) Reduces your Revive Time Penalty per death by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.XPMultiplier      => $"Increases your XP Multiplier by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nTeam XP multiplier is the additively combined XP multiplier of your party divided by the number of players.",
            StatType.LevelUpOptions      => $"Increases your level up reward options by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.WeaponDamagePercent => $"Increases your Damage by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.Luck                => $"Increases your Luck by {absPer} per tier ({current}/{max}).\nEach 1 Luck tightens variance towards the maximum roll by 1%.",
            StatType.DamagePerLevel      => $"Increases your Damage per Level by {absPer} per tier ({current}/{max}).",
            _                            => string.Empty
        };
    }

    private void Select(StatType stat)
    {
        _selectedStat = stat;
        RefreshSelectionBar();
    }

    private void RefreshSelectionBar()
    {
        if (_selectedStat == null) return;
        StatType stat = _selectedStat.Value;

        if (_visualsMap.TryGetValue(stat, out var v) && selectedIcon != null)
            selectedIcon.sprite = v.icon;

        if (selectedDescriptionText != null)
            selectedDescriptionText.text = GetDescription(stat);

        int tier = ProgressionManager.GetBaseStatTier(stat);
        int maxTier = ProgressionManager.GetMaxBaseTier(stat);
        bool maxed = tier >= maxTier;

        if (refundText != null)
            refundText.text = tier == 0
                ? "Refund:\n0 pts"
                : $"Refund:\n{FormatPoints(ProgressionManager.GetRefundAmount(stat))} pts";

        if (currentPriceText != null)
            currentPriceText.text = maxed
                ? "MAXED"
                : $"{FormatPoints(ProgressionManager.GetBaseStatUpgradeCost(stat))}\npts";

        if (buyAllPriceText != null)
            buyAllPriceText.text = maxed
                ? "MAXED"
                : $"{FormatPoints(ProgressionManager.GetBuyAllCost(stat))}\npts";
    }

    // All buttons — click to SELECT
    public void OnLevelUpStatsClicked()                { Select(StatType.LevelUpStats); }
    public void OnUpgradeDamageFlatClicked()           { Select(StatType.WeaponDamage); }
    public void OnUpgradeMaxHealthFlatClicked()        { Select(StatType.MaxHealth); }
    public void OnUpgradeMoveSpeedFlatClicked()        { Select(StatType.MoveSpeed); }
    public void OnUpgradeAttackSpeedFlatClicked()      { Select(StatType.AttackSpeed); }
    public void OnUpgradeHealthRegenFlatClicked()      { Select(StatType.HealthRegen); }
    public void OnUpgradeEnergyRegenFlatClicked()      { Select(StatType.EnergyRegen); }
    public void OnUpgradeDashHasteFlatClicked()        { Select(StatType.DashHaste); }
    public void OnUpgradeCritChanceFlatClicked()       { Select(StatType.CritChance); }
    public void OnUpgradeRangeFlatClicked()            { Select(StatType.WeaponRange); }
    public void OnUpgradeCollectionRadiusFlatClicked() { Select(StatType.CollectionRadius); }
    public void OnUpgradeMaxEnergyFlatClicked()        { Select(StatType.MaxEnergy); }
    public void OnUpgradeDashChargesClicked()          { Select(StatType.DashCharges); }
    public void OnUpgradeRevivePowerClicked()          { Select(StatType.RevivePower); }
    public void OnUpgradeReviveTimePenaltyClicked()    { Select(StatType.ReviveTimePenalty); }
    public void OnUpgradeXPMultiplierClicked()         { Select(StatType.XPMultiplier); }
    public void OnUpgradeLevelUpOptionsClicked()        { Select(StatType.LevelUpOptions); }
    public void OnUpgradeWeaponDamagePercentClicked()  { Select(StatType.WeaponDamagePercent); }
    public void OnUpgradeLuckClicked()                 { Select(StatType.Luck); }
    public void OnUpgradeDamagePerLevelClicked()       { Select(StatType.DamagePerLevel); }

    // Selection bar action buttons
    public void OnBuyOneClicked()
    {
        if (_selectedStat.HasValue)
            ProgressionManager.TryUpgradeBaseStat(_selectedStat.Value);
        Refresh();
    }

    public void OnBuyAllClicked()
    {
        if (_selectedStat.HasValue)
            while (ProgressionManager.CanUpgradeBaseStat(_selectedStat.Value))
                ProgressionManager.TryUpgradeBaseStat(_selectedStat.Value);
        Refresh();
    }

    public void OnResetUpgradeClicked()
    {
        if (_selectedStat.HasValue)
            ProgressionManager.ResetBaseStat(_selectedStat.Value);
        Refresh();
    }

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

    public void OnRefundClicked()         { ProgressionManager.RefundPoints();   Refresh(); }
    public void OnHardResetClicked()      { ProgressionManager.HardReset();      Refresh(); }
    public void OnAddDebugPointsClicked() { ProgressionManager.AddDebugPoints(); Refresh(); }
}
