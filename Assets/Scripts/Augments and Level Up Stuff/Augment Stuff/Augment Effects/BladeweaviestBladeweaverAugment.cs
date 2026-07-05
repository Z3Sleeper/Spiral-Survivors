using UnityEngine;

public class BladeweaviestBladeweaverAugment : MonoBehaviour
{
    [SerializeField] private int _bonusWeapons = 3;
    [SerializeField] private float _damagePenalty = 0.45f;
    [SerializeField] private float _onHitPowerPenalty = 0.25f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusWeaponCountFlat += _bonusWeapons;
        _stats.bonusWeaponDamagePenalty *= 1f - _damagePenalty;
        _stats.bonusOnHitPowerPenalty *= 1f - _onHitPowerPenalty;
    }

    private void OnDestroy()
    {
        if (_stats == null) return;
        _stats.bonusWeaponCountFlat -= _bonusWeapons;
        _stats.bonusWeaponDamagePenalty /= 1f - _damagePenalty;
        _stats.bonusOnHitPowerPenalty /= 1f - _onHitPowerPenalty;
    }
}
