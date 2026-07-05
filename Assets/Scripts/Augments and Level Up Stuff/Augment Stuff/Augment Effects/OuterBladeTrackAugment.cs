using UnityEngine;

public class OuterBladeTrackAugment : MonoBehaviour
{
    [SerializeField] private float _damagePenalty = 0.45f;
    [SerializeField] private float _onHitPowerPenalty = 0.25f;
    [SerializeField] private float _rangePenalty = 0.25f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }

        _stats.bonusWeaponDamagePenalty *= 1f - _damagePenalty;
        _stats.bonusOnHitPowerPenalty *= 1f - _onHitPowerPenalty;
        _stats.weaponRangePenalty *= 1f - _rangePenalty;

        var source = _stats.GetComponent<WeaponManager>();
        var outer = _stats.gameObject.AddComponent<OuterWeaponTrackManager>();
        outer.Initialize(_stats, source.WeaponPrefab);
    }

    private void OnDestroy()
    {
        if (_stats == null) return;
        _stats.bonusWeaponDamagePenalty /= 1f - _damagePenalty;
        _stats.bonusOnHitPowerPenalty /= 1f - _onHitPowerPenalty;
        _stats.weaponRangePenalty /= 1f - _rangePenalty;
    }
}
