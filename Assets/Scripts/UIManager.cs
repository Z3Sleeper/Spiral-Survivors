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

    private CharacterStats _stats;

    private void Awake()
    {
        instance = this;
    }

    public void SetPlayer(CharacterStats stats)
    {
        _stats = stats;
        healthBar.value = 1f;
        shieldBar.value = 1f;
        shieldRegenBar.value = 1f;
    }

    private void Update()
    {
        if (_stats == null) return;

        healthBar.value = _stats.currentHealth / _stats.maxHealth;
        shieldBar.value = _stats.currentShield / _stats.maxShield;
        shieldRegenBar.value = (_stats.shieldRegenTime - _stats._shieldRegenTimer) / _stats.shieldRegenTime;

        healthText.text = $"<b>(+{_stats.currentShield}){_stats.currentHealth}/{_stats.maxHealth}</b>";
    }
}