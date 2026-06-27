using UnityEngine;

public class KnightiestKnightAugment : MonoBehaviour
{
    [SerializeField] private float _bonusWeaponRangePercent = 0.175f;
    [SerializeField] private float _bonusMaxHealthPercent = 0.075f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }
        stats.bonusWeaponRangePercent += _bonusWeaponRangePercent;
        stats.BonusMaxHealthPercent += _bonusMaxHealthPercent;
        Destroy(gameObject);
    }
}
