using System.Collections.Generic;
using UnityEngine;

public class DashExplosionAugment : MonoBehaviour
{
    [SerializeField] private float _damagePercent = 5f;
    [SerializeField] private float _onHitEffectiveness = 5f;
    [SerializeField] private float _radius = 5f;
    [SerializeField] private GameObject _vfxPrefab;
    [SerializeField] private float _vfxDuration = 0.3f;

    private const string VFXTag = "DashExplosion";
    private CharacterStats _stats;
    private int _enemyHurtboxMask;
    private readonly Collider[] _overlapResults = new Collider[32];

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStats>();
        if (_stats == null) { Destroy(gameObject); return; }

        CharacterStats.OnVFXBroadcast += OnVFX;

        if (!_stats.isOwner) return;
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        PlayerController.OnDashEnded += OnDashEnded;
    }

    private void OnDestroy()
    {
        CharacterStats.OnVFXBroadcast -= OnVFX;
        PlayerController.OnDashEnded -= OnDashEnded;
    }

    private void OnDashEnded(PlayerController controller)
    {
        if (controller.GetComponent<CharacterStats>() != _stats) return;

        int count = Physics.OverlapSphereNonAlloc(_stats.transform.position, _radius, _overlapResults, _enemyHurtboxMask);
        var hitEnemies = new HashSet<EnemyBase>();

        for (int i = 0; i < count; i++)
        {
            var enemy = _overlapResults[i].GetComponentInParent<EnemyBase>();
            if (enemy == null || enemy._isDead || hitEnemies.Contains(enemy)) continue;
            hitEnemies.Add(enemy);
            enemy.TakeDamageRpc(_stats.UnpenalizedWeaponDamage * _damagePercent);
            WeaponBase.RaiseOnHit(_stats, enemy, _onHitEffectiveness);
        }

        _stats.BroadcastVFXRpc(
            new Vector3(_stats.transform.position.x, 0f, _stats.transform.position.z),
            _radius * 2f,
            _vfxDuration,
            VFXTag);
    }

    private void OnVFX(CharacterStats source, Vector3 position, float size, float duration, string vfxTag)
    {
        if (source != _stats || vfxTag != VFXTag || _vfxPrefab == null) return;
        var vfx = Instantiate(_vfxPrefab, position, Quaternion.identity);
        vfx.transform.localScale = new Vector3(size, vfx.transform.localScale.y, size);
        Destroy(vfx, duration);
    }
}
