using System.Collections;
using UnityEngine;

public class ShieldDamageReductionAugment : MonoBehaviour
{
    [SerializeField] private float _damageReduction = 0.25f;
    [SerializeField] private float _duration = 5f;
    [SerializeField] private float _maxShieldBonusPercent = 0.5f;

    private CharacterStats _stats;
    private Coroutine _drCoroutine;
    private bool _active;
    private float _prevShield;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.AddMaxShieldPercentRpc(_maxShieldBonusPercent);
        _prevShield = _stats.CurrentShield;
        _stats._currentShield.onChanged += OnShieldValueChanged;
    }

    private void OnDestroy()
    {
        if (_stats != null)
            _stats._currentShield.onChanged -= OnShieldValueChanged;
        if (_active && _stats != null)
            _stats.damageReductionPercent /= (1f - _damageReduction);
    }

    private void OnShieldValueChanged(float current)
    {
        float prev = _prevShield;
        _prevShield = current;
        if (prev <= 0f || current > 0f) return;
        if (_drCoroutine != null)
            StopCoroutine(_drCoroutine);
        else
        {
            _stats.damageReductionPercent *= (1f - _damageReduction);
            _active = true;
        }
        _drCoroutine = StartCoroutine(DRRoutine());
    }

    private IEnumerator DRRoutine()
    {
        yield return new WaitForSeconds(_duration);
        _stats.damageReductionPercent /= (1f - _damageReduction);
        _active = false;
        _drCoroutine = null;
    }
}
