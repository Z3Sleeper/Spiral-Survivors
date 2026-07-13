using System.Collections;
using UnityEngine;

public class ShieldDamageReductionAugment : MonoBehaviour
{
    [SerializeField] private float _damageReduction = 0.25f;
    [SerializeField] private float _duration = 5f;
    [SerializeField] private float _maxShieldBonusFlat = 0.05f;

    private CharacterStats _stats;
    private Coroutine _removalCoroutine;
    private bool _active;
    private float _prevShield;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.BonusMaxShieldFlat += _maxShieldBonusFlat;
        _stats.damageReductionPercent *= (1f - _damageReduction);
        _active = true;
        _prevShield = _stats.CurrentShield;
        _stats._currentShield.onChanged += OnShieldValueChanged;
    }

    private void OnDestroy()
    {
        if (_stats != null)
            _stats._currentShield.onChanged -= OnShieldValueChanged;
        if (_active && _stats != null)
        {
            _stats.damageReductionPercent /= (1f - _damageReduction);
            _stats.BonusMaxShieldFlat -= _maxShieldBonusFlat;
        }
    }

    private void OnShieldValueChanged(float current)
    {
        float prev = _prevShield;
        _prevShield = current;

        if (prev > 0f && current <= 0f)
        {
            // Shield broke — start removal countdown
            if (_removalCoroutine != null) StopCoroutine(_removalCoroutine);
            _removalCoroutine = StartCoroutine(RemovalRoutine());
        }
        else if (current > 0f && prev <= 0f)
        {
            // Shield restored — cancel countdown, reapply DR if it was removed
            if (_removalCoroutine != null)
            {
                StopCoroutine(_removalCoroutine);
                _removalCoroutine = null;
            }
            if (!_active)
            {
                _stats.damageReductionPercent *= (1f - _damageReduction);
                _active = true;
            }
        }
    }

    private IEnumerator RemovalRoutine()
    {
        yield return new WaitForSeconds(_duration);
        _stats.damageReductionPercent /= (1f - _damageReduction);
        _active = false;
        _removalCoroutine = null;
    }
}
