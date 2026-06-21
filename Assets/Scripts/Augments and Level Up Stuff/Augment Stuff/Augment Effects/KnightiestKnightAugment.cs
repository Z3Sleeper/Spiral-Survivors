using UnityEngine;

public class KnightiestKnightAugment : MonoBehaviour
{
    [SerializeField] private float _bonusWeaponRangeFlat = 4f;
    [SerializeField] private float _bonusWeaponRangePercent = 0.5f;
    [SerializeField] private float _bonusMaxHealth = 20f;
    [SerializeField] private float _bonusHealthRegen = 2f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.bonusWeaponRangeFlat += _bonusWeaponRangeFlat;
        stats.bonusWeaponRangePercent += _bonusWeaponRangePercent;
        stats.BonusMaxHealthFlat += _bonusMaxHealth;
        stats.BonusHealthRegenFlat += _bonusHealthRegen;
        Destroy(gameObject);
    }
}
