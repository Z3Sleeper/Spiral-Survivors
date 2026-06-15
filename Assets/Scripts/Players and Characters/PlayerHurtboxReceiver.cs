using System.Collections.Generic;
using UnityEngine;

public class PlayerHurtboxReceiver : MonoBehaviour
{
    private CharacterStats _stats;
    private readonly Dictionary<EnemyBase, float> _multiHitCooldowns = new();
    private readonly Dictionary<EnemyBase, float> _contactCooldowns = new();
    private readonly List<EnemyBase> _cooldownBuffer = new();

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStats>();
    }

    private void Update()
    {
        if (_stats == null || !_stats.isOwner) return;
        _cooldownBuffer.Clear();
        _cooldownBuffer.AddRange(_multiHitCooldowns.Keys);
        foreach (var enemy in _cooldownBuffer)
        {
            _multiHitCooldowns[enemy] -= Time.deltaTime;
            if (_multiHitCooldowns[enemy] <= 0f)
                _multiHitCooldowns.Remove(enemy);
        }
        _cooldownBuffer.Clear();
        _cooldownBuffer.AddRange(_contactCooldowns.Keys);
        foreach (var enemy in _cooldownBuffer)
        {
            _contactCooldowns[enemy] -= Time.deltaTime;
            if (_contactCooldowns[enemy] <= 0f)
                _contactCooldowns.Remove(enemy);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (_stats == null || !_stats.isOwner || _stats.IsDead || _stats.invincible) return;

        var attack = other.GetComponentInParent<IEnemyAttack>();
        if (attack == null || attack is EnemyBase) return; // EnemyBase attackers handled in OnTriggerStay

        var source = attack.Source;
        if (source != null && _multiHitCooldowns.ContainsKey(source)) return;

        float dmg = attack.GetDamage();
        if (dmg > 0f)
        {
            _stats.TakeDamageRpc(dmg);
            if (source != null)
                _multiHitCooldowns[source] = 0.5f;
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (_stats == null || !_stats.isOwner || _stats.IsDead || _stats.invincible) return;

        var enemy = other.GetComponentInParent<EnemyBase>();
        if (enemy == null || enemy._isDead) return;
        if (_contactCooldowns.ContainsKey(enemy)) return;

        float dmg = enemy.GetDamage();
        if (dmg <= 0f) return;
        _contactCooldowns[enemy] = enemy.contactCooldown;
        _stats.TakeDamageRpc(dmg);
    }
}
