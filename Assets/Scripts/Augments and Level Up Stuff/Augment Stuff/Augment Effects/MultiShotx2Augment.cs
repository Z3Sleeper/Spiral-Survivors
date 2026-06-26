using UnityEngine;

public class MultiShotx2Augment : MonoBehaviour
{
    [SerializeField] private float _damagePenalty = 0.5f;
    [SerializeField] private float _onHitPenalty = 0.25f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats != null && stats.isOwner)
        {
            stats.bonusMultishotPercent += 1;
            stats.bonusWeaponDamagePenalty *= 1f - _damagePenalty;
            stats.bonusOnHitEffectivenessPenalty *= 1f - _onHitPenalty;
        }
        Destroy(gameObject);
    }
}
