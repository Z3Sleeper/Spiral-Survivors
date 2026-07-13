using UnityEngine;

public class MultiShotSpreadAugment : MonoBehaviour
{
    [SerializeField] private float _spreadReduction = 0.8f;
    [SerializeField] private float _knockbackMulti = 1f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.multishotSpreadAngle *= 1f - _spreadReduction;
        _stats.knockbackMultiplier += _knockbackMulti;
    }

    private void OnDestroy()
    {
        if (_stats == null) return;
        _stats.multishotSpreadAngle /= 1f - _spreadReduction;
        _stats.knockbackMultiplier -= _knockbackMulti;
    }
}
