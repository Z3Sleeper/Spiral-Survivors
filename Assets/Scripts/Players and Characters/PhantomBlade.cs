using System;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class PhantomBlade : NetworkIdentity
{
    private const float BaseSpeed      = 10f;
    private const float BaseMaxRange   = 6f;
    private const float BaseSpawnRadius = 3f;
    private static readonly Vector3 BaseHalfExtents = new(0.1f, 0.1f, 0.5f);

    [NonSerialized] public float size = 1f;
    [NonSerialized] public float sizeMultiplier = 1f;
    [NonSerialized] public float damageMultiplier = 1f;
    [NonSerialized] public float speed = BaseSpeed;
    [NonSerialized] public float spawnRadius = BaseSpawnRadius;
    [NonSerialized] public float maxRange = BaseMaxRange;
    [NonSerialized] public Vector3 halfExtents = BaseHalfExtents;

    public static event Action<PhantomBlade> OnSummon;
    public static event Action<PhantomBlade> OnLateSummon;
    public static event Action<CharacterStats, EnemyBase, float> OnBladeHit;

    [NonSerialized] public float baseDamage;
    [NonSerialized] public float damage;
    [NonSerialized] public EnemyBase target;
    [NonSerialized] public bool isSecondary;
    [NonSerialized] public CharacterStats ownerStats;
    private float _distanceTravelled;
    private readonly HashSet<EnemyBase> _hitEnemies = new();
    private readonly RaycastHit[] _results = new RaycastHit[16];
    private int _enemyHurtboxMask;
    private Vector3 _prevPosition;

    public void Init(CharacterStats owner, float baseDamageMultiplier, float onHitEffectiveness = 1f)
    {
        ownerStats = owner;
        baseDamage = owner.UnpenalizedWeaponDamage * baseDamageMultiplier * onHitEffectiveness;
        damage = baseDamage;
        if (!isSecondary) size += 0.5f * baseDamageMultiplier;
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        OnSummon?.Invoke(this);
        OnLateSummon?.Invoke(this);
        damage *= damageMultiplier;
        ApplySize();
        _prevPosition = transform.position;
    }

    private void ApplySize()
    {
        float finalSize = size * sizeMultiplier;
        transform.localScale = Vector3.one * finalSize;
        halfExtents = BaseHalfExtents * finalSize;
        speed = BaseSpeed * finalSize;
        maxRange = BaseMaxRange * finalSize;
        transform.position -= transform.forward * ((finalSize - 1f) * spawnRadius);
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
                OnBladeHit?.Invoke(ownerStats, enemy, damage / baseDamage);
            }
        }

        _prevPosition = transform.position;
    }
}
