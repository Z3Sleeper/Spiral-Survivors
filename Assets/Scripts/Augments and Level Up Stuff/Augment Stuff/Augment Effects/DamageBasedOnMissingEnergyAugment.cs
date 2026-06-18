using UnityEngine;

public class DamageBasedOnMissingEnergyAugment : MonoBehaviour
{
    [SerializeField] private float _damagePerEnergy = 0.5f;
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
    }

    private void Update()
    {
        if (_stats == null) return;

        float missingEnergyPercent = 1f - Mathf.Clamp01(_stats.CurrentEnergy / _stats.MaxEnergy);
        float newBonus = missingEnergyPercent * _damagePerEnergy;

        _stats.bonusWeaponDamagePercent -= _currentBonus;
        _stats.bonusWeaponDamagePercent += newBonus;
        _currentBonus = newBonus;
    }

    private void OnDestroy()
    {
        if (_stats != null)
            _stats.bonusWeaponDamagePercent -= _currentBonus;
    }
}