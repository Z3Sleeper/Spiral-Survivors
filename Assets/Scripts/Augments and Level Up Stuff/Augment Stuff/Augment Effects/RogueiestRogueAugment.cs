using UnityEngine;

public class RogueiestRogueAugment : MonoBehaviour
{
    [SerializeField] private float _bonusWeaponDamageFlat = 36f;
    [SerializeField] private float _bonusWeaponSpeedPercent = 0.3f;
    [SerializeField] private float _bonusMoveSpeedPercent = 0.2f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.bonusWeaponDamageFlat += _bonusWeaponDamageFlat;
        stats.bonusWeaponSpeedPercent += _bonusWeaponSpeedPercent;
        stats.bonusMoveSpeedPercent += _bonusMoveSpeedPercent;
        Destroy(gameObject);
    }
}
