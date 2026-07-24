using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UpgradePanel : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI pointsText;
    [SerializeField] private ScrollRect upgradeScrollRect;

    [Header("Upgrade Buttons")]
    [SerializeField] private TextMeshProUGUI levelUpStatsButtonText;
    [SerializeField] private TextMeshProUGUI strengthFlatButtonText;
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
    [SerializeField] private TextMeshProUGUI strengthPercentButtonText;
    [SerializeField] private TextMeshProUGUI luckButtonText;
    [SerializeField] private TextMeshProUGUI strengthPerLevelButtonText;
    [SerializeField] private TextMeshProUGUI maxShieldButtonText;
    [SerializeField] private TextMeshProUGUI shieldRechargeTimeButtonText;

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
            Select(StatType.Strength);
        Refresh();
        if (upgradeScrollRect != null)
            upgradeScrollRect.verticalNormalizedPosition = 1f;
    }

    public void Refresh()
    {
        pointsText.text = $"{FormatPoints(ProgressionManager.AvailablePoints)} pts\n{FormatPoints(ProgressionManager.SpentPoints)} pts";

        RefreshButtonText(StatType.LevelUpStats,      "Level Up Stats",      levelUpStatsButtonText);
        RefreshButtonText(StatType.Strength,           "Strength",              strengthFlatButtonText);
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
        RefreshButtonText(StatType.StrengthPercent,     "% Strength",            strengthPercentButtonText);
        RefreshButtonText(StatType.Luck,                "Luck",                luckButtonText);
        RefreshButtonText(StatType.StrengthPerLevel,    "Strength Per Level",       strengthPerLevelButtonText);
        RefreshButtonText(StatType.MaxShield,            "Max Shield",             maxShieldButtonText);
        RefreshButtonText(StatType.ShieldRechargeTime,   "Shield Recharge",        shieldRechargeTimeButtonText);

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

        string color = tier >= maxTier                                    ? "#007700"
                     : ProgressionManager.CanUpgradeBaseStat(statType) ? "#000000"
                     : "#AA6600";

        buttonLabel.text = $"<color={color}>{label}\n({tier}/{maxTier})</color>";
    }

    private static string GetUnit(StatType statType) => statType switch
    {
        StatType.AttackSpeed       => "%",
        StatType.CritChance        => "%",
        StatType.MaxShield         => "%",
        StatType.ReviveTimePenalty  => "%",
        StatType.ShieldRechargeTime => "s",
        StatType.XPMultiplier      => "%",
        StatType.LevelUpStats        => "%",
        StatType.StrengthPercent     => "%",
        StatType.RevivePower       => "%",
        _                          => ""
    };

    private static string GetDescription(StatType stat)
    {
        int tier      = ProgressionManager.GetBaseStatTier(stat);
        int maxTier   = ProgressionManager.GetMaxBaseTier(stat);
        float perTier = ProgressionManager.GetBaseStatValuePerTier(stat);
        float displayScale = (stat == StatType.RevivePower || stat == StatType.StrengthPercent || stat == StatType.MaxShield) ? 100f : 1f;
        float current = Mathf.Abs(perTier * tier)   * displayScale;
        float max     = Mathf.Abs(perTier * maxTier) * displayScale;
        float absPer  = Mathf.Abs(perTier)           * displayScale;
        string unit   = GetUnit(stat);
        bool reduces  = perTier < 0f;
        string sign   = reduces ? "-" : "+";

        return stat switch
        {
            StatType.LevelUpStats      => $"Increases stat gains from level up rewards by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.Strength          => $"Increases your Strength by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nWeapon Strikes randomly deal between 80% to 120% of your Strength, influenced by Luck.",
            StatType.MaxHealth         => $"Increases your Max Health by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.MoveSpeed         => $"Increases your Move Speed by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.AttackSpeed       => $"Increases your Weapon Attack Speed by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nAttack Speed is equivalent to revolutions per second.",
            StatType.HealthRegen       => $"Increases your Health Regen per second by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.EnergyRegen       => $"Increases your Energy Regen per second by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nEnergy is used to activate Surge, reducing your Move Speed by 50% to double your Attack Speed.",
            StatType.DashHaste         => $"Increases your Dash Haste by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nEach 1 Dash Haste speeds up your Dash Charge cooldown by 1%.\nYou are invulnerable while Dashing.",
            StatType.CritChance        => $"Increases the chance for your Weapon Strikes to Critically Strike by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nEach 1% Crit Chance over 100% is converted into 0.5% Crit Damage.",
            StatType.WeaponRange       => $"Increases your Weapon Range by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.CollectionRadius  => $"Increases the distance you collect XP Orbs by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.MaxEnergy         => $"Increases your Max Energy by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.DashCharges       => $"Increases your maximum Dash Charges by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.RevivePower       => $"(Multiplayer Only) Increases the speed you are revived by and revive allies by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.ReviveTimePenalty => $"(Multiplayer Only) Reduces your Revive Time Penalty per death by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.XPMultiplier      => $"Increases your XP Multiplier by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).\nTeam XP multiplier is the additively combined XP multiplier of your party divided by the number of players.",
            StatType.LevelUpOptions      => $"Increases your level up reward options by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.StrengthPercent     => $"Increases your Strength by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
            StatType.Luck                => $"Increases your Luck by {absPer} per tier ({current}/{max}).\nEach 1 Luck tightens variance towards the maximum roll by 1%.",
            StatType.StrengthPerLevel    => $"Increases your Strength per Level by {absPer} per tier ({current}/{max}).",
            StatType.MaxShield           => $"Increases your Max Shield by {absPer}{unit} of your Max Health ({sign}{current}{unit}/{sign}{max}{unit}).\nShield takes 5 seconds to fully recharge, reduced by Shield Recharge Time Reductions.",
            StatType.ShieldRechargeTime  => $"Reduces your Shield Recharge Time by {absPer}{unit} per tier ({sign}{current}{unit}/{sign}{max}{unit}).",
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
                ? "0\npts"
                : $"{FormatPoints(ProgressionManager.GetRefundAmount(stat))}\npts";

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
    public void OnUpgradeStrengthFlatClicked()           { Select(StatType.Strength); }
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
    public void OnUpgradeStrengthPercentClicked()      { Select(StatType.StrengthPercent); }
    public void OnUpgradeLuckClicked()                 { Select(StatType.Luck); }
    public void OnUpgradeStrengthPerLevelClicked()     { Select(StatType.StrengthPerLevel); }
    public void OnUpgradeMaxShieldClicked()            { Select(StatType.MaxShield); }
    public void OnUpgradeShieldRechargeTimeClicked()   { Select(StatType.ShieldRechargeTime); }

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

    private static string FormatPoints(long value) => ProgressionManager.FormatPoints(value);

    public void OnRefundClicked()         { ProgressionManager.RefundPoints();   Refresh(); }
    public void OnHardResetClicked()      { ProgressionManager.HardReset();      Refresh(); }
    public void OnAddDebugPointsClicked() { ProgressionManager.AddDebugPoints(); Refresh(); }
}
