using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class WeaponArrow : NetworkIdentity
{
    [SerializeField] private Vector3 arrowHalfExtents = new(0.1f, 0.1f, 0.25f);

    private float _speed;
    private float _damage;
    private float _maxRange;
    private float _critChance;
    private float _critDamage;
    private Vector3 _direction;
    private float _distanceTravelled;
    private int _ricochetsRemaining;
    private int _piercesRemaining;
    private float _damageDropoff;
    private readonly HashSet<EnemyBase> _hitEnemies = new();
    private AudioClip _hitSound;
    private Vector3 _prevPosition;
    private int _enemyHurtboxMask;
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];
    private bool _destroyed;
    private CharacterStats _ownerStats;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        enabled = isOwner;
    }

    public void Init(float damage, float maxRange, Vector3 direction, float speed, float critChance, float critDamage, int pierce, int ricochet, AudioClip hitSound = null, CharacterStats ownerStats = null, float damageDropoff = 0f)
    {
        _ownerStats = ownerStats;
        _damage = damage;
        _damageDropoff = damageDropoff;
        _maxRange = maxRange;
        _direction = direction.normalized;
        _speed = speed;
        _critChance = critChance;
        _critDamage = critDamage;
        _piercesRemaining = pierce;
        _ricochetsRemaining = ricochet;
        _hitSound = hitSound;
        _prevPosition = transform.position;
    }

    private void Update()
    {
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
                if (enemy == null || _hitEnemies.Contains(enemy)) continue;
                ProcessHit(enemy);
                if (_destroyed) break;
            }
        }

        _prevPosition = transform.position;
    }

    private void ProcessHit(EnemyBase enemy)
    {
        _hitEnemies.Add(enemy);

        float damage = _damage;
        if (Random.value < _critChance)
            damage *= _critDamage;

        enemy.TakeDamageRpc(damage);
        _damage *= 1f - _damageDropoff;
        if (_ownerStats != null) WeaponBase.RaiseOnStrike(_ownerStats, enemy);
        SFXManager.instance?.Play(_hitSound, transform.position);

        if (_ricochetsRemaining > 0)
        {
            var target = FindNearestUnhitEnemy();
            if (target != null)
            {
                _direction = (target.transform.position - transform.position).normalized;
                transform.forward = _direction;
                _distanceTravelled = 0f;
                _ricochetsRemaining--;
                return;
            }
        }

        if (_piercesRemaining > 0)
        {
            _piercesRemaining--;
            return;
        }

        Destroy(gameObject);
        _destroyed = true;
    }

    private EnemyBase FindNearestUnhitEnemy()
    {
        EnemyBase nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var enemy in FindObjectsByType<EnemyBase>(FindObjectsSortMode.None))
        {
            if (enemy._isDead || _hitEnemies.Contains(enemy)) continue;
            float dist = Vector3.Distance(transform.position, enemy.transform.position);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = enemy;
            }
        }
        return nearest;
    }
}