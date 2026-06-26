using UnityEngine;

public class X2WeaponsAugment : MonoBehaviour
{
    [SerializeField] private float _damagePenalty = 0.45f;
    [SerializeField] private float _onHitPenalty = 0.45f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.bonusWeaponCountMultiplier += 1;
            stats.bonusWeaponDamagePenalty *= 1f - _damagePenalty;
            stats.bonusOnHitEffectivenessPenalty *= 1f - _onHitPenalty;
        }
        Destroy(gameObject);
    }
}
