using UnityEngine;

public class CritOnHitAugment : MonoBehaviour
{
    [SerializeField] private float _bonusCritChance = 0.10f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusCritChanceFlat += _bonusCritChance;
        WeaponBase.OnCritStrike += OnCritStrike;
    }

    private void OnDestroy()
    {
        WeaponBase.OnCritStrike -= OnCritStrike;
    }

    private void OnCritStrike(CharacterStats stats, EnemyBase enemy, float effectiveness)
    {
        if (stats != _stats) return;
        WeaponBase.RaiseOnHit(stats, enemy, effectiveness);
    }
}
