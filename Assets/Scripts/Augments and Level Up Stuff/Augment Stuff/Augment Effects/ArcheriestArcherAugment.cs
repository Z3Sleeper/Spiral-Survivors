using UnityEngine;

public class ArcheriestArcherAugment : MonoBehaviour
{
    [SerializeField] private float _bonusRangePercent = 0.25f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusWeaponRangePercent += _bonusRangePercent;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.bonusWeaponRangePercent -= _bonusRangePercent;
    }
}
