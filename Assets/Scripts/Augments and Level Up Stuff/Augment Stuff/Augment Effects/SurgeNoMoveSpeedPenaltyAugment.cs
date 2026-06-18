using UnityEngine;

public class SurgeNoMoveSpeedPenaltyAugment : MonoBehaviour
{
    [SerializeField] private float _energyRegenBonus = 0.25f;
    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.surgeMoveSpeedPenalty = Mathf.Max(stats.surgeMoveSpeedPenalty, 1f);
        stats.bonusEnergyRegenPercent += _energyRegenBonus;
        Destroy(gameObject);
    }
}
