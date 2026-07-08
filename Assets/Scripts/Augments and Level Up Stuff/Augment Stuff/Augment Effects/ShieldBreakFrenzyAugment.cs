using System.Collections;
using UnityEngine;

public class ShieldBreakFrenzyAugment : MonoBehaviour
{
    [SerializeField] private float _totalAttackSpeedBonus = 0.5f;
    [SerializeField] private float _moveSpeedBonus = 0.5f;
    [SerializeField] private float _duration = 2f;
    [SerializeField] private float _maxShieldBonusFlat = 0.05f;

    private CharacterStats _stats;
    private Coroutine _buffCoroutine;
    private bool _buffActive;
    private float _prevShield;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.BonusMaxShieldFlat += _maxShieldBonusFlat;
        _prevShield = _stats.CurrentShield;
        _stats._currentShield.onChanged += OnShieldValueChanged;
    }

    private void OnDestroy()
    {
        if (_stats == null) return;
        _stats._currentShield.onChanged -= OnShieldValueChanged;
        if (_buffActive) RemoveBuff();
        if (_stats.isSpawned) _stats.BonusMaxShieldFlat -= _maxShieldBonusFlat;
    }

    private void OnShieldValueChanged(float current)
    {
        float prev = _prevShield;
        _prevShield = current;

        if (prev > 0f && current <= 0f)
        {
            if (_buffCoroutine != null) StopCoroutine(_buffCoroutine);
            if (!_buffActive) ApplyBuff();
            _buffCoroutine = StartCoroutine(BuffRoutine());
        }
    }

    private void ApplyBuff()
    {
        _stats.attackSpeedTotalPercent += _totalAttackSpeedBonus;
        _stats.bonusMoveSpeedPercent += _moveSpeedBonus;
        _buffActive = true;
    }

    private void RemoveBuff()
    {
        _stats.attackSpeedTotalPercent -= _totalAttackSpeedBonus;
        _stats.bonusMoveSpeedPercent -= _moveSpeedBonus;
        _buffActive = false;
    }

    private IEnumerator BuffRoutine()
    {
        yield return new WaitForSeconds(_duration);
        RemoveBuff();
        _buffCoroutine = null;
    }
}
