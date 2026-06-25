using UnityEngine;

public class BladeweaviestBladeweaverAugment : MonoBehaviour
{
    [SerializeField] private int _bonusWeapons = 3;
    [SerializeField] private float _damagePenalty = 0.45f;
    [SerializeField] private float _onHitEffectivenessPenalty = 0.25f;
    [SerializeField] private float _bonusRangeFlat = 4f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }

        stats.bonusWeaponCountFlat += _bonusWeapons;
        stats.bonusWeaponDamagePenalty *= 1f - _damagePenalty;
        stats.bonusOnHitEffectivenessPenalty *= 1f - _onHitEffectivenessPenalty;
        stats.bonusWeaponRangeFlat += _bonusRangeFlat;

        Destroy(gameObject);
    }
}
