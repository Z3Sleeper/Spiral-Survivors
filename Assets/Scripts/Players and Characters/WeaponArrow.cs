using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class WeaponArrow : NetworkIdentity
{
    private float _speed;
    private float _damage;
    private float _maxRange;
    private float _critChance;
    private float _critDamage;
    private Vector3 _direction;
    private float _distanceTravelled;
    private int _ricochetsRemaining;
    private int _piercesRemaining;
    private readonly HashSet<EnemyBase> _hitEnemies = new();
    private AudioClip _hitSound;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
    }

    public void Init(float damage, float maxRange, Vector3 direction, float speed, float critChance, float critDamage, int pierce, int ricochet, AudioClip hitSound = null)
    {
        _damage = damage;
        _maxRange = maxRange;
        _direction = direction.normalized;
        _speed = speed;
        _critChance = critChance;
        _critDamage = critDamage;
        _piercesRemaining = pierce;
        _ricochetsRemaining = ricochet;
        _hitSound = hitSound;
    }

    private void Update()
    {
        float step = _speed * Time.deltaTime;
        transform.position += _direction * step;
        _distanceTravelled += step;

        if (_distanceTravelled >= _maxRange)
            Destroy(gameObject);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.layer != LayerMask.NameToLayer("EnemyHurtbox")) return;

        var enemy = other.GetComponentInParent<EnemyBase>();
        if (enemy == null || _hitEnemies.Contains(enemy)) return;

        _hitEnemies.Add(enemy);

        float damage = _damage;
        if (Random.value < _critChance)
            damage *= _critDamage;

        enemy.TakeDamageRpc(damage);
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