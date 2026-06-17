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
        healthBar.value = _stats.CurrentHealth / _stats.MaxHealth;
        shieldBar.value = _stats.CurrentShield / _stats.MaxShield;
        if (_stats.CurrentShield > 0)
            healthText.text = $"<b><color=#FF00FF>(+{(int)_stats.CurrentShield})</color> {(int)_stats.CurrentHealth}/{(int)_stats.MaxHealth}</b>";
        else
            healthText.text = $"{(int)_stats.CurrentHealth}/{(int)_stats.MaxHealth}</b>";
    }

    private void LateUpdate()
    {
        if (_camera == null)
            _camera = Camera.main;
        if (_camera == null) return;
        transform.rotation = _camera.transform.rotation;
    }
}