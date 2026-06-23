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

    private void OnEnable() => Refresh();

    public void Refresh()
    {
        pointsText.text = $"{ProgressionManager.AvailablePoints} pts\n{ProgressionManager.SpentPoints} pts";

        RefreshLevelUpButtonText(StatType.WeaponDamage, "Damage",       damageButtonText);
        RefreshLevelUpButtonText(StatType.MaxHealth,    "Max Health",   maxHealthButtonText);
        RefreshLevelUpButtonText(StatType.MoveSpeed,    "Move Speed",   moveSpeedButtonText);
        RefreshLevelUpButtonText(StatType.AttackSpeed,  "Attack Speed", attackSpeedButtonText);
        RefreshLevelUpButtonText(StatType.HealthRegen,  "Health Regen", healthRegenButtonText);
        RefreshLevelUpButtonText(StatType.EnergyRegen,  "Energy Regen", energyRegenButtonText);
        RefreshLevelUpButtonText(StatType.DashCooldown, "Dash Haste",   dashHasteButtonText);
        RefreshLevelUpButtonText(StatType.CritChance,   "Crit Chance",  critChanceButtonText);
        RefreshLevelUpButtonText(StatType.WeaponRange,  "Range & Pierce", rangeButtonText);

        RefreshBaseButtonText(StatType.WeaponDamage, "Damage",       "",  damageFlatButtonText);
        RefreshBaseButtonText(StatType.MaxHealth,    "Max Health",   "",  maxHealthFlatButtonText);
        RefreshBaseButtonText(StatType.MoveSpeed,    "Move Speed",   "",  moveSpeedFlatButtonText);
        RefreshBaseButtonText(StatType.AttackSpeed,  "Attack Speed", "%", attackSpeedFlatButtonText);
        RefreshBaseButtonText(StatType.HealthRegen,  "Health Regen", "",  healthRegenFlatButtonText);
        RefreshBaseButtonText(StatType.EnergyRegen,  "Energy Regen", "",  energyRegenFlatButtonText);
        RefreshBaseButtonText(StatType.DashCooldown, "Dash Haste",   "",  dashHasteFlatButtonText);
        RefreshBaseButtonText(StatType.CritChance,   "Crit Chance",  "%", critChanceFlatButtonText);
        RefreshBaseButtonText(StatType.WeaponRange,  "Range & Pierce", "",  rangeFlatButtonText);
    }

    private void RefreshLevelUpButtonText(StatType statType, string label, TextMeshProUGUI buttonLabel)
    {
        if (buttonLabel == null) return;
        int tier = ProgressionManager.GetStatTier(statType);
        int currentBonus = tier * 20;
        buttonLabel.text = tier >= 5
            ? $"{label}\n+20% (+{currentBonus}%) | MAXED"
            : $"{label}\n+20% (+{currentBonus}%) | {ProgressionManager.GetUpgradeCost(statType)} pts";
    }

    private void RefreshBaseButtonText(StatType statType, string label, string unit, TextMeshProUGUI buttonLabel)
    {
        if (buttonLabel == null) return;
        int tier = ProgressionManager.GetBaseStatTier(statType);
        float perTier = ProgressionManager.GetBaseStatValuePerTier(statType);
        float total = perTier * tier;
        buttonLabel.text = tier >= 5
            ? $"{label}\n+{perTier}{unit} (+{total}{unit}) | MAXED"
            : $"{label}\n+{perTier}{unit} (+{total}{unit}) | {ProgressionManager.GetBaseStatUpgradeCost(statType)} pts";
    }

    // Level-up stat buttons
    public void OnUpgradeDamageClicked()       { ProgressionManager.TryUpgrade(StatType.WeaponDamage); Refresh(); }
    public void OnUpgradeMaxHealthClicked()    { ProgressionManager.TryUpgrade(StatType.MaxHealth);    Refresh(); }
    public void OnUpgradeMoveSpeedClicked()    { ProgressionManager.TryUpgrade(StatType.MoveSpeed);    Refresh(); }
    public void OnUpgradeAttackSpeedClicked()  { ProgressionManager.TryUpgrade(StatType.AttackSpeed);  Refresh(); }
    public void OnUpgradeHealthRegenClicked()  { ProgressionManager.TryUpgrade(StatType.HealthRegen);  Refresh(); }
    public void OnUpgradeEnergyRegenClicked()  { ProgressionManager.TryUpgrade(StatType.EnergyRegen);  Refresh(); }
    public void OnUpgradeDashHasteClicked()    { ProgressionManager.TryUpgrade(StatType.DashCooldown); Refresh(); }
    public void OnUpgradeCritChanceClicked()   { ProgressionManager.TryUpgrade(StatType.CritChance);   Refresh(); }
    public void OnUpgradeRangeClicked()        { ProgressionManager.TryUpgrade(StatType.WeaponRange);  Refresh(); }

    // Base stat buttons
    public void OnUpgradeDamageFlatClicked()       { ProgressionManager.TryUpgradeBaseStat(StatType.WeaponDamage); Refresh(); }
    public void OnUpgradeMaxHealthFlatClicked()    { ProgressionManager.TryUpgradeBaseStat(StatType.MaxHealth);    Refresh(); }
    public void OnUpgradeMoveSpeedFlatClicked()    { ProgressionManager.TryUpgradeBaseStat(StatType.MoveSpeed);    Refresh(); }
    public void OnUpgradeAttackSpeedFlatClicked()  { ProgressionManager.TryUpgradeBaseStat(StatType.AttackSpeed);  Refresh(); }
    public void OnUpgradeHealthRegenFlatClicked()  { ProgressionManager.TryUpgradeBaseStat(StatType.HealthRegen);  Refresh(); }
    public void OnUpgradeEnergyRegenFlatClicked()  { ProgressionManager.TryUpgradeBaseStat(StatType.EnergyRegen);  Refresh(); }
    public void OnUpgradeDashHasteFlatClicked()    { ProgressionManager.TryUpgradeBaseStat(StatType.DashCooldown); Refresh(); }
    public void OnUpgradeCritChanceFlatClicked()   { ProgressionManager.TryUpgradeBaseStat(StatType.CritChance);   Refresh(); }
    public void OnUpgradeRangeFlatClicked()        { ProgressionManager.TryUpgradeBaseStat(StatType.WeaponRange);  Refresh(); }

    public void OnRefundClicked()         { ProgressionManager.RefundPoints();    Refresh(); }
    public void OnHardResetClicked()      { ProgressionManager.HardReset();       Refresh(); }
    public void OnAddDebugPointsClicked() { ProgressionManager.AddDebugPoints();  Refresh(); }
}
