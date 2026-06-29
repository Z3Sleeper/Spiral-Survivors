using UnityEngine;

public class SurgeNoMoveSpeedPenaltyAugment : MonoBehaviour
{
    [SerializeField] private float _energyRegenFlatBonus = 5f;
    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.surgeMoveSpeedPenalty = Mathf.Max(stats.surgeMoveSpeedPenalty, 1f);
        stats.bonusEnergyRegenFlat += _energyRegenFlatBonus;
        Destroy(gameObject);
    }
}
