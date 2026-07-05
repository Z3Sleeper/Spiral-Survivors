using UnityEngine;

public class X2WeaponsAugment : MonoBehaviour
{
    [SerializeField] private float _damagePenalty = 0.45f;
    [SerializeField] private float _onHitPenalty = 0.45f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusWeaponCountMultiplier += 1;
        _stats.bonusWeaponDamagePenalty *= 1f - _damagePenalty;
        _stats.bonusOnHitPowerPenalty *= 1f - _onHitPenalty;
    }

    private void OnDestroy()
    {
        if (_stats == null) return;
        _stats.bonusWeaponCountMultiplier -= 1;
        _stats.bonusWeaponDamagePenalty /= 1f - _damagePenalty;
        _stats.bonusOnHitPowerPenalty /= 1f - _onHitPenalty;
    }
}
