using System.Collections;
using UnityEngine;

public class SurgeDamageReductionAugment : MonoBehaviour
{
    [SerializeField] private float _damageReduction = 0.25f;
    [SerializeField] private float _invincibilityDuration = 0.2f;
    [SerializeField] private float _bonusMaxHealthFlat = 10f;
    [SerializeField] private float _bonusEnergyRegenFlat = 5f;

    private CharacterStats _stats;
    private Coroutine _invincibilityCoroutine;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.BonusMaxHealthFlat      += _bonusMaxHealthFlat;
        _stats.bonusEnergyRegenFlat    += _bonusEnergyRegenFlat;
        PlayerController.OnSurgeStarted += OnSurgeStarted;
        PlayerController.OnSurgeEnded   += OnSurgeEnded;
    }

    private void OnDestroy()
    {
        PlayerController.OnSurgeStarted -= OnSurgeStarted;
        PlayerController.OnSurgeEnded   -= OnSurgeEnded;
    }

    private void OnSurgeStarted(PlayerController controller)
    {
        if (controller.GetComponent<CharacterStats>() != _stats) return;
        _stats.damageReductionPercent *= (1f - _damageReduction);
        _invincibilityCoroutine = StartCoroutine(InvincibilityRoutine());
    }

    private void OnSurgeEnded(PlayerController controller)
    {
        if (controller.GetComponent<CharacterStats>() != _stats) return;
        _stats.damageReductionPercent /= (1f - _damageReduction);
        if (_invincibilityCoroutine != null)
        {
            StopCoroutine(_invincibilityCoroutine);
            _invincibilityCoroutine = null;
            _stats.invincible = false;
        }
    }

    private IEnumerator InvincibilityRoutine()
    {
        _stats.invincible = true;
        yield return new WaitForSeconds(_invincibilityDuration);
        _stats.invincible = false;
        _invincibilityCoroutine = null;
    }
}
