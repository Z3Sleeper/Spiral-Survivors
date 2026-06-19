using UnityEngine;

public class SurgeNoMoveSpeedPenaltyAugment : MonoBehaviour
{
    [SerializeField] private float _energyRegenFlatBonus = 10f;
    [SerializeField] private float _energyRegenPercentBonus = 0.25f;
    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.surgeMoveSpeedPenalty = Mathf.Max(stats.surgeMoveSpeedPenalty, 1f);
        stats.bonusEnergyRegenFlat += _energyRegenFlatBonus;
        stats.bonusEnergyRegenPercent += _energyRegenPercentBonus;
        Destroy(gameObject);
    }
}
