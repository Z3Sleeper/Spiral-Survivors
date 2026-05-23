using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class UIManager : MonoBehaviour
{
    public static UIManager instance;

    [SerializeField] private Slider healthBar;
    [SerializeField] private Slider shieldBar;
    [SerializeField] private Slider shieldRegenBar;
    [SerializeField] private TextMeshProUGUI healthText;
    [SerializeField] private TextMeshProUGUI healthRegenText;
    [SerializeField] private Image dashChargeCooldownImage;
    [SerializeField] private TextMeshProUGUI dashChargesText;
    [SerializeField] private Image energyImage;
    [SerializeField] private TextMeshProUGUI energyText;

    private CharacterStats _stats;
    private PlayerController _controller;

    private void Awake()
    {
        instance = this;
    }

    public void SetPlayer(CharacterStats stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerController>();
        healthBar.value = 1f;
        shieldBar.value = 1f;
        shieldRegenBar.value = 1f;
        dashChargeCooldownImage.fillAmount = 1f;
        energyImage.fillAmount = 1f;
    }

    private void Update()
    {
        if (_stats == null || _controller == null) return;

        // Health and Shields
        healthBar.value = _stats.CurrentHealth / _stats.MaxHealth;
        shieldBar.value = _stats.CurrentShield / _stats.MaxShield;
        shieldRegenBar.value = (_stats.ShieldRegenTime - _stats._shieldRechargeTimer) / _stats.ShieldRegenTime;
        healthText.text = $"<b><color=#FF00FF>(+{(int)_stats.CurrentShield})</color> {(int)_stats.CurrentHealth}/{(int)_stats.MaxHealth}</b>";

        // Health Regen
        healthRegenText.text = $"<b>+{_stats.HealthRegen}/s</b>";

        // Dash
        if (_controller._currentCharges < _stats.dashCharges)
            dashChargeCooldownImage.fillAmount = 1f - (_controller._chargeRegenTimer / _stats.dashCooldown);
        else
            dashChargeCooldownImage.fillAmount = 1f;
        dashChargesText.text = $"<b>{_controller._currentCharges}</b>";

        // Energy / Surge
        if (_stats.CurrentEnergy < _stats.MaxEnergy)
            energyImage.fillAmount = _stats.CurrentEnergy / _stats.MaxEnergy;
        else
            energyImage.fillAmount = 1f;
        if (_stats.CurrentEnergy < _stats.surgeActivationThreshhold)
            energyImage.color = Color.red;
        else
            energyImage.color = Color.yellow;
        energyText.text = $"<b>{(int)_stats.CurrentEnergy}</b>";
    }
}