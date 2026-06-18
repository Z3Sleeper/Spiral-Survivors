using UnityEngine;

public class RegenerativeStrikesAugment : MonoBehaviour
{
    [SerializeField] private float _regenMultiplier = 3f;
    [SerializeField] private GameObject _vfxPrefab;
    [SerializeField] private float _vfxSize = 1f;
    [SerializeField] private float _vfxDuration = 0.3f;

    private const string VFXTag = "RegenerativeStrikes";
    private CharacterStats _stats;

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null) { Destroy(gameObject); return; }

        CharacterStats.OnVFXBroadcast += OnVFX;

        if (!_stats.isOwner) return;
        WeaponBase.OnHit += OnHit;
    }

    private void OnDestroy()
    {
        CharacterStats.OnVFXBroadcast -= OnVFX;
        WeaponBase.OnHit -= OnHit;
    }

    private void OnHit(CharacterStats source, EnemyBase enemy, float effectiveness)
    {
        if (source != _stats) return;
        float bonus = _regenMultiplier * _stats.HealthRegen * _stats.OnHitEffectiveness * effectiveness;
        enemy.TakeDamageRpc(bonus);
        _stats.BroadcastVFXRpc(enemy.transform.position, _vfxSize, _vfxDuration, VFXTag);
    }

    private void OnVFX(CharacterStats source, Vector3 position, float size, float duration, string vfxTag)
    {
        if (source != _stats || vfxTag != VFXTag || _vfxPrefab == null) return;
        var vfx = Instantiate(_vfxPrefab, position, Quaternion.identity);
        vfx.transform.localScale = Vector3.one * size;
        Destroy(vfx, duration);
    }
}
