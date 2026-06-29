using UnityEngine;

public class OnHitEqualToCurrentEnergyAugment : MonoBehaviour
{
    [SerializeField] private float energyDamagePercent = 0.1f;
    [SerializeField] private float bonusMaxEnergy = 25f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusMaxEnergyFlat += bonusMaxEnergy;
        WeaponBase.OnHit += OnHit;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.bonusMaxEnergyFlat -= bonusMaxEnergy;
        WeaponBase.OnHit -= OnHit;
    }

    private void OnHit(CharacterStats stats, EnemyBase enemy, float effectiveness)
    {
        if (stats != _stats) return;
        enemy.TakeDamageRpc(_stats.CurrentEnergy * energyDamagePercent);
    }
}
