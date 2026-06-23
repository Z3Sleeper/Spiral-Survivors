using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class PhantomBlade : NetworkIdentity
{
    private const float Speed = 10f;
    private const float MaxRange = 6f;
    private const float DamageMultiplier = 0.5f;
    private static readonly Vector3 HalfExtents = new(0.1f, 0.1f, 0.5f);

    private float _damage;
    private float _distanceTravelled;
    private readonly HashSet<EnemyBase> _hitEnemies = new();
    private readonly RaycastHit[] _results = new RaycastHit[16];
    private int _enemyHurtboxMask;
    private Vector3 _prevPosition;
    public void Init(CharacterStats owner, float onHitEffectiveness = 1f)
    {
        _damage = owner.WeaponDamage * DamageMultiplier * onHitEffectiveness;
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        _prevPosition = transform.position;
    }

    private void Update()
    {
        if (!isOwner) return;

        float step = Speed * Time.deltaTime;
        transform.position += transform.forward * step;
        _distanceTravelled += step;

        if (_distanceTravelled >= MaxRange)
        {
            Destroy(gameObject);
            return;
        }

        Vector3 delta = transform.position - _prevPosition;
        float dist = delta.magnitude;
        if (dist > 0.001f)
        {
            int count = Physics.BoxCastNonAlloc(_prevPosition, HalfExtents, delta / dist, _results, transform.rotation, dist, _enemyHurtboxMask);
            for (int i = 0; i < count; i++)
            {
                var enemy = _results[i].collider.GetComponentInParent<EnemyBase>();
                if (enemy == null || enemy._isDead || !_hitEnemies.Add(enemy)) continue;
                enemy.TakeDamageRpc(_damage);
            }
        }

        _prevPosition = transform.position;
    }
}
