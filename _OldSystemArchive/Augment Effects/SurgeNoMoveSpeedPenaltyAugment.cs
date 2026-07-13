using UnityEngine;

public class SurgeNoMoveSpeedPenaltyAugment : MonoBehaviour
{
    [SerializeField] private float _energyRegenFlatBonus = 5f;

    private CharacterStats _stats;
    private float _originalSurgePenalty;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _originalSurgePenalty = _stats.surgeMoveSpeedPenalty;
        _stats.surgeMoveSpeedPenalty = Mathf.Max(_stats.surgeMoveSpeedPenalty, 1f);
        _stats.bonusEnergyRegenFlat += _energyRegenFlatBonus;
    }

    private void OnDestroy()
    {
        if (_stats == null) return;
        _stats.surgeMoveSpeedPenalty = _originalSurgePenalty;
        _stats.bonusEnergyRegenFlat -= _energyRegenFlatBonus;
    }
}
