using UnityEngine;

public class RogueiestRogueAugment : MonoBehaviour
{
    [SerializeField] private float _bonusWeaponSpeedPercent = 0.175f;
    [SerializeField] private float _bonusMoveSpeedPercent = 0.075f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.bonusWeaponSpeedPercent += _bonusWeaponSpeedPercent;
        stats.bonusMoveSpeedPercent += _bonusMoveSpeedPercent;
        Destroy(gameObject);
    }
}
