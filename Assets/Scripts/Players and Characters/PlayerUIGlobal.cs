using PurrNet;
using UnityEngine;
using UnityEngine.UI;

public class PlayerUIGlobal : NetworkBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private Slider shieldBar;
    [SerializeField] private GameObject dashUI;
    [SerializeField] private Image dashChargeCooldownImage;
    [SerializeField] private GameObject[] dashNotches;
    [SerializeField] private GameObject energyUI;
    [SerializeField] private Image energyImage;

    private CharacterStats _stats;
    private PlayerController _controller;
    private Camera _camera;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _stats = GetComponentInParent<CharacterStats>();
        _controller = GetComponentInParent<PlayerController>();
        _camera = Camera.main;

        if (dashUI != null) dashUI.SetActive(isOwner);
        if (energyUI != null) energyUI.SetActive(isOwner);
    }

    private void Update()
    {
        if (_stats == null) return;
        healthBar.value = _stats.CurrentHealth / _stats.MaxHealth;
        shieldBar.value = _stats.CurrentShield / _stats.MaxShield;
        if (!isOwner) return;
        UpdateDash();
        UpdateEnergy();
    }

    private void UpdateDash()
    {
        if (_controller == null) return;
        if (dashChargeCooldownImage != null)
            dashChargeCooldownImage.fillAmount = _controller._currentCharges < _stats.dashCharges
                ? 1f - (_controller._chargeRegenTimer / _stats.DashCooldown)
                : 1f;
        for (int i = 0; i < dashNotches.Length; i++)
            dashNotches[i].SetActive(i < _controller._currentCharges);
    }

    private void UpdateEnergy()
    {
        if (energyImage == null) return;
        energyImage.fillAmount = _stats.CurrentEnergy < _stats.MaxEnergy
            ? _stats.CurrentEnergy / _stats.MaxEnergy
            : 1f;
        energyImage.color = _stats.CurrentEnergy < _stats.SurgeActivationThreshhold ? Color.red : Color.yellow;
    }

    private void LateUpdate()
    {
        if (_camera == null)
            _camera = Camera.main;
        if (_camera == null) return;
        transform.rotation = _camera.transform.rotation;
    }
}