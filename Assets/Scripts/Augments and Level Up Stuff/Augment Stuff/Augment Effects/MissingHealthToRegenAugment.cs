using UnityEngine;

public class MissingHealthToRegenAugment : MonoBehaviour
{
    [SerializeField] private float _healthPercent = 0.2f;

    private CharacterStats _stats;
    private float _contribution;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
    }

    private void Update()
    {
        float newContribution = (_stats.MaxHealth - _stats.CurrentHealth) * _healthPercent;
        if (newContribution == _contribution) return;

        _stats.BonusHealthRegenFlat += newContribution - _contribution;
        _contribution = newContribution;
    }

    private void OnDestroy()
    {
        if (_stats != null)
            _stats.BonusHealthRegenFlat -= _contribution;
    }
}
