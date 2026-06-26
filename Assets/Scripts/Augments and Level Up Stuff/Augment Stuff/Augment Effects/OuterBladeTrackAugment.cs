using UnityEngine;

public class OuterBladeTrackAugment : MonoBehaviour
{
    [SerializeField] private float _damagePenalty = 0.45f;
    [SerializeField] private float _onHitEffectivenessPenalty = 0.25f;
    [SerializeField] private float _rangePenalty = 0.25f;

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }

        stats.bonusWeaponDamagePenalty *= 1f - _damagePenalty;
        stats.bonusOnHitEffectivenessPenalty *= 1f - _onHitEffectivenessPenalty;
        stats.weaponRangePenalty *= 1f - _rangePenalty;

        var source = stats.GetComponent<WeaponManager>();
        var outer = stats.gameObject.AddComponent<OuterWeaponTrackManager>();
        outer.Initialize(stats, source.WeaponPrefab);

        Destroy(gameObject);
    }
}
