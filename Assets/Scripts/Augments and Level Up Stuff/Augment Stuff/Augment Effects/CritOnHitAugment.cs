using UnityEngine;

public class CritOnHitAugment : MonoBehaviour
{
    [SerializeField] private float _bonusCritChance = 0.10f;
    [SerializeField] private float _baseDamagePercent = 0.10f;
    [SerializeField] private float _variancePercent = 0.25f;
    [SerializeField] private float _onHitEffectivenessPenalty = 0.35f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusCritChanceFlat += _bonusCritChance;
        _stats.bonusOnHitEffectivenessPenalty *= 1f - _onHitEffectivenessPenalty;
        WeaponBase.OnCritStrike += OnCritStrike;
        WeaponBase.OnHit += OnHit;
    }

    private void OnDestroy()
    {
        if (_stats != null)
        {
            _stats.bonusCritChanceFlat -= _bonusCritChance;
            _stats.bonusOnHitEffectivenessPenalty /= 1f - _onHitEffectivenessPenalty;
        }
        WeaponBase.OnCritStrike -= OnCritStrike;
        WeaponBase.OnHit -= OnHit;
    }

    private void OnHit(CharacterStats stats, EnemyBase enemy, float effectiveness, bool isCrit)
    {
        if (stats != _stats) return;
        float max = _baseDamagePercent * (1f + _variancePercent);
        float min = Mathf.Lerp(_baseDamagePercent * (1f - _variancePercent), max, _stats.LuckChance);
        enemy.TakeDamageRpc(_stats.WeaponDamage * Random.Range(min, max) * _stats.OnHitEffectiveness * effectiveness);
    }

    private void OnCritStrike(CharacterStats stats, EnemyBase enemy, float effectiveness)
    {
        if (stats != _stats) return;
        WeaponBase.RaiseOnHit(stats, enemy, effectiveness, isCrit: true);
    }
}
