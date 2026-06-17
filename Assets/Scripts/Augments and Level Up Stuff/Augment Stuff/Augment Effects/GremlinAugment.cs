using UnityEngine;

public class GremlinAugment : MonoBehaviour
{
    [SerializeField] private float _sizeChange = 0.5f;
    [SerializeField] private float _attackSpeedPercentBonus = 1f;
    [SerializeField] private float _moveSpeedPercentBonus = 0.25f;

    private static readonly string[] _unscaledChildren = { "ReviveZone", "WorldspaceUI", "Minimap Icon" };

    private void Start()
    {
        var stats = GetComponentInParent<CharacterStats>();
        if (stats == null || !stats.isOwner) { Destroy(gameObject); return; }

        stats.transform.localScale *= _sizeChange;
        stats.bonusWeaponRadiusPercent *= _sizeChange;
        stats.bonusWeaponSizePercent *= _sizeChange;
        stats.bonusWeaponRangePercent *= _sizeChange;

        foreach (var childName in _unscaledChildren)
        {
            var child = stats.transform.Find(childName);
            if (child != null)
                child.localScale /= _sizeChange;
        }

        stats.bonusWeaponSpeedPercent *= (1f + _attackSpeedPercentBonus);
        stats.bonusMoveSpeedPercent *= (1f + _moveSpeedPercentBonus);

        Destroy(gameObject);
    }
}
