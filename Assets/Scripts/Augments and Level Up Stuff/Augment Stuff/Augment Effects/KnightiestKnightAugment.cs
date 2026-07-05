using UnityEngine;

public class KnightiestKnightAugment : MonoBehaviour
{
    [SerializeField] private float _bonusMaxHealthPercent = 0.25f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.AddMaxHealthPercent(_bonusMaxHealthPercent);
    }

    private void OnDestroy()
    {
        if (_stats != null && _stats.isSpawned) _stats.AddMaxHealthPercent(-_bonusMaxHealthPercent);
    }
}
