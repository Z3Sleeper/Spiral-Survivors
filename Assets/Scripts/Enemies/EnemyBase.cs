using System;
using System.Collections;
using PurrNet;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class EnemyBase : NetworkIdentity, IEnemyAttack
{
    protected Rigidbody _rb;
    protected Collider _hurtbox;
    protected Collider _physicsCollider;
    protected Collider _contactDamageCollider;
    protected Animator _animator;

    [Header("Identity")]
    public string enemyName = "Enemy";

    [Header("Stats")]
    public float moveSpeed = 0.6f;
    [NonSerialized] public float baseMoveSpeed;
    public float backpedalSpeedDivisor = 3f;
    public float maxHealth = 30f;
    [NonSerialized] public float baseHealth;
    public float preferredRange = 0f; // 0 = melee, >0 = ranged
    public float rangeToleranceFar = 1f;
    public float rangeToleranceClose = 0.3f;
    public float damage = 10f;
    [NonSerialized] public float baseDamage;
    public float xpValue = 1f;
    [NonSerialized] public float baseXpValue;
    [NonSerialized] public float pointValue;
    [Header("Contact Damage")]
    public bool contactColliderEnabled = true;
    public float contactCooldown = 0.5f;
    private bool _atPreferredRange = false;
    [NonSerialized] public bool _isDead = false;
    public bool stunResistant = false;
    [Range(0f, 1f)] public float knockbackResistance = 0f;
    public bool showDamageNumbers = true;
    [SerializeField] private float _damageNumberPlayerOffset = 0f;
    private PlayerID _killerOwner;
    public bool isStunnable;
    public bool IsStunned { get; private set; }
    private Coroutine _stunCoroutine;
    public EnemyBase Source => this;
    public virtual float GetDamage() => damage;
    public virtual bool CanContactDamage => true;


    public SyncVar<float> _currentHealthSync = new(0);
    public SyncVar<float> _syncMaxHealth = new(0);
    public float CurrentHealth
    {
        get => _currentHealthSync.value;
        set => _currentHealthSync.value = value;
    }

    public static int ActiveCount = 0;
    public static int ActiveNormalCount = 0;
    public static int ActivePlebCount = 0;
    [NonSerialized] public bool isPleb = false;
    [NonSerialized] public bool isUncapped = false;

    public Transform CurrentTarget => _target;

    private float _findTargetTimer = 0;
    protected float FindTargetInterval = 1f;
    protected Transform _target;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();

        var hurtboxChild = transform.Find("Hurtbox");
        if (hurtboxChild != null) _hurtbox = hurtboxChild.GetComponent<Collider>();

        var physicsChild = transform.Find("PhysicsCollider");
        if (physicsChild != null) _physicsCollider = physicsChild.GetComponent<Collider>();

        var contactChild = transform.Find("ContactDamageCollider");
        if (contactChild != null)
        {
            _contactDamageCollider = contactChild.GetComponent<Collider>();
            _contactDamageCollider.enabled = contactColliderEnabled;
        }

        baseHealth = maxHealth;
        baseDamage = damage;
        baseMoveSpeed = moveSpeed;
        baseXpValue = xpValue;
        pointValue = xpValue;
        isStunnable = !stunResistant;

        _findTargetTimer = UnityEngine.Random.Range(0f, FindTargetInterval);
        CacheRenderers();
        _animator = GetComponentInChildren<Animator>();
    }

    private void KillGraph()
    {
        if (_animator == null) return;
        if (_animator.playableGraph.IsValid())
            _animator.playableGraph.Destroy();
        _animator.enabled = false;
    }

    protected override void OnDespawned(bool asServer)
    {
        KillGraph();
        base.OnDespawned(asServer);
    }

    protected override void OnSpawned(bool asServer)
    {
        if (_animator != null) _animator.enabled = true;
        base.OnSpawned(asServer);

        _syncMaxHealth.onChanged -= OnMaxHealthChanged;
        _syncMaxHealth.onChanged += OnMaxHealthChanged;

        if (!asServer) return;
        _syncMaxHealth.value = maxHealth;
        CurrentHealth = maxHealth;
        ActiveCount++;
        if (isPleb) ActivePlebCount++;
        else if (!isUncapped) ActiveNormalCount++;
    }

    protected override void OnDestroy()
    {
        KillGraph();
        base.OnDestroy();
    }

    private void OnApplicationQuit() => KillGraph();

    private void OnMaxHealthChanged(float val) => maxHealth = val;

    public void Stun(float duration)
    {
        if (!isServer || _isDead) return;
        if (!isStunnable) return;
        _rb.linearVelocity = Vector3.zero;
        if (_stunCoroutine != null) StopCoroutine(_stunCoroutine);
        _stunCoroutine = StartCoroutine(StunCoroutine(duration));
    }

    private IEnumerator StunCoroutine(float duration)
    {
        IsStunned = true;
        bool hadContactDamage = _contactDamageCollider != null && _contactDamageCollider.enabled;
        if (_contactDamageCollider != null) _contactDamageCollider.enabled = false;
        yield return new WaitForSeconds(duration);
        if (_contactDamageCollider != null) _contactDamageCollider.enabled = hadContactDamage;
        IsStunned = false;
        _stunCoroutine = null;
    }

    protected virtual void Update()
    {
        if (_isDead) return;
        if (!isServer) return;
        if (IsStunned) return;
        _findTargetTimer -= Time.deltaTime;
        if (_findTargetTimer <= 0f)
        {
            _findTargetTimer = FindTargetInterval;
            FindTarget();
        }

    }

    protected virtual void FixedUpdate()
    {
        if (_isDead) return;
        if (!isServer) return;
        if (IsStunned) { _rb.linearVelocity = Vector3.zero; return; }
        if (_target == null)
        {
            FindTarget();
            return;
        }
        HandleMovement();
    }

    protected virtual void FindTarget()
    {
        float nearest = float.MaxValue;
        foreach (var p in CharacterStats.All.Values)
        {
            if (p.IsDead) continue;
            float dist = Vector3.Distance(transform.position, p.transform.position);
            if (dist < nearest)
            {
                nearest = dist;
                _target = p.transform;
            }
        }
    }

    protected virtual void HandleMovement()
    {
        if (_target == null) return;

        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0;
        Vector3 dir = toTarget.normalized;

        if (dir != Vector3.zero)
        {
            Quaternion targetRotation = Quaternion.LookRotation(dir);
            _rb.MoveRotation(Quaternion.Slerp(transform.rotation, targetRotation, 10f * Time.fixedDeltaTime));
        }

        float distToTarget = Vector3.Distance(transform.position, _target.position);

        if (preferredRange > 0)
        {
            if (_atPreferredRange)
            {
                if (distToTarget > preferredRange + rangeToleranceFar)
                {
                    _atPreferredRange = false;
                    _rb.AddForce(dir * moveSpeed, ForceMode.VelocityChange);
                }
                else if (distToTarget < preferredRange - rangeToleranceClose)
                {
                    _atPreferredRange = false;
                    _rb.AddForce(-dir * (moveSpeed / backpedalSpeedDivisor), ForceMode.VelocityChange);
                }
            }
            else
            {
                if (distToTarget > preferredRange)
                    _rb.AddForce(dir * moveSpeed, ForceMode.VelocityChange);
                else if (distToTarget < preferredRange)
                    _rb.AddForce(-dir * (moveSpeed / backpedalSpeedDivisor), ForceMode.VelocityChange);

                if (Mathf.Abs(distToTarget - preferredRange) <= 0.1f)
                    _atPreferredRange = true;
            }
        }
        else
        {
            _rb.AddForce(dir * moveSpeed, ForceMode.VelocityChange);
        }
    }


    protected Vector3 SnapFaceTarget()
    {
        if (_target == null) return Vector3.zero;
        Vector3 dir = _target.position - transform.position;
        dir.y = 0;
        if (dir == Vector3.zero) return Vector3.zero;
        dir.Normalize();
        _rb.rotation = Quaternion.LookRotation(dir);
        SnapRotationRpc(dir);
        return dir;
    }

    protected void FaceTargetSmooth()
    {
        if (_target == null) return;
        Vector3 dir = _target.position - transform.position;
        dir.y = 0;
        if (dir == Vector3.zero) return;
        Quaternion targetRotation = Quaternion.LookRotation(dir.normalized);
        _rb.MoveRotation(Quaternion.Slerp(transform.rotation, targetRotation, 10f * Time.fixedDeltaTime));
    }

    [ObserversRpc]
    private void SnapRotationRpc(Vector3 direction)
    {
        if (isServer) return;
        _rb.rotation = Quaternion.LookRotation(direction);
    }

    protected bool TargetInRange()
    {
        if (_target == null) return false;
        return Vector3.Distance(transform.position, _target.position) <= preferredRange + rangeToleranceFar;
    }

    protected IEnumerator DashToTarget(float speed, float stopDistance)
    {
        if (_physicsCollider != null) _physicsCollider.enabled = false;
        while (!_isDead && _target != null && Vector3.Distance(transform.position, _target.position) > stopDistance)
        {
            Vector3 toTarget = _target.position - transform.position;
            toTarget.y = 0f;
            Vector3 dir = toTarget.normalized;
            _rb.linearVelocity = dir * speed;
            if (dir != Vector3.zero)
                _rb.MoveRotation(Quaternion.LookRotation(dir));
            yield return null;
        }
        _rb.linearVelocity = Vector3.zero;
        if (_physicsCollider != null && !_isDead) _physicsCollider.enabled = true;
    }

    protected IEnumerator DashToPoint(float speed, Vector3 destination)
    {
        if (_physicsCollider != null) _physicsCollider.enabled = false;
        Vector3 initialDir = destination - transform.position;
        initialDir.y = 0f;
        initialDir.Normalize();
        while (!_isDead)
        {
            Vector3 toDestination = destination - transform.position;
            toDestination.y = 0f;
            if (Vector3.Dot(toDestination, initialDir) <= 0f) break;
            _rb.linearVelocity = initialDir * speed;
            _rb.MoveRotation(Quaternion.LookRotation(initialDir));
            yield return null;
        }
        _rb.linearVelocity = Vector3.zero;
        if (_physicsCollider != null && !_isDead) _physicsCollider.enabled = true;
    }

    protected IEnumerator DashBehindTarget(float speed, float distance)
    {
        if (_target == null) yield break;
        if (_physicsCollider != null) _physicsCollider.enabled = false;
        Vector3 approachDir = (_target.position - transform.position);
        approachDir.y = 0f;
        approachDir.Normalize();
        while (!_isDead && _target != null)
        {
            Vector3 behindPoint = _target.position + approachDir * distance;
            Vector3 toPoint = behindPoint - transform.position;
            toPoint.y = 0f;
            if (Vector3.Dot(toPoint, approachDir) <= 0f) break;
            _rb.linearVelocity = approachDir * speed;
            _rb.MoveRotation(Quaternion.LookRotation(approachDir));
            yield return null;
        }
        _rb.linearVelocity = Vector3.zero;
        if (_physicsCollider != null && !_isDead) _physicsCollider.enabled = true;
    }

    public virtual void TakeDamage(float damage, bool isCrit = false, PlayerID attackerOwner = default)
    {
        if (!isServer) return;
        if (CurrentHealth <= 0) return;
        CurrentHealth -= damage;
        FlashHitRpc();
        if (showDamageNumbers) ShowDamageNumberRpc(damage, transform.position, isCrit, attackerOwner, _damageNumberPlayerOffset);
        if (CurrentHealth <= 0)
        {
            _killerOwner = attackerOwner;
            Die();
        }
    }

    [SerializeField] private Renderer[] _renderers;
    private Color[] _originalColors;

    private void CacheRenderers()
    {
        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
            _originalColors[i] = _renderers[i].material.color;
    }

    [ObserversRpc]
    private void ShowDamageNumberRpc(float amount, Vector3 position, bool isCrit, PlayerID attackerOwner, float playerOffset)
    {
        if (playerOffset != 0f)
        {
            CharacterStats localPlayer = null;
            foreach (var kv in CharacterStats.All)
                if (kv.Value.isOwner) { localPlayer = kv.Value; break; }
            if (localPlayer != null)
            {
                Vector3 toPlayer = localPlayer.transform.position - position;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude > 0.001f)
                    position += toPlayer.normalized * playerOffset;
            }
        }
        DamageNumberManager.instance?.Spawn(amount, position, HashCode.Combine(GetInstanceID(), attackerOwner.GetHashCode()), isCrit, attackerOwner);
    }

    [ObserversRpc]
    private void FlashHitRpc()
    {
        StartCoroutine(FlashCoroutine());
    }

    private IEnumerator FlashCoroutine()
    {
        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].material.color = Color.red;
        yield return new WaitForSeconds(0.08f);
        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].material.color = _originalColors[i];
    }

    [ServerRpc(requireOwnership: false)]
    public void TakeDamageRpc(float damage, bool isCrit = false, RPCInfo info = default)
    {
        TakeDamage(damage, isCrit, info.sender);
    }

    public void ApplyKnockback(Vector3 direction, float force)
    {
        if (!isStunnable || _isDead) return;
        float scaledForce = force * (1f - knockbackResistance);
        const float drag      = 9f;
        const float threshold = 1f;
        float stunDuration = scaledForce > threshold
            ? Mathf.Log(scaledForce / threshold) / drag
            : 0f;
        Stun(stunDuration);
        ApplyKnockbackRpc(direction, scaledForce);
    }

    [ObserversRpc(requireServer: false, runLocally: true)]
    private void ApplyKnockbackRpc(Vector3 direction, float scaledForce)
    {
        _rb.linearVelocity = Vector3.zero;
        _rb.AddForce(direction * scaledForce, ForceMode.VelocityChange);
    }

    protected virtual void Die()
    {
        _isDead = true;
        ActiveCount--;
        if (isPleb) ActivePlebCount--;
        else if (!isUncapped) ActiveNormalCount--;

        XPOrbManager.instance.SpawnOrbs(transform.position, xpValue);
        GameManager.instance?.AwardPoints(Mathf.RoundToInt(pointValue));

        HideOnDeathRpc(_killerOwner);
        Destroy(gameObject, 0.5f);
    }

    [ObserversRpc]
    private void HideOnDeathRpc(PlayerID killerOwner)
    {
        _isDead = true;
        foreach (var col in GetComponentsInChildren<Collider>())
            col.enabled = false;
        foreach (var renderer in GetComponentsInChildren<Renderer>())
            renderer.enabled = false;
        OnHideOnDeath();

        CharacterStats.All.TryGetValue(killerOwner, out var killer);
        CharacterStats.RaiseOnEnemyDeath(killer, this);
        if (killer != null && killer.isOwner)
            CharacterStats.RaiseOnEnemyKill(killer, this);
    }

    protected virtual void OnHideOnDeath()
    {
        var shield = GetComponentInChildren<GloomShield>();
        if (shield != null) shield.HideOnDeathRpc();
    }

    protected override void OnPoolReset()
    {
        base.OnPoolReset();
        _isDead = false;
        IsStunned = false;
        isStunnable = !stunResistant;
        _atPreferredRange = false;
        _target = null;
        _killerOwner = default;
        _findTargetTimer = UnityEngine.Random.Range(0f, FindTargetInterval);
        if (_stunCoroutine != null) { StopCoroutine(_stunCoroutine); _stunCoroutine = null; }
        if (_rb != null) { _rb.isKinematic = false; _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; }
        foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = true;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].material.color = _originalColors[i];
        if (_hurtbox != null) _hurtbox.enabled = true;
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        if (_contactDamageCollider != null) _contactDamageCollider.enabled = contactColliderEnabled;
    }

}