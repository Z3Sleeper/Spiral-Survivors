using UnityEngine;

public class CriticalHealingAugment : MonoBehaviour
{
    [SerializeField] private float bonusCritChance = 0.1f;
    [SerializeField] private float _baseCritHealMultiplier = 0.50f;
    [SerializeField] private float _variancePercent = 0.25f;

    private CharacterStats _stats;
    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusCritChanceFlat += bonusCritChance;
        CharacterStats.OnHeal += OnHeal;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.bonusCritChanceFlat -= bonusCritChance;
        CharacterStats.OnHeal -= OnHeal;
    }

    private void OnHeal(CharacterStats stats, float amount)
    {
        if (stats != _stats) return;
        if (Random.value >= _stats.CritChance) return;

        float max = _baseCritHealMultiplier * (1f + _variancePercent);
        float min = Mathf.Lerp(_baseCritHealMultiplier * (1f - _variancePercent), max, _stats.LuckChance);
        _stats.HealSilent(amount * (_stats.CritDamage - 1f) * Random.Range(min, max));
    }
}
