using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class WeaponBlade : NetworkIdentity
{
    private CharacterStats _stats;
    private Dictionary<EnemyBase, float> _hitCooldowns = new Dictionary<EnemyBase, float>();

    protected override void OnSpawned()
    {
        base.OnSpawned();

        enabled = isOwner;
    }

    public void Init(CharacterStats stats)
    {
        _stats = stats;
    }

    private void Update()
    {
        var keys = new List<EnemyBase>(_hitCooldowns.Keys);
        foreach (var enemy in keys)
        {
            _hitCooldowns[enemy] -= Time.deltaTime;
            if (_hitCooldowns[enemy] <= 0)
                _hitCooldowns.Remove(enemy);
        }
    }

    private void OnTriggerEnter(Collider other) => TryHit(other);
    private void OnTriggerStay(Collider other) => TryHit(other);

    private void TryHit(Collider other)
    {
        if (_stats == null) return;
        if (_stats.isDead) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("EnemyHurtbox")) return;
        var enemy = other.GetComponentInParent<EnemyBase>();
        if (enemy == null) return;
        if (_hitCooldowns.ContainsKey(enemy)) return;

        _hitCooldowns[enemy] = 90f / _stats.weaponSpeed;
        enemy.TakeDamageRpc(_stats.weaponDamage);
    }
}