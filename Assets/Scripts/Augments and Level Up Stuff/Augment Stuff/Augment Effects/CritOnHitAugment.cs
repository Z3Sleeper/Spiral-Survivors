using UnityEngine;

public class CritOnHitAugment : MonoBehaviour
{
    [SerializeField] private float _bonusCritChance = 0.10f;
    [SerializeField] private float _percentDamageOnHit = 0.10f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusCritChanceFlat += _bonusCritChance;
        WeaponBase.OnCritStrike += OnCritStrike;
        WeaponBase.OnHit += OnHit;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.bonusCritChanceFlat -= _bonusCritChance;
        WeaponBase.OnCritStrike -= OnCritStrike;
        WeaponBase.OnHit -= OnHit;
    }

    private void OnHit(CharacterStats stats, EnemyBase enemy, float effectiveness)
    {
        if (stats != _stats) return;
        enemy.TakeDamageRpc(_stats.WeaponDamage * _percentDamageOnHit);
    }

    private void OnCritStrike(CharacterStats stats, EnemyBase enemy, float effectiveness)
    {
        if (stats != _stats) return;
        WeaponBase.RaiseOnHit(stats, enemy, effectiveness);
    }
}
