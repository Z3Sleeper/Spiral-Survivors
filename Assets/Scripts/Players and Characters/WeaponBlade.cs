using System.Collections.Generic;
using UnityEngine;

public class WeaponBlade : WeaponBase
{
    [SerializeField] private Vector3 bladeHalfExtents = new(0.1f, 0.1f, 0.5f);
    private int _enemyHurtboxMask;
    private int _strikeableMask;
    private int _playerHurtboxMask;
    private readonly RaycastHit[] _sweepResults = new RaycastHit[32];
    private readonly Collider[] _overlapResults = new Collider[32];
    private readonly RaycastHit[] _strikeableSweepResults = new RaycastHit[16];
    private readonly Collider[] _strikeableOverlapResults = new Collider[16];
    private readonly RaycastHit[] _allySweepResults = new RaycastHit[8];
    private readonly Collider[] _allyOverlapResults = new Collider[8];
    private HashSet<EnemyBase> _currentOverlaps = new();
    private HashSet<EnemyBase> _newOverlaps = new();
    private HashSet<IStrikeable> _currentStrikeables = new();
    private HashSet<IStrikeable> _newStrikeables = new();
    private HashSet<CharacterStats> _currentAllyOverlaps = new();
    private HashSet<CharacterStats> _newAllyOverlaps = new();
    private Vector3 _prevPosition;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        _strikeableMask = LayerMask.GetMask("Strikeable");
        _playerHurtboxMask = LayerMask.GetMask("PlayerHurtbox");
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
        _newAllyOverlaps.Clear();

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

            if (_stats.canContactAllies)
            {
                int aCount = Physics.BoxCastNonAlloc(_prevPosition, scaledHalfExtents, delta / distance, _allySweepResults, hb.rotation, distance, _playerHurtboxMask);
                for (int i = 0; i < aCount; i++)
                {
                    var ally = _allySweepResults[i].collider.GetComponentInParent<CharacterStats>();
                    if (ally != null && ally != _stats) _newAllyOverlaps.Add(ally);
                }
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

        if (_stats.canContactAllies)
        {
            int aOverlapCount = Physics.OverlapBoxNonAlloc(hb.position, scaledHalfExtents, _allyOverlapResults, hb.rotation, _playerHurtboxMask);
            for (int i = 0; i < aOverlapCount; i++)
            {
                var ally = _allyOverlapResults[i].GetComponentInParent<CharacterStats>();
                if (ally != null && ally != _stats) _newAllyOverlaps.Add(ally);
            }
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

        foreach (var ally in _newAllyOverlaps)
            if (!_currentAllyOverlaps.Contains(ally))
                HitAlly(ally);

        (_currentOverlaps, _newOverlaps) = (_newOverlaps, _currentOverlaps);
        (_currentStrikeables, _newStrikeables) = (_newStrikeables, _currentStrikeables);
        (_currentAllyOverlaps, _newAllyOverlaps) = (_newAllyOverlaps, _currentAllyOverlaps);
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

    private void HitAlly(CharacterStats ally)
    {
        if (ally == null || ally.IsDead) return;
        WeaponBase.RaiseOnAllyHit(_stats, ally);
        if (!_stats.canDamageAllies) return;
        float damage = _stats.WeaponDamage;
        bool isCrit = Random.value < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage;
        ally.TakeDamageRpc(damage);
    }
}