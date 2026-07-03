using UnityEngine;

public class OnHitEqualToCurrentEnergyAugment : MonoBehaviour
{
    [SerializeField] private float _baseEnergyDamagePercent = 0.1f;
    [SerializeField] private float _variancePercent = 0.20f;
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

    private void OnHit(CharacterStats stats, EnemyBase enemy, float effectiveness, bool isCrit)
    {
        if (stats != _stats) return;
        float max = _baseEnergyDamagePercent * (1f + _variancePercent);
        float min = Mathf.Lerp(_baseEnergyDamagePercent * (1f - _variancePercent), max, _stats.LuckChance);
        enemy.TakeDamageRpc(_stats.CurrentEnergy * Random.Range(min, max));
    }
}
