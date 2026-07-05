using UnityEngine;

public class RangeBasedOnCurrentEnergyAugment : MonoBehaviour
{
    [SerializeField] private float _bonusMaxEnergy   = 25f;
    [SerializeField] private float _energyThreshold  = 0.75f;
    [SerializeField] private float _maxEnergyPercent = 0.05f;

    private CharacterStats _stats;
    private float _currentBonus;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusMaxEnergyFlat += _bonusMaxEnergy;
    }

    private void Update()
    {
        if (_stats == null || !_stats.isSpawned) return;

        float targetBonus = _stats.CurrentEnergy >= _stats.MaxEnergy * _energyThreshold
            ? _maxEnergyPercent * _stats.MaxEnergy
            : 0f;

        if (Mathf.Abs(targetBonus - _currentBonus) > 0.0001f)
        {
            _stats.bonusWeaponRangeFlat += targetBonus - _currentBonus;
            _currentBonus = targetBonus;
        }
    }

    private void OnDestroy()
    {
        if (_stats == null || !_stats.isOwner || !_stats.isSpawned) return;
        _stats.bonusMaxEnergyFlat   -= _bonusMaxEnergy;
        _stats.bonusWeaponRangeFlat -= _currentBonus;
    }
}
