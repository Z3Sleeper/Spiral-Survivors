using System;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class PhantomBlade : NetworkIdentity
{
    private const float DamageMultiplier = 0.25f;
    [NonSerialized] public float speed = 10f;
    [NonSerialized] public float spawnRadius = 3f;
    [NonSerialized] public float maxRange = 6f;
    [NonSerialized] public Vector3 halfExtents = new(0.1f, 0.1f, 0.5f);

    public static event Action<PhantomBlade> OnSummon;

    [NonSerialized] public float baseDamage;
    [NonSerialized] public float damage;
    private float _distanceTravelled;
    private readonly HashSet<EnemyBase> _hitEnemies = new();
    private readonly RaycastHit[] _results = new RaycastHit[16];
    private int _enemyHurtboxMask;
    private Vector3 _prevPosition;
    public void Init(CharacterStats owner, float onHitEffectiveness = 1f)
    {
        baseDamage = owner.WeaponDamage * DamageMultiplier * onHitEffectiveness;
        damage = baseDamage;
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        OnSummon?.Invoke(this);
        _prevPosition = transform.position;
    }

    private void Update()
    {
        if (!isOwner) return;

        float step = speed * Time.deltaTime;
        transform.position += transform.forward * step;
        _distanceTravelled += step;

        if (_distanceTravelled >= maxRange)
        {
            Destroy(gameObject);
            return;
        }

        Vector3 delta = transform.position - _prevPosition;
        float dist = delta.magnitude;
        if (dist > 0.001f)
        {
            int count = Physics.BoxCastNonAlloc(_prevPosition, halfExtents, delta / dist, _results, transform.rotation, dist, _enemyHurtboxMask);
            for (int i = 0; i < count; i++)
            {
                var enemy = _results[i].collider.GetComponentInParent<EnemyBase>();
                if (enemy == null || enemy._isDead || !_hitEnemies.Add(enemy)) continue;
                enemy.TakeDamageRpc(damage);
            }
        }

        _prevPosition = transform.position;
    }
}
