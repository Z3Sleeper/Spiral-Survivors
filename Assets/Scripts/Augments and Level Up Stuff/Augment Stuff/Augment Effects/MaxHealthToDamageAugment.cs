using UnityEngine;

public class MaxHealthToDamageAugment : MonoBehaviour
{
    [SerializeField] private float _healthPercent = 0.2f;
    [SerializeField] private float _bonusMaxHealth = 10f;

    private CharacterStats _stats;
    private float _contribution;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }

        _stats.BonusMaxHealthFlat += _bonusMaxHealth;
        _contribution = _stats.MaxHealth * _healthPercent;
        _stats.bonusWeaponDamageFlat += _contribution;
    }

    private void Update()
    {
        if (!_stats.isSpawned) return;
        float newContribution = _stats.MaxHealth * _healthPercent;
        if (newContribution == _contribution) return;

        _stats.bonusWeaponDamageFlat += newContribution - _contribution;
        _contribution = newContribution;
    }

    private void OnDestroy()
    {
        if (_stats != null && _stats.isSpawned)
        {
            _stats.BonusMaxHealthFlat -= _bonusMaxHealth;
            _stats.bonusWeaponDamageFlat -= _contribution;
        }
    }
}