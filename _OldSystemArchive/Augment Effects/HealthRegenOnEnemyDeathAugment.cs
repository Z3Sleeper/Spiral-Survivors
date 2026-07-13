using UnityEngine;

public class HealthRegenOnEnemyDeathAugment : MonoBehaviour
{
    [SerializeField] private float _baseRegenMultiplier = 1f;
    [SerializeField] private float _variancePercent = 0.20f;
    [SerializeField] private float _flatRegenBonus = 0.5f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.AddHealthRegenFlat(_flatRegenBonus);
        CharacterStats.OnEnemyDeath += OnEnemyDeath;
    }

    private void OnDestroy()
    {
        CharacterStats.OnEnemyDeath -= OnEnemyDeath;
        if (_stats != null && _stats.isOwner && _stats.isSpawned)
            _stats.AddHealthRegenFlat(-_flatRegenBonus);
    }

    private void OnEnemyDeath(CharacterStats killer, EnemyBase enemy)
    {
        float max = _baseRegenMultiplier * (1f + _variancePercent);
        float min = Mathf.Lerp(_baseRegenMultiplier * (1f - _variancePercent), max, _stats.LuckChance);
        _stats.ReceiveHealRpc(Random.Range(min, max) * _stats.HealthRegen);
    }
}
