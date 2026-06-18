using UnityEngine;

public class MultiShotx2Augment : MonoBehaviour
{
    [SerializeField] private float _damagePenalty = 0.6f;
    [SerializeField] private float _onHitPenalty = 0.6f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.bonusMultishotPercent += 1;
            stats.bonusWeaponDamagePenalty *= (1f - _damagePenalty);
            stats.bonusOnHitEffectivenessPenalty *= (1f - _onHitPenalty);
        }
        Destroy(gameObject);
    }
}
