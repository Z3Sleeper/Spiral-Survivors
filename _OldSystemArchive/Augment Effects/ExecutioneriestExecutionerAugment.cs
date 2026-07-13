using UnityEngine;

public class ExecutioneriestExecutionerAugment : MonoBehaviour
{
    [SerializeField] private float _bonusCritChancePercent = 0.25f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusCritChancePercent += _bonusCritChancePercent;
    }

    private void OnDestroy()
    {
        if (_stats != null) _stats.bonusCritChancePercent -= _bonusCritChancePercent;
    }
}
