using UnityEngine;

public class MissingHealthToRegenAugment : MonoBehaviour
{
    [SerializeField] private float regenPercentBonus = 1f;
    [SerializeField] private float flatRegenBonus = 1f;

    private CharacterStats _stats;
    private bool _bonusActive;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.BonusHealthRegenFlat += flatRegenBonus;
    }

    private void Update()
    {
        if (!_stats.isSpawned) return;
        bool shouldBeActive = _stats.CurrentHealth < _stats.MaxHealth * 0.5f;
        if (shouldBeActive == _bonusActive) return;

        _bonusActive = shouldBeActive;
        _stats.BonusHealthRegenPercent += shouldBeActive ? regenPercentBonus : -regenPercentBonus;
    }

    private void OnDestroy()
    {
        if (_stats == null || !_stats.isSpawned) return;
        _stats.BonusHealthRegenFlat -= flatRegenBonus;
        if (_bonusActive)
            _stats.BonusHealthRegenPercent -= regenPercentBonus;
    }
}
