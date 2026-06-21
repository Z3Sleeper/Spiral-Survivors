using UnityEngine;

public class RangeBasedOnCurrentEnergyAugment : MonoBehaviour
{
    [SerializeField] private float _rangePerEnergy = 0.25f;
    [SerializeField] private float _bonusMaxEnergy = 20f;
    [SerializeField] private float _bonusEnergyRegen = 5f;
    private CharacterStats _stats;
    private float _currentBonus = 0f;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner)
        {
            Destroy(gameObject);
            return;
        }
        _stats.bonusMaxEnergyFlat += _bonusMaxEnergy;
        _stats.bonusEnergyRegenFlat += _bonusEnergyRegen;
    }

    private void Update()
    {
        if (_stats == null || !_stats.isSpawned) return;

        float currentEnergyPercent = Mathf.Clamp01(_stats.CurrentEnergy / _stats.MaxEnergy);
        float newBonus = currentEnergyPercent * _rangePerEnergy;

        _stats.bonusWeaponRangePercent -= _currentBonus;
        _stats.bonusWeaponRangePercent += newBonus;
        _currentBonus = newBonus;
    }

    private void OnDestroy()
    {
        if (_stats != null && _stats.isSpawned)
            _stats.bonusWeaponRangePercent -= _currentBonus;
    }
}