using UnityEngine;

public class GremlinAugment : MonoBehaviour
{
    [SerializeField] private float _sizeChange = 0.5f;
    [SerializeField] private float _attackSpeedPercentBonus = 1f;
    [SerializeField] private float _moveSpeedPercentBonus = 0.25f;

    private static readonly string[] _unscaledChildren = { "ReviveZone", "WorldspaceUI", "Minimap Icon" };

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }

        _stats.transform.localScale *= _sizeChange;
        _stats.bonusWeaponRadiusPercent *= _sizeChange;
        _stats.bonusWeaponSizePercent *= _sizeChange;

        foreach (var childName in _unscaledChildren)
        {
            var child = _stats.transform.Find(childName);
            if (child != null)
                child.localScale /= _sizeChange;
        }

        _stats.attackSpeedTotalPercent += _attackSpeedPercentBonus;
        _stats.bonusMoveSpeedPercent += _moveSpeedPercentBonus;
    }

    private void OnDestroy()
    {
        if (_stats == null) return;
        _stats.transform.localScale /= _sizeChange;
        _stats.bonusWeaponRadiusPercent /= _sizeChange;
        _stats.bonusWeaponSizePercent /= _sizeChange;

        foreach (var childName in _unscaledChildren)
        {
            var child = _stats.transform.Find(childName);
            if (child != null)
                child.localScale *= _sizeChange;
        }

        _stats.attackSpeedTotalPercent -= _attackSpeedPercentBonus;
        _stats.bonusMoveSpeedPercent -= _moveSpeedPercentBonus;
    }
}
