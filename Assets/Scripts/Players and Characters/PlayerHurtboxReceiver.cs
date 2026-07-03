using System;
using System.Collections.Generic;
using UnityEngine;

public class PlayerHurtboxReceiver : MonoBehaviour
{
    public static event Action<CharacterStats, EnemyBase> OnDamaged;

    private CharacterStats _stats;
    private readonly Dictionary<EnemyBase, float> _multiHitCooldowns = new();
    private readonly Dictionary<EnemyBase, float> _contactCooldowns = new();
    private readonly List<EnemyBase> _cooldownBuffer = new();
    private readonly float _multiHitCooldown = 0.5f;

    private void Awake()
    {
        _stats = GetComponentInParent<CharacterStats>();
        _contactDamageLayer = LayerMask.NameToLayer("ContactDamage");
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
        if (_stats == null || !_stats.isOwner || _stats.IsDead || _stats.IsInvincible) return;

        var attack = other.GetComponentInParent<IEnemyAttack>();
        if (attack == null || attack is EnemyBase) return; // EnemyBase attackers handled in OnTriggerStay

        var source = attack.Source;
        if (source != null && _multiHitCooldowns.ContainsKey(source)) return;

        float dmg = attack.GetDamage();
        if (dmg > 0f)
        {
            _stats.TakeDamage(dmg);
            if (source != null)
            {
                _multiHitCooldowns[source] = _multiHitCooldown;
                OnDamaged?.Invoke(_stats, source);
            }
        }
    }

    private int _contactDamageLayer;

    private void OnTriggerStay(Collider other)
    {
        if (_stats == null || !_stats.isOwner || _stats.IsDead || _stats.IsInvincible) return;
        if (other.gameObject.layer != _contactDamageLayer) return;

        var enemy = other.GetComponentInParent<EnemyBase>();
        if (enemy == null || enemy._isDead || !enemy.CanContactDamage) return;
        if (_contactCooldowns.ContainsKey(enemy)) return;

        float dmg = enemy.GetDamage();
        if (dmg <= 0f) return;
        _contactCooldowns[enemy] = enemy.contactCooldown;
        _stats.TakeDamage(dmg);
        OnDamaged?.Invoke(_stats, enemy);
    }
}
