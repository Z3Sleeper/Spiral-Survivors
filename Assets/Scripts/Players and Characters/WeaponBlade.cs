using System.Collections.Generic;
using UnityEngine;

public class WeaponBlade : WeaponBase
{
    [SerializeField] private Vector3 bladeHalfExtents = new(0.1f, 0.1f, 0.5f);
    private int _enemyHurtboxMask;
    private int _strikeableMask;
    private readonly RaycastHit[] _sweepResults = new RaycastHit[32];
    private readonly Collider[] _overlapResults = new Collider[32];
    private readonly RaycastHit[] _strikeableSweepResults = new RaycastHit[16];
    private readonly Collider[] _strikeableOverlapResults = new Collider[16];
    private HashSet<EnemyBase> _currentOverlaps = new();
    private HashSet<EnemyBase> _newOverlaps = new();
    private HashSet<IStrikeable> _currentStrikeables = new();
    private HashSet<IStrikeable> _newStrikeables = new();
    private Vector3 _prevPosition;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        _strikeableMask = LayerMask.GetMask("Strikeable");
        _prevPosition = hitboxTarget != null ? hitboxTarget.position : transform.position;
        enabled = isOwner;
    }

    private void Update()
    {
        if (_stats == null || _stats.IsDead) return;

        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        Vector3 scaledHalfExtents = Vector3.Scale(bladeHalfExtents, hb.lossyScale);
        _newOverlaps.Clear();
        _newStrikeables.Clear();

        Vector3 delta = hb.position - _prevPosition;
        float distance = delta.magnitude;

        if (distance > 0.001f)
        {
            int count = Physics.BoxCastNonAlloc(_prevPosition, scaledHalfExtents, delta / distance, _sweepResults, hb.rotation, distance, _enemyHurtboxMask);
            for (int i = 0; i < count; i++)
            {
                var enemy = _sweepResults[i].collider.GetComponentInParent<EnemyBase>();
                if (enemy != null) _newOverlaps.Add(enemy);
            }

            int sCount = Physics.BoxCastNonAlloc(_prevPosition, scaledHalfExtents, delta / distance, _strikeableSweepResults, hb.rotation, distance, _strikeableMask);
            for (int i = 0; i < sCount; i++)
            {
                var strikeable = _strikeableSweepResults[i].collider.GetComponentInParent<IStrikeable>();
                if (strikeable != null) _newStrikeables.Add(strikeable);
            }
        }

        int overlapCount = Physics.OverlapBoxNonAlloc(hb.position, scaledHalfExtents, _overlapResults, hb.rotation, _enemyHurtboxMask);
        for (int i = 0; i < overlapCount; i++)
        {
            var enemy = _overlapResults[i].GetComponentInParent<EnemyBase>();
            if (enemy != null) _newOverlaps.Add(enemy);
        }

        int sOverlapCount = Physics.OverlapBoxNonAlloc(hb.position, scaledHalfExtents, _strikeableOverlapResults, hb.rotation, _strikeableMask);
        for (int i = 0; i < sOverlapCount; i++)
        {
            var strikeable = _strikeableOverlapResults[i].GetComponentInParent<IStrikeable>();
            if (strikeable != null) _newStrikeables.Add(strikeable);
        }

        foreach (var enemy in _newOverlaps)
            if (!_currentOverlaps.Contains(enemy))
                Hit(enemy);

        foreach (var strikeable in _newStrikeables)
            if (!_currentStrikeables.Contains(strikeable))
            {
                strikeable.OnStruck();
                Vector3 hitPos = strikeable is Component c ? c.transform.position : transform.position;
                SFXManager.instance?.Play(_stats.hitSound, hitPos, 1f, 0.05f);
            }

        (_currentOverlaps, _newOverlaps) = (_newOverlaps, _currentOverlaps);
        (_currentStrikeables, _newStrikeables) = (_newStrikeables, _currentStrikeables);
        _prevPosition = hb.position;
    }

    private void Hit(EnemyBase enemy)
    {
        if (enemy == null || enemy._isDead) return;
        float damage = _stats.WeaponDamage;
        bool isCrit = Random.value < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage;
        enemy.TakeDamageRpc(damage);
        InvokeOnStrike(enemy);
        if (isCrit) WeaponBase.RaiseOnCritStrike(_stats, enemy);
        SFXManager.instance?.Play(_stats.hitSound, enemy.transform.position, 1f, 0.05f);
    }
}