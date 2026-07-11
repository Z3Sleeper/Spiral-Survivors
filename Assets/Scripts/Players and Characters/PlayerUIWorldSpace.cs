using UnityEngine;
using UnityEngine.UI;

public class PlayerUIWorldSpace : MonoBehaviour
{
    [SerializeField] private Slider healthBar;
    [SerializeField] private Slider shieldBar;
    [SerializeField] private GameObject dashUI;
    [SerializeField] private Image dashChargeCooldownImage;
    [SerializeField] private GameObject[] dashNotches;
    [SerializeField] private GameObject energyUI;
    [SerializeField] private Image energyImage;

    private CharacterStatsPD _stats;
    private PDCharacterController _controller;
    private Camera _camera;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStatsPD>();
        _controller = GetComponentInParent<PDCharacterController>();
        _camera = Camera.main;

        bool owner = _controller != null && _controller.isOwner;
        if (dashUI != null) dashUI.SetActive(owner);
        if (energyUI != null) energyUI.SetActive(owner);
    }

    private void Update()
    {
        if (_stats == null) return;

        if (healthBar != null)
            healthBar.value = _stats.MaxHealth > 0f ? _stats.UIHealth / _stats.MaxHealth : 0f;
        if (shieldBar != null)
            shieldBar.value = _stats.MaxShield > 0f ? _stats.UIShield / _stats.MaxShield : 0f;

        if (_controller == null || !_controller.isOwner) return;

        UpdateDash();
        UpdateEnergy();
    }

    private void UpdateDash()
    {
        if (dashChargeCooldownImage != null)
            dashChargeCooldownImage.fillAmount = _controller.currentState.currentCharges < _stats.dashCharges
                ? 1f - (_controller.currentState.chargeRegenTimer / _stats.DashCooldown)
                : 1f;
        for (int i = 0; i < dashNotches.Length; i++)
            dashNotches[i].SetActive(i < _controller.currentState.currentCharges);
    }

    private void UpdateEnergy()
    {
        if (energyImage == null) return;
        energyImage.fillAmount = _stats.MaxEnergy > 0f ? _stats.UIEnergy / _stats.MaxEnergy : 1f;
        energyImage.color = _stats.UIIsSurging || _stats.UIEnergy < _stats.SurgeActivationThreshhold
            ? Color.red
            : Color.yellow;
    }

    private void LateUpdate()
    {
        if (_camera == null)
            _camera = Camera.main;
        if (_camera == null) return;
        transform.rotation = _camera.transform.rotation;
    }
}
