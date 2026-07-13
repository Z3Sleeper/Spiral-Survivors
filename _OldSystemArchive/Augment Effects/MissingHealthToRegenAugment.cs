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
        _stats.AddHealthRegenFlat(flatRegenBonus);
    }

    private void Update()
    {
        if (!_stats.isSpawned) return;
        bool shouldBeActive = _stats.CurrentHealth < _stats.MaxHealth * 0.5f;
        if (shouldBeActive == _bonusActive) return;

        _bonusActive = shouldBeActive;
        _stats.AddHealthRegenPercent(shouldBeActive ? regenPercentBonus : -regenPercentBonus);
    }

    private void OnDestroy()
    {
        if (_stats == null || !_stats.isOwner || !_stats.isSpawned) return;
        _stats.AddHealthRegenFlat(-flatRegenBonus);
        if (_bonusActive)
            _stats.AddHealthRegenPercent(-regenPercentBonus);
    }
}
