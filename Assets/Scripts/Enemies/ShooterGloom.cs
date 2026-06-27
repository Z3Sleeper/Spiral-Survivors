using PurrNet;
using UnityEngine;

public class ShooterGloom : EnemyBase
{
    private static readonly int ShooterShootHash = Animator.StringToHash("ShooterShoot");
    private Animator _animator;

    [Header("Shooter Settings")]
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField] private float spawnOffset = 0.75f;
    [SerializeField] private float attackCooldown = 5f;
    [SerializeField] private float minAttackRange = 3f;
    [SerializeField] private float dashSpeedMultiplier = 33f;

    public enum State { Moving, Dashing, Windup, PostShot }
    private float _stateTimer = float.MaxValue;
    private float _attackCooldownTimer = float.MaxValue;
    private Vector3 _dashTarget;
    [SerializeField] private float windupDuration = 0.5f;
    [SerializeField] private float postShotPause = 0.3f;
    [SerializeField] private int shotsPerAttack = 1;

    [Header("Projectile Settings")]
    [SerializeField] private float speed = 8f;
    [SerializeField] private float maxRange = 30f;
    [SerializeField] private int multishot = 0;
    [SerializeField] private float shotSpreadAngle = 60f;

    private int _shotsRemaining;

    [HideInInspector] public SyncVar<State> _syncState = new(State.Moving);
    private State _state
    {
        get => _syncState.value;
        set => _syncState.value = value;
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _syncState.onChanged += OnStateChanged;
        OnStateChanged(_syncState.value);
        TryGetComponent(out _animator);
        _shotsRemaining = shotsPerAttack;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, attackCooldown);
    }

    private void OnStateChanged(State state)
    {
        if (state == State.Windup)
            _animator.Play(ShooterShootHash, 0, 0f);
    }

    protected override void Update()
    {
        base.Update();
        if (!isServer) return;
        if (_isDead) return;

        _stateTimer -= Time.deltaTime;
        _attackCooldownTimer -= Time.deltaTime;

        switch (_state)
        {
            case State.Moving:
                if (TargetInRange() && _attackCooldownTimer <= 0f)
                {
                    if (TargetTooClose())
                        EnterDash();
                    else
                        EnterWindup();
                }
                break;

            case State.Dashing:
                if (Vector3.Distance(transform.position, _dashTarget) < 0.2f)
                {
                    _physicsCollider.enabled = true;
                    _rb.linearVelocity = new Vector3(0f, _rb.linearVelocity.y, 0f);
                    EnterWindup();
                }
                break;

            case State.Windup:
                if (_stateTimer <= 0f)
                    Shoot();
                break;

            case State.PostShot:
                if (_stateTimer <= 0f)
                    if (_shotsRemaining > 0)
                        EnterWindup();
                    else
                        EnterMoving();
                break;
        }
    }

    protected override void FixedUpdate()
    {
        if (_isDead) return;
        if (_state == State.Moving)
            base.FixedUpdate();
        else if (_state == State.Dashing)
        {
            Vector3 dir = (_dashTarget - transform.position);
            dir.y = 0;
            float speed = baseMoveSpeed * dashSpeedMultiplier;
            _rb.linearVelocity = new Vector3(dir.normalized.x * speed, _rb.linearVelocity.y, dir.normalized.z * speed);
        }
        else if (_state == State.Windup)
        {
            if (_target != null)
            {
                Vector3 dir = _target.position - transform.position;
                dir.y = 0;
                if (dir != Vector3.zero)
                    transform.rotation = Quaternion.LookRotation(dir);
            }
        }
        else if (_state == State.PostShot && isServer)
            FaceTargetSmooth();
    }

    private void EnterDash()
    {
        if (_target == null) return;
        float dist = Vector3.Distance(transform.position, _target.position);
        Vector3 awayDir = transform.position - _target.position;
        awayDir.y = 0;
        awayDir.Normalize();
        _dashTarget = transform.position + awayDir * (minAttackRange - dist + 0.5f);
        _physicsCollider.enabled = false;
        _rb.linearVelocity = Vector3.zero;
        _state = State.Dashing;
    }

    private bool TargetTooClose()
    {
        if (_target == null) return false;
        return Vector3.Distance(transform.position, _target.position) < minAttackRange;
    }

    private void EnterWindup()
    {
        SnapFaceTarget();
        _stateTimer = windupDuration;
        _state = State.Windup;
    }

    private void Shoot()
    {
        _state = State.PostShot;
        _stateTimer = postShotPause;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, 0.2f * attackCooldown);

        if (_target == null) return;
        Vector3 baseDir = _target.position - transform.position;
        baseDir.y = 0;
        baseDir.Normalize();

        Vector3 spawnPos = transform.position + transform.forward * spawnOffset;
        Vector3 spawnPosition = new(spawnPos.x, 0f, spawnPos.z);

        int totalShots = 1 + multishot;
        float startAngle = totalShots > 1 ? -shotSpreadAngle / 2f : 0f;
        float angleStep = totalShots > 1 ? shotSpreadAngle / (totalShots - 1) : 0f;

        for (int i = 0; i < totalShots; i++)
        {
            float angle = startAngle + i * angleStep;
            Vector3 dir = Quaternion.Euler(0, angle, 0) * baseDir;
            SpawnProjectileRpc(spawnPosition, dir, damage, speed, maxRange);
        }

        _shotsRemaining--;
    }

    [ObserversRpc]
    private void SpawnProjectileRpc(Vector3 position, Vector3 direction, float dmg, float spd, float range)
    {
        var proj = Instantiate(projectilePrefab, position, Quaternion.LookRotation(direction));
        proj.GetComponent<ShooterGloomProjectile>().Init(dmg, direction, spd, range, this);
    }

    private void EnterMoving()
    {
        _state = State.Moving;
        _shotsRemaining = shotsPerAttack;
    }
}