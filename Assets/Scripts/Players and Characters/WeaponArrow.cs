using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class WeaponArrow : NetworkIdentity
{
    [SerializeField] private Vector3 arrowHalfExtents = new(0.1f, 0.1f, 0.25f);
    private float _speed;
    private float _onHitPower = 1f;
    private float _onHitDropOff;
    private float _damage;
    private float _maxRange;
    private float _critChance;
    private float _critDamage;
    private Vector3 _direction;
    private float _distanceTravelled;
    private int _ricochetsRemaining;
    private float _damageDropoff;
    private readonly HashSet<EnemyBase> _hitEnemies = new();
    private readonly HashSet<CharacterStats> _hitAllies = new();
    private readonly HashSet<IStrikeable> _hitStrikeables = new();
    private AudioClip _hitSound;
    private Vector3 _prevPosition;
    private int _enemyHurtboxMask;
    private int _strikeableMask;
    private int _playerHurtboxMask;
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];
    private readonly RaycastHit[] _strikeableResults = new RaycastHit[8];
    private readonly RaycastHit[] _allyResults = new RaycastHit[8];
    private bool _destroyed;
    private bool _isLocalArrow;
    private bool _canContactAllies;
    private bool _canDamageAllies;
    private CharacterStats _ownerStats;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        _strikeableMask = LayerMask.GetMask("Strikeable");
        _playerHurtboxMask = LayerMask.GetMask("PlayerHurtbox");
    }

    public void Init(float damage, float maxRange, Vector3 direction, float speed, float critChance, float critDamage, int ricochet, AudioClip hitSound = null, CharacterStats ownerStats = null, float damageDropoff = 0f, float onHitDropOff = 0f, bool canContactAllies = false, bool canDamageAllies = false)
    {
        _isLocalArrow = true;
        _ownerStats = ownerStats;
        _damage = damage;
        _damageDropoff = damageDropoff;
        _onHitDropOff = onHitDropOff;
        _maxRange = maxRange;
        _direction = direction.normalized;
        _speed = speed;
        _critChance = critChance;
        _critDamage = critDamage;
        _ricochetsRemaining = ricochet;
        _hitSound = hitSound;
        _canContactAllies = canContactAllies;
        _canDamageAllies = canDamageAllies;
        _prevPosition = transform.position;
    }

    private void Update()
    {
        if (!_isLocalArrow) return;
        float step = _speed * Time.deltaTime;
        transform.position += _direction * step;
        _distanceTravelled += step;

        if (_distanceTravelled >= _maxRange)
        {
            Destroy(gameObject);
            return;
        }

        Vector3 delta = transform.position - _prevPosition;
        float dist = delta.magnitude;
        if (dist > 0.001f)
        {
            Vector3 scaledHalfExtents = Vector3.Scale(arrowHalfExtents, transform.lossyScale);
            int hitCount = Physics.BoxCastNonAlloc(_prevPosition, scaledHalfExtents, delta / dist, _sweepResults, transform.rotation, dist, _enemyHurtboxMask);
            for (int i = 0; i < hitCount; i++)
            {
                var enemy = _sweepResults[i].collider.GetComponentInParent<EnemyBase>();
                if (enemy == null || enemy._isDead || _hitEnemies.Contains(enemy)) continue;
                ProcessHit(enemy);
                if (_destroyed) break;
            }

            if (!_destroyed)
            {
                int sCount = Physics.BoxCastNonAlloc(_prevPosition, scaledHalfExtents, delta / dist, _strikeableResults, transform.rotation, dist, _strikeableMask);
                for (int i = 0; i < sCount; i++)
                {
                    var strikeable = _strikeableResults[i].collider.GetComponentInParent<IStrikeable>();
                    if (strikeable == null || !_hitStrikeables.Add(strikeable)) continue;
                    strikeable.OnStruck();
                    ProcessStrikeableHit();
                    if (_destroyed) break;
                }
            }

            if (!_destroyed && _canContactAllies)
            {
                int aCount = Physics.BoxCastNonAlloc(_prevPosition, scaledHalfExtents, delta / dist, _allyResults, transform.rotation, dist, _playerHurtboxMask);
                for (int i = 0; i < aCount; i++)
                {
                    var ally = _allyResults[i].collider.GetComponentInParent<CharacterStats>();
                    if (ally == null || ally.IsDead || ally == _ownerStats || _hitAllies.Contains(ally)) continue;
                    ProcessAllyHit(ally);
                    if (_destroyed) break;
                }
            }
        }

        _prevPosition = transform.position;
    }

    private void ProcessHit(EnemyBase enemy)
    {
        _hitEnemies.Add(enemy);

        float damage = WeaponBase.RollDamage(_damage, _ownerStats != null ? _ownerStats.LuckChance : 0f);
        bool isCrit = Random.value < _critChance;
        if (isCrit) damage *= _critDamage;

        enemy.TakeDamageRpc(damage, isCrit);
        _damage *= 1f - _damageDropoff;
        if (_ownerStats != null) WeaponBase.RaiseOnStrike(_ownerStats, enemy, _onHitPower, isCrit);
        if (isCrit && _ownerStats != null) WeaponBase.RaiseOnCritStrike(_ownerStats, enemy, _onHitPower);
        _onHitPower *= 1f - _onHitDropOff;
        SFXManager.instance?.Play(_hitSound, transform.position, 1f, 0.05f);

        if (_ricochetsRemaining > 0)
        {
            Vector3? ricochetTarget = FindNearestRicochetTarget();
            if (ricochetTarget.HasValue)
            {
                _direction = (ricochetTarget.Value - transform.position).normalized;
                transform.forward = _direction;
                _distanceTravelled = 0f;
                _ricochetsRemaining--;
            }
        }
    }

    private void ProcessStrikeableHit()
    {
        SFXManager.instance?.Play(_hitSound, transform.position, 1f, 0.05f);

        if (_ricochetsRemaining > 0)
        {
            Vector3? ricochetTarget = FindNearestRicochetTarget();
            if (ricochetTarget.HasValue)
            {
                _direction = (ricochetTarget.Value - transform.position).normalized;
                transform.forward = _direction;
                _distanceTravelled = 0f;
                _ricochetsRemaining--;
            }
        }
    }

    private void ProcessAllyHit(CharacterStats ally)
    {
        _hitAllies.Add(ally);

        if (_ownerStats != null) WeaponBase.RaiseOnAllyStrike(_ownerStats, ally, _onHitPower);

        if (_canDamageAllies)
        {
            float damage = _damage;
            bool isCrit = Random.value < _critChance;
            if (isCrit) damage *= _critDamage;
            ally.TakeDamageRpc(damage);
            _damage *= 1f - _damageDropoff;
            _onHitPower *= 1f - _onHitDropOff;
        }

        SFXManager.instance?.Play(_hitSound, transform.position, 1f, 0.05f);

        if (_ricochetsRemaining > 0)
        {
            Vector3? ricochetTarget = FindNearestRicochetTarget();
            if (ricochetTarget.HasValue)
            {
                _direction = (ricochetTarget.Value - transform.position).normalized;
                transform.forward = _direction;
                _distanceTravelled = 0f;
                _ricochetsRemaining--;
            }
        }
    }

    private Vector3? FindNearestRicochetTarget()
    {
        float nearestDist = float.MaxValue;
        Vector3? nearestPos = null;

        foreach (var enemy in FindObjectsByType<EnemyBase>(FindObjectsSortMode.None))
        {
            if (enemy._isDead || _hitEnemies.Contains(enemy)) continue;
            float dist = Vector3.Distance(transform.position, enemy.transform.position);
            if (dist < nearestDist) { nearestDist = dist; nearestPos = enemy.transform.position; }
        }

        if (_canContactAllies)
        {
            foreach (var ally in CharacterStats.All.Values)
            {
                if (ally.IsDead || ally == _ownerStats || _hitAllies.Contains(ally)) continue;
                float dist = Vector3.Distance(transform.position, ally.transform.position);
                if (dist < nearestDist) { nearestDist = dist; nearestPos = ally.transform.position; }
            }
        }

        return nearestPos;
    }
}