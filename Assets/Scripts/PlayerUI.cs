using PurrNet;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerUI : NetworkBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private TextMeshProUGUI healthText;
    private CharacterStats _stats;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
    
        GetComponentInChildren<Canvas>().gameObject.SetActive(isOwner);

        if (!isOwner) return;

        _stats = GetComponent<CharacterStats>();

        healthBar.value = 1f; // start full
    }

    private void Update()
    {
        if (_stats == null) return;
        
        healthBar.value = _stats.currentHealth / _stats.maxHealth;
        healthText.text = $"{_stats.currentHealth}/{_stats.maxHealth}";
    }
}