using PurrNet;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerUIGlobal : NetworkBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private Slider shieldBar;
    [SerializeField] private TextMeshProUGUI healthText;
    private CharacterStats _stats;
    private Camera _camera;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _stats = GetComponentInParent<CharacterStats>();
        _camera = Camera.main;

        // Hide for the owner, they have their own UI
        if (isOwner)
            gameObject.SetActive(false);
        if (!isOwner)
            gameObject.SetActive(true);
    }

    private void Update()
    {
        if (_stats == null) return;
        healthBar.value = _stats.currentHealth / _stats.maxHealth;
        shieldBar.value = _stats.currentShield / _stats.maxShield;
        healthText.text = $"<b>(+{_stats.currentShield}){_stats.currentHealth}/{_stats.maxHealth}</b>";
    }

    private void LateUpdate()
    {
        if (_camera == null)
            _camera = Camera.main;
        if (_camera == null) return;
        transform.forward = _camera.transform.forward;
    }
}