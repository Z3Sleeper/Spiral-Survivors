using System.Collections.Generic;
using UnityEngine;

public class WeaponBlade : WeaponBase
{
    [SerializeField] private Vector3 bladeHalfExtents = new(0.1f, 0.1f, 0.5f);
    private int _enemyHurtboxMask;
    private readonly RaycastHit[] _sweepResults = new RaycastHit[32];
    private readonly Collider[] _overlapResults = new Collider[32];
    private HashSet<EnemyBase> _currentOverlaps = new();
    private HashSet<EnemyBase> _newOverlaps = new();
    private Vector3 _prevPosition;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        _prevPosition = transform.position;
        enabled = isOwner;
    }

    private void Update()
    {
        if (_stats == null || _stats.IsDead) return;

        Vector3 scaledHalfExtents = Vector3.Scale(bladeHalfExtents, transform.lossyScale);
        _newOverlaps.Clear();

        Vector3 delta = transform.position - _prevPosition;
        float distance = delta.magnitude;

        if (distance > 0.001f)
        {
            int count = Physics.BoxCastNonAlloc(_prevPosition, scaledHalfExtents, delta / distance, _sweepResults, transform.rotation, distance, _enemyHurtboxMask);
            for (int i = 0; i < count; i++)
            {
                var enemy = _sweepResults[i].collider.GetComponentInParent<EnemyBase>();
                if (enemy != null) _newOverlaps.Add(enemy);
            }
        }

        int overlapCount = Physics.OverlapBoxNonAlloc(transform.position, scaledHalfExtents, _overlapResults, transform.rotation, _enemyHurtboxMask);
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
        InvokeOnHit(enemy);
        SFXManager.instance?.Play(_stats.hitSound, enemy.transform.position);
    }
}