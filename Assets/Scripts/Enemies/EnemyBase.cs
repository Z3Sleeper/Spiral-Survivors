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
    [Header("Contact Damage")]
    public bool hasContactDamage = false;
    public float contactCooldown = 0.5f;
    private bool _atPreferredRange = false;
    [NonSerialized] public bool _isDead = false;
    [NonSerialized] public bool isFinalBoss = false;
    public EnemyBase Source => this;
    public virtual float GetDamage() => damage;
    public virtual bool CanContactDamage => hasContactDamage;


    public SyncVar<float> _currentHealthSync = new(0);
    public SyncVar<float> _syncMaxHealth = new(0);
    public float CurrentHealth
    {
        get => _currentHealthSync.value;
        set => _currentHealthSync.value = value;
    }

    public static int ActiveCount = 0;

    private float _findTargetTimer = 0;
    private const float FindTargetInterval = 1f;
    protected Transform _target;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _hurtbox = transform.Find("Hurtbox").GetComponent<Collider>();
        _physicsCollider = transform.Find("PhysicsCollider").GetComponent<Collider>();

        baseHealth = maxHealth;
        baseDamage = damage;
        baseMoveSpeed = moveSpeed;

        _findTargetTimer = UnityEngine.Random.Range(0f, FindTargetInterval);
        CacheRenderers();
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();

        _syncMaxHealth.onChanged += val => maxHealth = val;

        if (!isServer) return;
        _syncMaxHealth.value = maxHealth;
        CurrentHealth = maxHealth;
        ActiveCount++;
    }

    protected virtual void Update()
    {
        if (_isDead) return;
        if (!isServer) return;
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
        foreach (var p in CharacterStats.All)
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

    public virtual void TakeDamage(float damage)
    {
        if (!isServer) return;
        if (CurrentHealth <= 0) return;
        CurrentHealth -= damage;
        FlashHitRpc();
        if (CurrentHealth <= 0)
            Die();
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
    public void TakeDamageRpc(float damage)
    {
        TakeDamage(damage);
    }

    protected virtual void Die()
    {
        _isDead = true;
        ActiveCount--;

        if (isFinalBoss)
            GameManager.instance.TriggerWin();

        XPOrbManager.instance.SpawnOrbs(transform.position, xpValue);

        // Disable colliders
        foreach (var col in GetComponentsInChildren<Collider>())
            col.enabled = false;

        // Hide visually
        foreach (var renderer in GetComponentsInChildren<Renderer>())
            renderer.enabled = false;

        Destroy(gameObject, 0.5f);
    }

}