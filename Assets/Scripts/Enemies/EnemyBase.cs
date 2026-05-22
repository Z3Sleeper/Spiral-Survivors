using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class EnemyBase : NetworkIdentity
{
    private Rigidbody _rb;

    [Header("Stats")]
    public float moveSpeed = 1f;
    public float backpedalSpeedDivisor = 2f;
    public float maxHealth = 30f;
    public float preferredRange = 0f; // 0 = melee, >0 = ranged
    public float rangeToleranceFar = 1f;
    public float rangeToleranceClose = 0.3f;
    public float baseDamage = 10f;
    [Header("Contact Damage")]
    public bool hasContactDamage = false;
    public float contactCooldown = 1f;
    private Dictionary<CharacterStats, float> _contactCooldowns = new Dictionary<CharacterStats, float>();
    private bool _atPreferredRange = false;
    protected bool _isDead = false;


    public SyncVar<float> _currentHealthSync = new SyncVar<float>(0);
    public float _currentHealth
    {
        get => _currentHealthSync.value;
        set => _currentHealthSync.value = value;
    }

    private float _findTargetTimer = 0;
    private const float FindTargetInterval = 1f;
    protected Transform _target;

    protected virtual void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();

        enabled = isServer;

        if (!isServer) return;
        
        _currentHealth = maxHealth;
    }

    protected virtual void Update()
    {
        if (_isDead) return;
        _findTargetTimer -= Time.deltaTime;
        if (_findTargetTimer <= 0f)
        {
            _findTargetTimer = FindTargetInterval;
            FindTarget();
        }

        var keys = new List<CharacterStats>(_contactCooldowns.Keys);
        foreach (var player in keys)
        {
            _contactCooldowns[player] -= Time.deltaTime;
            if (_contactCooldowns[player] <= 0)
                _contactCooldowns.Remove(player);
        }
    }

    protected virtual void FixedUpdate()
    {
        if (_isDead) return;
        if (_target == null)
        {
            FindTarget();
            return;
        }
        HandleMovement();
    }

    protected virtual void FindTarget()
    {
        // Find nearest player
        var players = FindObjectsByType<CharacterStats>(FindObjectsSortMode.None);
        float nearest = float.MaxValue;
        foreach (var p in players)
        {
            if (p.isDead) continue;
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

        Vector3 toTarget = (_target.position - transform.position);
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

    public virtual void TakeDamage(float damage)
    {
        if (!isServer) return;
        if (_currentHealth <= 0) return;
        _currentHealth -= damage;
        if (_currentHealth <= 0)
            Die();
    }

    [ServerRpc(requireOwnership: false)]
    public void TakeDamageRpc(float damage)
    {
        TakeDamage(damage);
    }

    protected virtual void Die()
    {
        _isDead = true;

        // Disable colliders
        foreach (var col in GetComponentsInChildren<Collider>())
            col.enabled = false;

        // Hide visually
        foreach (var renderer in GetComponentsInChildren<Renderer>())
            renderer.enabled = false;

        // Destroy after short delay to let any in-flight RPCs resolve
        Destroy(gameObject);
    }

    private void OnTriggerEnter(Collider other) => TryContactDamage(other);
    private void OnTriggerStay(Collider other) => TryContactDamage(other);

    private void TryContactDamage(Collider other)
    {
        if (!isServer) return;
        if (!hasContactDamage) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("PlayerHurtbox")) return;
        var player = other.GetComponentInParent<CharacterStats>();
        if (player == null) return;
        if (_contactCooldowns.ContainsKey(player)) return;

        _contactCooldowns[player] = contactCooldown;
        player.TakeDamageRpc(baseDamage);
    }
}