using PurrNet;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerUI : NetworkBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private Slider shieldBar;
    [SerializeField] private Slider shieldRegenBar;
    [SerializeField] private TextMeshProUGUI healthText;
    private CharacterStats _stats;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
    
        GetComponentInChildren<Canvas>().gameObject.SetActive(isOwner);

        if (!isOwner) return;

        _stats = GetComponent<CharacterStats>();

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

        healthText.text = $"{_stats.currentHealth}(+{_stats.currentShield})/{_stats.maxHealth}";
    }
}