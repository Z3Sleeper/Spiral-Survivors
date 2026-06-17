using UnityEngine;

public class X2WeaponsAugment : MonoBehaviour
{
    [SerializeField] private float _damagePenalty = 0.3f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.bonusWeaponCountMultiplier += 1;
            stats.bonusWeaponDamagePenalty *= (1f - _damagePenalty);
        }
        Destroy(gameObject);
    }
}
