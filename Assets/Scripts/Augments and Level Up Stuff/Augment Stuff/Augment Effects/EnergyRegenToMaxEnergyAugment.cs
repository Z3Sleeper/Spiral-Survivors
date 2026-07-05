using UnityEngine;

public class EnergyRegenToMaxEnergyAugment : MonoBehaviour
{
    [SerializeField] private float _conversionMultiplier = 1f;
    [SerializeField] private float _bonusEnergyRegenFlat = 2f;

    private CharacterStats _stats;
    private float _appliedBonus;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusEnergyRegenFlat += _bonusEnergyRegenFlat;
        _appliedBonus = _stats.EnergyRegen * _conversionMultiplier;
        _stats.bonusMaxEnergyFlat += _appliedBonus;
    }

    private void Update()
    {
        if (_stats == null) return;
        float newBonus = _stats.EnergyRegen * _conversionMultiplier;
        float delta = newBonus - _appliedBonus;
        if (delta != 0f)
        {
            _stats.bonusMaxEnergyFlat += delta;
            _appliedBonus = newBonus;
        }
    }

    private void OnDestroy()
    {
        if (_stats == null || !_stats.isOwner || !_stats.isSpawned) return;
        _stats.bonusEnergyRegenFlat -= _bonusEnergyRegenFlat;
        _stats.bonusMaxEnergyFlat -= _appliedBonus;
    }
}
