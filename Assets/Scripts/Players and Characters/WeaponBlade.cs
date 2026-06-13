using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
public class WeaponBlade : WeaponBase
{
    private BoxCollider _box;
    private int _enemyHurtboxMask;
    private readonly RaycastHit[] _sweepResults = new RaycastHit[32];
    private readonly Collider[] _overlapResults = new Collider[32];
    private HashSet<EnemyBase> _currentOverlaps = new();
    private HashSet<EnemyBase> _newOverlaps = new();
    private Vector3 _prevPosition;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _box = GetComponent<BoxCollider>();
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        _prevPosition = transform.position;
        enabled = isOwner;
    }

    private void Update()
    {
        if (_stats == null || _stats.IsDead) return;

        float sweepRadius = Vector3.Scale(_box.size * 0.5f, transform.lossyScale).magnitude;
        _newOverlaps.Clear();

        Vector3 delta = transform.position - _prevPosition;
        float distance = delta.magnitude;

        if (distance > 0.001f)
        {
            int count = Physics.SphereCastNonAlloc(_prevPosition, sweepRadius, delta / distance, _sweepResults, distance, _enemyHurtboxMask);
            for (int i = 0; i < count; i++)
            {
                var enemy = _sweepResults[i].collider.GetComponentInParent<EnemyBase>();
                if (enemy != null) _newOverlaps.Add(enemy);
            }
        }

        // Catch enemies already overlapping at current position (SphereCast misses these)
        int overlapCount = Physics.OverlapSphereNonAlloc(transform.position, sweepRadius, _overlapResults, _enemyHurtboxMask);
        for (int i = 0; i < overlapCount; i++)
        {
            var enemy = _overlapResults[i].GetComponentInParent<EnemyBase>();
            if (enemy != null) _newOverlaps.Add(enemy);
        }

        foreach (var enemy in _newOverlaps)
            if (!_currentOverlaps.Contains(enemy))
                Hit(enemy);

        (_currentOverlaps, _newOverlaps) = (_newOverlaps, _currentOverlaps);
        _prevPosition = transform.position;
    }

    private void Hit(EnemyBase enemy)
    {
        float damage = _stats.WeaponDamage;
        if (Random.value < _stats.CritChance)
            damage *= _stats.CritDamage;
        enemy.TakeDamageRpc(damage);
        SFXManager.instance?.Play(_stats.hitSound, enemy.transform.position);
    }
}