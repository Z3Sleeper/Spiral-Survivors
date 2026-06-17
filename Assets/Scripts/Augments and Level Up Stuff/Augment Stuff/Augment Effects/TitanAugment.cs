using UnityEngine;

public class TitanAugment : MonoBehaviour
{
    [SerializeField] private float _sizeMultiplier = 1.5f;
    [SerializeField] private float _healthBonus = 0.3f;
    [SerializeField] private float _damageBonus = 0.15f;

    private static readonly string[] _unscaledChildren = { "ReviveZone", "WorldspaceUI", "Minimap Icon" };

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }

        stats.transform.localScale *= _sizeMultiplier;
        stats.bonusWeaponRangePercent *= _sizeMultiplier;
        stats.bonusWeaponRadiusPercent *= _sizeMultiplier;
        stats.bonusWeaponSizePercent *= _sizeMultiplier;

        foreach (var childName in _unscaledChildren)
        {
            var child = stats.transform.Find(childName);
            if (child != null)
                child.localScale /= _sizeMultiplier;
        }

        stats.MultiplyMaxHealthRpc(1f + _healthBonus);
        stats.bonusWeaponDamagePercent += _damageBonus;

        Destroy(gameObject);
    }
}
