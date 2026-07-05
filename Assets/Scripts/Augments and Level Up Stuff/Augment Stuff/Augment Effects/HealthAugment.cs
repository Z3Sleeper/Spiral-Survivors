using UnityEngine;

public class HealthAugment : MonoBehaviour
{
    [SerializeField] private float _healthBonus = 50f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.AddMaxHealthFlat(_healthBonus);
    }

    private void OnDestroy()
    {
        if (_stats != null && _stats.isSpawned) _stats.AddMaxHealthFlat(-_healthBonus);
    }
}
