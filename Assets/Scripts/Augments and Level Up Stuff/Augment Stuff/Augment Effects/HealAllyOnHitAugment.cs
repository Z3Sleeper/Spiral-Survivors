using UnityEngine;

public class HealAllyOnHitAugment : MonoBehaviour
{
    [SerializeField] private float _baseHealMultiplier = 6f;
    [SerializeField] private float _variancePercent = 0.20f;
    [SerializeField] private float _bonusHealthRegen = 1f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.canContactAllies = true;
        _stats.AddHealthRegenFlat(_bonusHealthRegen);
        WeaponBase.OnAllyHit += OnAllyHit;
    }

    private void OnDestroy()
    {
        WeaponBase.OnAllyHit -= OnAllyHit;
        if (_stats == null || !_stats.isOwner || !_stats.isSpawned) return;
        _stats.AddHealthRegenFlat(-_bonusHealthRegen);
    }

    private void OnAllyHit(CharacterStats stats, CharacterStats ally, float effectiveness)
    {
        if (stats != _stats) return;
        float max = _baseHealMultiplier * (1f + _variancePercent);
        float min = Mathf.Lerp(_baseHealMultiplier * (1f - _variancePercent), max, _stats.LuckChance);
        float amount = _stats.HealthRegen * Random.Range(min, max) * _stats.OnHitPower * effectiveness;
        ally.ReceiveHealRpc(amount);
        _stats.ReceiveHealRpc(amount);
    }
}
