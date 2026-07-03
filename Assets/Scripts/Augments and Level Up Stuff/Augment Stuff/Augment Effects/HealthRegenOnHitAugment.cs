using UnityEngine;

public class HealthRegenOnHitAugment : MonoBehaviour
{
    [SerializeField] private float _baseRegenMultiplier = 6f;
    [SerializeField] private float _variancePercent = 0.20f;
    [SerializeField] private float _flatRegenBonus = 0.5f;

    private CharacterStats _stats;

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null) { Destroy(gameObject); return; }

        if (!_stats.isOwner) return;
        _stats.AddHealthRegenFlat(_flatRegenBonus);
        WeaponBase.OnHit += OnHit;
    }

    private void OnDestroy()
    {
        WeaponBase.OnHit -= OnHit;
        if (_stats != null && _stats.isOwner && _stats.isSpawned) _stats.AddHealthRegenFlat(-_flatRegenBonus);
    }

    private void OnHit(CharacterStats source, EnemyBase enemy, float effectiveness, bool isCrit)
    {
        if (source != _stats) return;
        float max = _baseRegenMultiplier * (1f + _variancePercent);
        float min = Mathf.Lerp(_baseRegenMultiplier * (1f - _variancePercent), max, _stats.LuckChance);
        float bonus = Random.Range(min, max) * _stats.HealthRegen * _stats.OnHitEffectiveness * effectiveness;
        enemy.TakeDamageRpc(bonus);
    }
}
