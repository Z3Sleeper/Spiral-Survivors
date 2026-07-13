using UnityEngine;

public class TitanAugment : MonoBehaviour
{
    [SerializeField] private float _sizeMultiplier = 1.5f;
    [SerializeField] private float _rangeBonus = 0.5f;
    [SerializeField] private float _healthBonus = 0.2f;

    private static readonly string[] _unscaledChildren = { "ReviveZone", "WorldspaceUI", "Minimap Icon" };

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }

        _stats.transform.localScale *= _sizeMultiplier;
        _stats.bonusWeaponRangePercent += _rangeBonus;
        _stats.bonusWeaponRadiusPercent *= _sizeMultiplier;
        _stats.bonusWeaponSizePercent *= _sizeMultiplier;

        foreach (var childName in _unscaledChildren)
        {
            var child = _stats.transform.Find(childName);
            if (child != null)
                child.localScale /= _sizeMultiplier;
        }

        _stats.AddMaxHealthPercent(_healthBonus);
    }

    private void OnDestroy()
    {
        if (_stats == null) return;
        _stats.transform.localScale /= _sizeMultiplier;
        _stats.bonusWeaponRangePercent -= _rangeBonus;
        _stats.bonusWeaponRadiusPercent /= _sizeMultiplier;
        _stats.bonusWeaponSizePercent /= _sizeMultiplier;

        foreach (var childName in _unscaledChildren)
        {
            var child = _stats.transform.Find(childName);
            if (child != null)
                child.localScale *= _sizeMultiplier;
        }

        if (_stats.isSpawned) _stats.AddMaxHealthPercent(-_healthBonus);
    }
}
