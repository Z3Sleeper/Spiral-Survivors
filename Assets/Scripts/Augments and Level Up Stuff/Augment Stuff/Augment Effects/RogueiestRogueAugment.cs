using UnityEngine;

public class RogueiestRogueAugment : MonoBehaviour
{
    [SerializeField] private float _bonusAttackSpeedTotalPercent = 0.175f;
    [SerializeField] private float _bonusMoveSpeedPercent = 0.075f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.attackSpeedTotalPercent += _bonusAttackSpeedTotalPercent;
        stats.bonusMoveSpeedPercent += _bonusMoveSpeedPercent;
        Destroy(gameObject);
    }
}
