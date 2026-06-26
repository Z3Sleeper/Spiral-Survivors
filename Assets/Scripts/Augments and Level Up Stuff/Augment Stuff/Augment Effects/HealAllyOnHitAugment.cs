using UnityEngine;

public class HealAllyOnHitAugment : MonoBehaviour
{
    [SerializeField] private float _healMultiplier = 3f;
    [SerializeField] private float _bonusHealthRegen = 1f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.canContactAllies = true;
        _stats.BonusHealthRegenFlat += _bonusHealthRegen;
        WeaponBase.OnAllyHit += OnAllyHit;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.BonusHealthRegenFlat -= _bonusHealthRegen;
        WeaponBase.OnAllyHit -= OnAllyHit;
    }

    private void OnAllyHit(CharacterStats stats, CharacterStats ally, float effectiveness)
    {
        if (stats != _stats) return;
        ally.HealRpc(_stats.HealthRegen * _healMultiplier * _stats.OnHitEffectiveness * effectiveness);
    }
}
