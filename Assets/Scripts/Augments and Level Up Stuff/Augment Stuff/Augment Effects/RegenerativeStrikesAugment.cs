using UnityEngine;

public class RegenerativeStrikesAugment : MonoBehaviour
{
    [SerializeField] private float _regenMultiplier = 3f;

    private CharacterStats _stats;

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        WeaponBase.OnHit += OnHit;
    }

    private void OnDestroy()
    {
        WeaponBase.OnHit -= OnHit;
    }

    private void OnHit(CharacterStats attacker, EnemyBase enemy)
    {
        if (attacker != _stats) return;
        float bonus = _regenMultiplier * _stats.HealthRegen * _stats.OnHitEffectiveness;
        enemy.TakeDamageRpc(bonus);
    }
}
