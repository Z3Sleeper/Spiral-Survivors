using System.Collections.Generic;
using UnityEngine;

public class WeaponBow : WeaponBase
{
    [SerializeField] private GameObject arrowPrefab;
    [SerializeField] private LayerMask enemyHurtboxLayer;
    public float projectileSpeed = 20f;
    public float bowAttackSpeedScaling = 10f;

    private float _fireTimer;
    public override bool IsMelee => false;

    private float InitialHitDuration => 90f / _stats.WeaponSpeed;
    private readonly Dictionary<EnemyBase, float> _initialHitCooldowns = new();
    private readonly List<EnemyBase> _cooldownBuffer = new();
    private EnemyBase _currentTarget;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
    }

    public override void Init(CharacterStats stats)
    {
        base.Init(stats);
        _fireTimer = 0f;
    }

    private void Update()
    {
        if (_stats == null || _stats.IsDead) return;

        _fireTimer -= Time.deltaTime;

        float cooldownDuration = InitialHitDuration;
        _cooldownBuffer.Clear();
        _cooldownBuffer.AddRange(_initialHitCooldowns.Keys);
        foreach (var e in _cooldownBuffer)
        {
            if (Time.time - _initialHitCooldowns[e] >= cooldownDuration)
                _initialHitCooldowns.Remove(e);
        }

        if (_fireTimer > 0f) return;

        Vector3? target = GetTargetPosition();
        if (_stats.continuousFire || target.HasValue)
        {
            if (_currentTarget != null)
                _initialHitCooldowns[_currentTarget] = Time.time;
            Fire(target ?? (transform.position + transform.forward));
            _fireTimer = 1f / (_stats.AttackSpeed * bowAttackSpeedScaling);
        }
    }

    private readonly RaycastHit[] _targetSweep = new RaycastHit[16];

    private Vector3? GetTargetPosition()
    {
        _currentTarget = null;
        var origin = transform.position - transform.forward * 1.0f;
        int count = Physics.RaycastNonAlloc(origin, transform.forward, _targetSweep, _stats.WeaponRange + 1f, enemyHurtboxLayer);

        System.Array.Sort(_targetSweep, 0, count, Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));

        for (int i = 0; i < count; i++)
        {
            var enemy = _targetSweep[i].collider.GetComponentInParent<EnemyBase>();
            if (enemy != null && (!_initialHitCooldowns.TryGetValue(enemy, out float hitTime) || Time.time - hitTime >= InitialHitDuration))
            {
                _currentTarget = enemy;
                return _targetSweep[i].collider.bounds.center;
            }
        }
        return null;
    }

    private void Fire(Vector3 targetPosition)
    {
        float rawPierce = _stats.Pierce;
        int pierce = (int)rawPierce;
        if (Random.value < rawPierce - pierce) pierce++;

        float rawRicochet = _stats.Ricochet;
        int ricochet = (int)rawRicochet;
        if (Random.value < rawRicochet - ricochet) ricochet++;

        float rawMultishot = _stats.Multishot;
        int bonusShots = (int)rawMultishot;
        if (Random.value < rawMultishot - bonusShots) bonusShots++;
        int multishot = 1 + bonusShots;

        Vector3 baseDir = (targetPosition - transform.position).normalized;
        float startAngle = multishot > 1 ? -_stats.multishotSpreadAngle / 2f : 0f;
        float angleStep = multishot > 1 ? _stats.multishotSpreadAngle / (multishot - 1) : 0f;

        for (int i = 0; i < multishot; i++)
        {
            Vector3 dir = Quaternion.Euler(0, startAngle + i * angleStep, 0) * baseDir;
            var arrow = Instantiate(arrowPrefab, transform.position, Quaternion.LookRotation(dir));
            arrow.transform.localScale *= _stats.WeaponSize;
            arrow.GetComponent<WeaponArrow>().Init(_stats.WeaponDamage, _stats.WeaponRange * 1.5f, dir, projectileSpeed, _stats.CritChance, _stats.CritDamage, pierce, ricochet, _stats.hitSound, _stats, _stats.ProjectileDamageDropoff, _stats.ProjectileOnHitDropoff);
        }
    }
}
