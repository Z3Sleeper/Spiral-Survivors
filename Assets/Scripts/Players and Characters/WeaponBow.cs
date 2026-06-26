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
    private readonly Dictionary<CharacterStats, float> _initialAllyHitCooldowns = new();
    private readonly List<CharacterStats> _allyBuffer = new();
    private EnemyBase _currentTarget;
    private CharacterStats _currentAllyTarget;
    private int _playerHurtboxMask;
    private readonly RaycastHit[] _allyTargetSweep = new RaycastHit[8];

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _playerHurtboxMask = LayerMask.GetMask("PlayerHurtbox");
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

        _allyBuffer.Clear();
        _allyBuffer.AddRange(_initialAllyHitCooldowns.Keys);
        foreach (var a in _allyBuffer)
        {
            if (Time.time - _initialAllyHitCooldowns[a] >= cooldownDuration)
                _initialAllyHitCooldowns.Remove(a);
        }

        if (_fireTimer > 0f) return;

        Vector3? target = GetTargetPosition();
        if (_stats.continuousFire || target.HasValue)
        {
            if (_currentTarget != null)
                _initialHitCooldowns[_currentTarget] = Time.time;
            if (_currentAllyTarget != null)
                _initialAllyHitCooldowns[_currentAllyTarget] = Time.time;
            Fire(target ?? (transform.position + transform.forward));
            _fireTimer = 1f / (_stats.AttackSpeed * bowAttackSpeedScaling);
        }
    }

    private readonly RaycastHit[] _targetSweep = new RaycastHit[16];

    private Vector3? GetTargetPosition()
    {
        _currentTarget = null;
        _currentAllyTarget = null;
        var origin = transform.position - transform.forward * 1.0f;
        float range = _stats.WeaponRange + 1f;

        int count = Physics.RaycastNonAlloc(origin, transform.forward, _targetSweep, range, enemyHurtboxLayer);
        System.Array.Sort(_targetSweep, 0, count, Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));

        EnemyBase foundEnemy = null;
        Vector3 enemyPos = default;
        float enemyDist = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var enemy = _targetSweep[i].collider.GetComponentInParent<EnemyBase>();
            if (enemy != null && (!_initialHitCooldowns.TryGetValue(enemy, out float hitTime) || Time.time - hitTime >= InitialHitDuration))
            {
                foundEnemy = enemy;
                enemyPos = _targetSweep[i].collider.bounds.center;
                enemyDist = _targetSweep[i].distance;
                break;
            }
        }

        if (_stats.canContactAllies)
        {
            int aCount = Physics.RaycastNonAlloc(origin, transform.forward, _allyTargetSweep, range, _playerHurtboxMask);
            System.Array.Sort(_allyTargetSweep, 0, aCount, Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));

            for (int i = 0; i < aCount; i++)
            {
                var ally = _allyTargetSweep[i].collider.GetComponentInParent<CharacterStats>();
                if (ally == null || ally.IsDead) continue;
                if (_initialAllyHitCooldowns.TryGetValue(ally, out float hitTime) && Time.time - hitTime < InitialHitDuration) continue;

                if (_allyTargetSweep[i].distance < enemyDist)
                {
                    _currentAllyTarget = ally;
                    return _allyTargetSweep[i].collider.bounds.center;
                }
                break;
            }
        }

        if (foundEnemy != null)
        {
            _currentTarget = foundEnemy;
            return enemyPos;
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
            arrow.GetComponent<WeaponArrow>().Init(_stats.WeaponDamage, _stats.WeaponRange * 1.5f, dir, projectileSpeed, _stats.CritChance, _stats.CritDamage, pierce, ricochet, _stats.hitSound, _stats, _stats.ProjectileDamageDropoff, _stats.ProjectileOnHitDropoff, _stats.canContactAllies, _stats.canDamageAllies);
        }
    }
}
