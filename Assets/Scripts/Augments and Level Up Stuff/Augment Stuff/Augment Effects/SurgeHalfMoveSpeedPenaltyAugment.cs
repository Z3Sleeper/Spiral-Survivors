using UnityEngine;

public class SurgeHalfMoveSpeedPenaltyAugment : MonoBehaviour
{
    [SerializeField] private float _energyRegenFlatBonus = 5f;
    [SerializeField] private float _energyRegenPercentBonus = 0.25f;
    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.surgeMoveSpeedPenalty = (stats.surgeMoveSpeedPenalty + 1f) * 0.5f;
        stats.bonusEnergyRegenFlat += _energyRegenFlatBonus;
        stats.bonusEnergyRegenPercent += _energyRegenPercentBonus;
        Destroy(gameObject);
    }
}
