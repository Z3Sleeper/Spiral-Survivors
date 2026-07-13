using UnityEngine;

public class Ricochetx2Augment : MonoBehaviour
{
    [SerializeField] private float _dropoffReduction = 0.5f;

    private CharacterStats _stats;

    private void Start()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null || !_stats.isOwner) { Destroy(gameObject); return; }
        _stats.bonusRicochetPercent += 1f;
        _stats.projectileDamageDropoffReduction *= 1f - _dropoffReduction;
    }

    private void OnDestroy()
    {
        if (_stats == null) return;
        _stats.bonusRicochetPercent -= 1f;
        _stats.projectileDamageDropoffReduction /= 1f - _dropoffReduction;
    }
}
