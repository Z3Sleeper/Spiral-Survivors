using TMPro;
using UnityEngine;

public class UpgradePanel : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI pointsText;

    [Header("Level-Up Stat Upgrades")]
    [SerializeField] private TextMeshProUGUI damageButtonText;
    [SerializeField] private TextMeshProUGUI maxHealthButtonText;
    [SerializeField] private TextMeshProUGUI moveSpeedButtonText;
    [SerializeField] private TextMeshProUGUI attackSpeedButtonText;
    [SerializeField] private TextMeshProUGUI healthRegenButtonText;
    [SerializeField] private TextMeshProUGUI energyRegenButtonText;
    [SerializeField] private TextMeshProUGUI dashHasteButtonText;
    [SerializeField] private TextMeshProUGUI critChanceButtonText;
    [SerializeField] private TextMeshProUGUI rangeButtonText;

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

    private void OnEnable() => Refresh();

    public void Refresh()
    {
        pointsText.text = $"{FormatPoints(ProgressionManager.AvailablePoints)} pts\n{FormatPoints(ProgressionManager.SpentPoints)} pts";

        RefreshLevelUpButtonText(StatType.WeaponDamage, "Damage",       damageButtonText);
        RefreshLevelUpButtonText(StatType.MaxHealth,    "Max Health",   maxHealthButtonText);
        RefreshLevelUpButtonText(StatType.MoveSpeed,    "Move Speed",   moveSpeedButtonText);
        RefreshLevelUpButtonText(StatType.AttackSpeed,  "Attack Speed", attackSpeedButtonText);
        RefreshLevelUpButtonText(StatType.HealthRegen,  "Health Regen", healthRegenButtonText);
        RefreshLevelUpButtonText(StatType.EnergyRegen,  "Energy Regen", energyRegenButtonText);
        RefreshLevelUpButtonText(StatType.DashHaste, "Dash Haste",   dashHasteButtonText);
        RefreshLevelUpButtonText(StatType.CritChance,   "Crit Chance",  critChanceButtonText);
        RefreshLevelUpButtonText(StatType.WeaponRange,  "Range", rangeButtonText);

        RefreshBaseButtonText(StatType.WeaponDamage, "Damage",       "",  damageFlatButtonText);
        RefreshBaseButtonText(StatType.MaxHealth,    "Max Health",   "",  maxHealthFlatButtonText);
        RefreshBaseButtonText(StatType.MoveSpeed,    "Move Speed",   "",  moveSpeedFlatButtonText);
        RefreshBaseButtonText(StatType.AttackSpeed,  "Attack Speed", "%", attackSpeedFlatButtonText);
        RefreshBaseButtonText(StatType.HealthRegen,  "Health Regen", "",  healthRegenFlatButtonText);
        RefreshBaseButtonText(StatType.EnergyRegen,  "Energy Regen", "",  energyRegenFlatButtonText);
        RefreshBaseButtonText(StatType.DashHaste, "Dash Haste",   "",  dashHasteFlatButtonText);
        RefreshBaseButtonText(StatType.CritChance,   "Crit Chance",  "%", critChanceFlatButtonText);
        RefreshBaseButtonText(StatType.WeaponRange,        "Range",    "",  rangeFlatButtonText);
        RefreshBaseButtonText(StatType.CollectionRadius,   "Collection Radius", "",  collectionRadiusFlatButtonText);
    }

    private void RefreshLevelUpButtonText(StatType statType, string label, TextMeshProUGUI buttonLabel)
    {
        if (buttonLabel == null) return;
        int tier = ProgressionManager.GetStatTier(statType);
        int maxTier = ProgressionManager.LevelUpStatMaxTier;
        int currentBonus = tier * 25;
        buttonLabel.text = tier >= maxTier
            ? $"({tier}/{maxTier}) {label}\n+25% (+{currentBonus}%)\nMAXED"
            : $"({tier}/{maxTier}) {label}\n+25% (+{currentBonus}%)\n{FormatPoints(ProgressionManager.GetUpgradeCost(statType))} pts";
    }

    private void RefreshBaseButtonText(StatType statType, string label, string unit, TextMeshProUGUI buttonLabel)
    {
        if (buttonLabel == null) return;
        int tier = ProgressionManager.GetBaseStatTier(statType);
        int maxTier = ProgressionManager.GetMaxBaseTier(statType);
        float perTier = ProgressionManager.GetBaseStatValuePerTier(statType);
        float total = perTier * tier;
        buttonLabel.text = tier >= maxTier
            ? $"({tier}/{maxTier}) {label}\n+{perTier}{unit} (+{total}{unit})\nMAXED"
            : $"({tier}/{maxTier}) {label}\n+{perTier}{unit} (+{total}{unit})\n{FormatPoints(ProgressionManager.GetBaseStatUpgradeCost(statType))} pts";
    }

    // Level-up stat buttons
    public void OnUpgradeDamageClicked()       { ProgressionManager.TryUpgrade(StatType.WeaponDamage); Refresh(); }
    public void OnUpgradeMaxHealthClicked()    { ProgressionManager.TryUpgrade(StatType.MaxHealth);    Refresh(); }
    public void OnUpgradeMoveSpeedClicked()    { ProgressionManager.TryUpgrade(StatType.MoveSpeed);    Refresh(); }
    public void OnUpgradeAttackSpeedClicked()  { ProgressionManager.TryUpgrade(StatType.AttackSpeed);  Refresh(); }
    public void OnUpgradeHealthRegenClicked()  { ProgressionManager.TryUpgrade(StatType.HealthRegen);  Refresh(); }
    public void OnUpgradeEnergyRegenClicked()  { ProgressionManager.TryUpgrade(StatType.EnergyRegen);  Refresh(); }
    public void OnUpgradeDashHasteClicked()    { ProgressionManager.TryUpgrade(StatType.DashHaste); Refresh(); }
    public void OnUpgradeCritChanceClicked()   { ProgressionManager.TryUpgrade(StatType.CritChance);   Refresh(); }
    public void OnUpgradeRangeClicked()        { ProgressionManager.TryUpgrade(StatType.WeaponRange);  Refresh(); }

    // Base stat buttons
    public void OnUpgradeDamageFlatClicked()       { ProgressionManager.TryUpgradeBaseStat(StatType.WeaponDamage); Refresh(); }
    public void OnUpgradeMaxHealthFlatClicked()    { ProgressionManager.TryUpgradeBaseStat(StatType.MaxHealth);    Refresh(); }
    public void OnUpgradeMoveSpeedFlatClicked()    { ProgressionManager.TryUpgradeBaseStat(StatType.MoveSpeed);    Refresh(); }
    public void OnUpgradeAttackSpeedFlatClicked()  { ProgressionManager.TryUpgradeBaseStat(StatType.AttackSpeed);  Refresh(); }
    public void OnUpgradeHealthRegenFlatClicked()  { ProgressionManager.TryUpgradeBaseStat(StatType.HealthRegen);  Refresh(); }
    public void OnUpgradeEnergyRegenFlatClicked()  { ProgressionManager.TryUpgradeBaseStat(StatType.EnergyRegen);  Refresh(); }
    public void OnUpgradeDashHasteFlatClicked()    { ProgressionManager.TryUpgradeBaseStat(StatType.DashHaste); Refresh(); }
    public void OnUpgradeCritChanceFlatClicked()   { ProgressionManager.TryUpgradeBaseStat(StatType.CritChance);   Refresh(); }
    public void OnUpgradeRangeFlatClicked()             { ProgressionManager.TryUpgradeBaseStat(StatType.WeaponRange);        Refresh(); }
    public void OnUpgradeCollectionRadiusFlatClicked()  { ProgressionManager.TryUpgradeBaseStat(StatType.CollectionRadius);  Refresh(); }

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
