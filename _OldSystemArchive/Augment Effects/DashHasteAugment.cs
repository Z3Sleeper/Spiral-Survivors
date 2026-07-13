using UnityEngine;

public class DashHasteAugment : MonoBehaviour
{
    [SerializeField] private float _dashHasteBonus = 100f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.dashHaste += _dashHasteBonus;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.dashHaste -= _dashHasteBonus;
    }
}
