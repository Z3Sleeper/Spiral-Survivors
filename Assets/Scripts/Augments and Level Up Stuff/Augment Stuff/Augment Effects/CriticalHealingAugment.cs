using UnityEngine;

public class CriticalHealingAugment : MonoBehaviour
{
    [SerializeField] private float bonusCritChance = 0.1f;

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

        _stats.HealSilentRpc(amount * (_stats.CritDamage - 1f));
    }
}
