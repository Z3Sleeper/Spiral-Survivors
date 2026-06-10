using System.Collections.Generic;
using UnityEngine;

public class WeaponBlade : WeaponBase
{
    private Dictionary<EnemyBase, float> _hitTimestamps = new Dictionary<EnemyBase, float>();

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
    }

    private void OnTriggerEnter(Collider other) => TryHit(other);

    private void TryHit(Collider other)
    {
        if (_stats == null) return;
        if (_stats.IsDead) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("EnemyHurtbox")) return;
        var enemy = other.GetComponentInParent<EnemyBase>();
        if (enemy == null) return;

        float cooldown = 90f / _stats.WeaponSpeed;
        if (_hitTimestamps.TryGetValue(enemy, out float lastHit) && Time.time - lastHit < cooldown) return;

        _hitTimestamps[enemy] = Time.time;

        // Calculate Crit
        float damage = _stats.WeaponDamage;
        if (Random.value < _stats.CritChance)
            damage *= _stats.CritDamage;

        enemy.TakeDamageRpc(damage);

        SFXManager.instance?.Play(_stats.hitSound, enemy.transform.position);
    }
}