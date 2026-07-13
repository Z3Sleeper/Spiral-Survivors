using System;
using System.Collections;
using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public interface IEnemyPD
{
    bool _isDead { get; }
    float CurrentHealth { get; }
    float MaxHealth { get; }
    Transform transform { get; }
    void TakeDamage(float damage, bool isCrit, PlayerID attackerOwner);
    void AddKnockbackVelocity(Vector3 impulse);
    void Stun(float duration);
    void TriggerHitFeedback(float damage, bool isCrit, CharacterStatsPD attacker);
}

// Per-target contact damage cooldown — contact damage repeats over time (unlike a one-shot attack),
// so each player touching the enemy needs their own independent cooldown, not a single shared one.
public struct ContactCooldown
{
    public PlayerID player;
    public float remaining;
}

// Common enemy simulation fields; embedded inside each enemy's TState struct.
public struct EnemyBaseState
{
    public float health;
    public bool isDead;
    public float stunTimer;
    public bool atPreferredRange;
    public float findTargetTimer;
    public DisposableList<ContactCooldown> contactCooldowns;
    public bool isDashing;
    public Vector3 dashDirection;
    public Vector3 dashDestination;
    public float    dashSpeed;
    public PlayerID killerOwner;
    public bool initialized;
}

[RequireComponent(typeof(PredictedRigidbody))]
public abstract class EnemyBasePD<TState> : PredictedIdentity<TState>, IEnemyPD
    where TState : struct, IPredictedData<TState>
{
    [SerializeField] protected PredictedRigidbody _rigidbody;
    protected Collider _hurtbox;
    protected Collider _physicsCollider;
    protected Collider _contactDamageCollider;
    protected Animator _animator;

    [Header("Identity")]
    public string enemyName = "Enemy";

    [Header("Stats")]
    public float moveSpeed = 6f;
    [NonSerialized] public float baseMoveSpeed;
    public float backpedalSpeedDivisor = 3f;
    public float maxHealth = 30f;
    [NonSerialized] public float baseHealth;
    public float preferredRange = 0f;
    public float rangeToleranceFar = 1f;
    public float rangeToleranceClose = 0.3f;
    public float damage = 10f;
    [NonSerialized] public float baseDamage;
    public float xpValue = 1f;
    [NonSerialized] public float baseXpValue;

    [Header("Contact Damage")]
    public bool contactColliderEnabled = true;
    public float contactCooldown = 0.5f;
    public bool stunResistant = false;
    [Range(0f, 1f)] public float knockbackResistance = 0f;
    public bool showDamageNumbers = true;
    [SerializeField] private float _damageNumberPlayerOffset = 0f;

    protected float FindTargetInterval = 1f;
    protected Transform _target;

    public bool isStunnable = true;
    public bool IsStunned => B.stunTimer > 0f;
    protected virtual bool IsReadyToAttack => false;
    public bool _isDead => B.isDead;

    public static int ActiveCount = 0;
    public static int ActiveNormalCount = 0;
    public static int ActivePlebCount = 0;
    [NonSerialized] public bool isPleb = false;
    [NonSerialized] public bool isUncapped = false;

    public virtual float GetDamage() => damage;
    public virtual bool CanContactDamage => true;

    [SerializeField] private Renderer[] _renderers;
    private Color[] _originalColors;
    private bool  _wasDeadLastFrame;
    private float _verifiedHealth;

    // ── Subclass contract ──────────────────────────────────────────────────
    protected abstract ref EnemyBaseState GetBase(ref TState state);

    // Shorthand for accessing base fields on currentState
    protected ref EnemyBaseState B => ref GetBase(ref currentState);

    public float CurrentHealth => _verifiedHealth;

    public float MaxHealth => maxHealth;

    // ── State ──────────────────────────────────────────────────────────────
    protected override TState GetInitialState()
    {
        var s = default(TState);
        ref var b = ref GetBase(ref s);
        b.health = maxHealth;
        b.findTargetTimer = FindTargetInterval;
        b.contactCooldowns = DisposableList<ContactCooldown>.Create(4);
        return s;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────
    private void Awake()
    {
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
        isStunnable = !stunResistant;

        CacheRenderers();
        _animator = GetComponentInChildren<Animator>();
    }

    protected override void LateAwake()
    {
        ActiveCount++;
        if (isPleb) ActivePlebCount++;
        else if (!isUncapped) ActiveNormalCount++;
        _verifiedHealth = maxHealth;

        if (_contactDamageCollider != null && _contactDamageCollider.TryGetComponent(out PredictedPhysicsCallbacks callbacks))
        {
            callbacks.onTriggerEnter += OnContactTrigger;
            callbacks.onTriggerStay += OnContactTrigger;
        }
    }

    private void OnContactTrigger(GameObject other)
    {
        if (!CanContactDamage) return;
        if (IsStunned) return;
        ref var b = ref B;
        if (b.isDead) return;

        var stats = other.GetComponentInParent<CharacterStatsPD>();
        if (stats == null || !stats.owner.HasValue) return;

        var player = stats.owner.Value;
        var cooldowns = b.contactCooldowns;

        for (int i = 0; i < cooldowns.Count; i++)
        {
            if (!cooldowns[i].player.Equals(player)) continue;
            if (cooldowns[i].remaining > 0f) return;
            cooldowns[i] = new ContactCooldown { player = player, remaining = contactCooldown };
            stats.TakeDamage(GetDamage());
            return;
        }

        cooldowns.Add(new ContactCooldown { player = player, remaining = contactCooldown });
        stats.TakeDamage(GetDamage());
    }

    protected override void Destroyed()
    {
        KillGraph();
    }

    // ── Simulation ─────────────────────────────────────────────────────────
    protected override void Simulate(ref TState state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused)
        {
            // DoPhysicsPass() isn't gated by IsPaused — it keeps integrating whatever velocity the
            // Rigidbody already has every tick regardless of the visual pause. Without this, an enemy
            // mid-knockback or mid-dash-to-target when pause hits would keep sliding from residual
            // momentum while "paused" (same root cause as the equivalent player-side fix).
            if (_rigidbody != null)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            return;
        }
        ref var b = ref GetBase(ref state);

        // Pooled reuse never calls OnRemovedFromPool() (PredictedHierarchy's pool path re-registers
        // with reset:false), so first-tick setup has to be driven off State instead, since
        // GetInitialState() is the one thing reliably re-run on every reuse.
        if (!b.initialized)
        {
            // On the client, this tick can be replayed before the pooled object has actually been
            // reactivated yet, which throws when touching the Animator and — worse — would otherwise
            // mark this spawn as already-reset without ever having rebound the animator, permanently
            // leaving it on whatever pose it was frozen in during its previous pooled life. Don't commit
            // to initialized until we can actually do the reset; retry next tick instead.
            if (!gameObject.activeInHierarchy) return;

            b.initialized = true;
            _target = null;
            _wasDeadLastFrame = false;
            _verifiedHealth   = maxHealth;

            if (_rigidbody != null)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

            foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = true;
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].material.color = _originalColors[i];

            if (_hurtbox != null) _hurtbox.enabled = true;
            if (_physicsCollider != null) _physicsCollider.enabled = true;
            if (_contactDamageCollider != null) _contactDamageCollider.enabled = contactColliderEnabled;

            if (_animator != null)
            {
                _animator.enabled = true;
                // Force back to each layer's default state, regardless of whatever pose it was
                // frozen in when pooled (e.g. mid-attack) — enabled alone doesn't reset this.
                _animator.Rebind();
                _animator.Update(0f);
            }

            OnRespawnReset();
        }

        if (b.isDead) return;

        if (b.stunTimer > 0f)
        {
            b.stunTimer -= delta;
            return;
        }

        b.findTargetTimer -= delta;
        if (b.findTargetTimer <= 0f)
        {
            b.findTargetTimer = FindTargetInterval;
            FindTarget();
        }

        for (int i = b.contactCooldowns.Count - 1; i >= 0; i--)
        {
            var entry = b.contactCooldowns[i];
            entry.remaining -= delta;
            if (entry.remaining <= 0f)
                b.contactCooldowns.RemoveAt(i);
            else
                b.contactCooldowns[i] = entry;
        }

        bool wasDashing = b.isDashing;
        if (b.isDashing)
            HandleDash(ref state, delta);

        if (!b.isDashing)
            HandleActiveState(ref state, delta, wasDashing);
    }

    protected virtual void HandleActiveState(ref TState state, float delta, bool dashJustEnded = false)
    {
        if (_target != null) HandleMovement(ref state, delta);
    }

    protected override void UpdateView(TState viewState, TState? verified)
    {
        if (_animator != null)
            UpdateAnimator(viewState);

        // Death is state-critical — only act on server-confirmed dead.
        if (verified.HasValue)
        {
            var v = verified.Value;
            ref var vb = ref GetBase(ref v);

            if (vb.isDead && !_wasDeadLastFrame)
            {
                OnHideOnDeath();

                CharacterStatsPD.All.TryGetValue(vb.killerOwner, out _);
                // TODO: CharacterStatsPD.RaiseOnEnemyDeath(killer, this);
                // TODO: if (killer != null && killer.isOwner) CharacterStatsPD.RaiseOnEnemyKill(killer, this);
                if (GameManagerPD.instance != null) GameManagerPD.instance.AwardUpgradePoints(xpValue);

                hierarchy.Delete(this);
            }

            _wasDeadLastFrame = vb.isDead;
            _verifiedHealth   = vb.health;
        }
    }

    // ── Movement ───────────────────────────────────────────────────────────
    protected virtual void FindTarget()
    {
        float nearest = float.MaxValue;
        foreach (var p in CharacterStatsPD.All.Values)
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

    protected virtual void HandleMovement(ref TState state, float delta)
    {
        if (_target == null) return;

        ref var b = ref GetBase(ref state);
        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0;
        Vector3 dir = toTarget.normalized;

        if (dir != Vector3.zero)
        {
            Quaternion targetRotation = Quaternion.LookRotation(dir);
            _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, targetRotation, 10f * delta));
        }

        float distToTarget = Vector3.Distance(transform.position, _target.position);

        if (preferredRange > 0)
        {
            if (b.atPreferredRange)
            {
                if (distToTarget > preferredRange + rangeToleranceFar)
                {
                    b.atPreferredRange = false;
                    _rigidbody.AddForce(dir * (moveSpeed * delta * 5f), ForceMode.VelocityChange);
                }
                else if (distToTarget < preferredRange - rangeToleranceClose)
                {
                    b.atPreferredRange = false;
                    _rigidbody.AddForce(-dir * (moveSpeed / backpedalSpeedDivisor * delta * 5f), ForceMode.VelocityChange);
                }
            }
            else
            {
                if (distToTarget > preferredRange)
                    _rigidbody.AddForce(dir * (moveSpeed * delta * 5f), ForceMode.VelocityChange);
                else if (distToTarget < preferredRange)
                    _rigidbody.AddForce(-dir * (moveSpeed / backpedalSpeedDivisor * delta * 5f), ForceMode.VelocityChange);

                if (Mathf.Abs(distToTarget - preferredRange) <= 0.1f)
                    b.atPreferredRange = true;
            }
        }
        else
        {
            _rigidbody.AddForce(dir * (moveSpeed * delta * 5f), ForceMode.VelocityChange);
        }
    }

    // ── Dash helpers (for subclasses) ──────────────────────────────────────
    protected void StartDashToTarget(ref TState state, float speed, float stopDistance)
    {
        if (_target == null) return;
        ref var b = ref GetBase(ref state);
        Vector3 dir = (_target.position - transform.position);
        dir.y = 0f;
        dir.Normalize();
        b.isDashing = true;
        b.dashDirection = dir;
        b.dashDestination = _target.position - dir * stopDistance;
        b.dashDestination.y = transform.position.y;
        b.dashSpeed = speed;
        if (_physicsCollider != null) _physicsCollider.enabled = false;
    }

    protected void StartDashToPoint(ref TState state, float speed, Vector3 destination)
    {
        ref var b = ref GetBase(ref state);
        Vector3 dir = destination - transform.position;
        dir.y = 0f;
        dir.Normalize();
        b.isDashing = true;
        b.dashDirection = dir;
        b.dashDestination = destination;
        b.dashSpeed = speed;
        if (_physicsCollider != null) _physicsCollider.enabled = false;
    }

    protected void HandleDash(ref TState state, float delta)
    {
        ref var b = ref GetBase(ref state);
        Vector3 toDestination = b.dashDestination != Vector3.zero
            ? b.dashDestination - transform.position
            : (_target != null ? _target.position - transform.position : Vector3.zero);
        toDestination.y = 0f;

        if (Vector3.Dot(toDestination, b.dashDirection) <= 0f || b.isDead)
        {
            b.isDashing = false;
            _rigidbody.velocity = Vector3.zero;
            if (_physicsCollider != null && !b.isDead) _physicsCollider.enabled = true;
            return;
        }

        _rigidbody.velocity = b.dashDirection * b.dashSpeed;
        _rigidbody.MoveRotation(Quaternion.LookRotation(b.dashDirection));
    }

    protected Vector3 SnapFaceTarget(ref TState state)
    {
        if (_target == null) return Vector3.zero;
        Vector3 dir = _target.position - transform.position;
        dir.y = 0;
        if (dir == Vector3.zero) return Vector3.zero;
        dir.Normalize();
        _rigidbody.MoveRotation(Quaternion.LookRotation(dir));
        return dir;
    }

    protected void FaceTargetSmooth(float delta)
    {
        if (_target == null) return;
        Vector3 dir = _target.position - transform.position;
        dir.y = 0;
        if (dir == Vector3.zero) return;
        _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir.normalized), 10f * delta));
    }

    protected bool TargetInRange()
    {
        if (_target == null) return false;
        return Vector3.Distance(transform.position, _target.position) <= preferredRange + rangeToleranceFar;
    }

    // ── Combat ─────────────────────────────────────────────────────────────
    public virtual void TakeDamage(float damage, bool isCrit = false, PlayerID attackerOwner = default)
    {
        ref var b = ref B;
        if (b.isDead) return;
        if (b.health <= 0) return;

        b.killerOwner = attackerOwner;
        b.health      -= damage;

        if (b.health <= 0)
        {
            b.health = 0;
            Die();
        }
    }

    public void TriggerHitFeedback(float damage, bool isCrit, CharacterStatsPD attacker)
    {
        if (!B.isDead) StartCoroutine(FlashCoroutine());
        if (showDamageNumbers) ShowDamageNumber(damage, attacker.owner ?? default, isCrit);
        if (attacker.isOwner && SFXManager.instance != null)
            SFXManager.instance.Play(attacker.hitSfx, transform.position, 1f, 0.05f);
    }

    protected virtual void Die()
    {
        B.isDead = true;
        ActiveCount--;
        if (isPleb) ActivePlebCount--;
        else if (!isUncapped) ActiveNormalCount--;

        if (XPOrbManagerPD.instance != null)
            XPOrbManagerPD.instance.SpawnOrbs(hierarchy, transform.position, xpValue);
    }

    public void Stun(float duration)
    {
        if (_isDead || stunResistant || !isStunnable) return;
        if (IsReadyToAttack) return;
        B.stunTimer = Mathf.Max(B.stunTimer, duration);
    }

    public void AddKnockbackVelocity(Vector3 impulse)
    {
        if (!isStunnable || _isDead) return;
        float effectiveSpeed = impulse.magnitude * (1f - knockbackResistance);
        // Zeroed first rather than added to, so knockback doesn't have to fight whatever velocity the
        // enemy's own movement AddForce was already carrying that tick.
        _rigidbody.velocity = impulse.normalized * effectiveSpeed;
        const float drag      = 9f;
        const float threshold = 1f;
        float stunDuration = effectiveSpeed > threshold
            ? Mathf.Log(effectiveSpeed / threshold) / drag
            : 0f;
        Stun(stunDuration);
    }

    // ── Visuals ────────────────────────────────────────────────────────────
    protected virtual void OnHideOnDeath() { }
    protected virtual void UpdateAnimator(TState viewState) { }

    // Called once per (re)spawn, from within the guaranteed-reliable respawn block in Simulate().
    // Subclasses with their own pooled state (attack phases, hitboxes, visual-state caches, etc.)
    // should reset it here instead of OnRemovedFromPool(), which never fires for pooled reuse.
    protected virtual void OnRespawnReset() { }

    private void ShowDamageNumber(float amount, PlayerID attackerOwner, bool isCrit = false)
    {
        Vector3 pos = transform.position;
        if (_damageNumberPlayerOffset != 0f)
        {
            foreach (var kv in CharacterStatsPD.All)
            {
                if (!kv.Value.isOwner) continue;
                Vector3 toPlayer = kv.Value.transform.position - pos;
                toPlayer.y = 0f;
                if (toPlayer.sqrMagnitude > 0.001f)
                    pos += toPlayer.normalized * _damageNumberPlayerOffset;
                break;
            }
        }
        if (DamageNumberManager.instance != null)
            DamageNumberManager.instance.Spawn(amount, pos, HashCode.Combine(GetInstanceID(), attackerOwner.GetHashCode()), isCrit, attackerOwner);
    }

    private IEnumerator FlashCoroutine()
    {
        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].material.color = Color.red;
        yield return new WaitForSeconds(0.08f);
        for (int i = 0; i < _renderers.Length; i++)
            _renderers[i].material.color = _originalColors[i];
    }

    private void CacheRenderers()
    {
        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
            _originalColors[i] = _renderers[i].material.color;
    }

    private void KillGraph()
    {
        if (_animator == null) return;
        // Not destroying playableGraph here: Destroyed() fires on every pooled death (not just true
        // destruction), and a destroyed graph can't be recreated by Rebind() on the next reuse — it
        // permanently severs the Animator's connection to its controller. The GameObject is already
        // SetActive(false) while pooled, which already stops it from being ticked at all, so simply
        // disabling the component is sufficient and reuse-safe.
        _animator.enabled = false;
    }

    // ── Pool callbacks ─────────────────────────────────────────────────────
    protected override void OnAddedToPool()
    {
        KillGraph();
    }
}

// Concrete non-generic class for enemies that don't need extra state beyond the base.
[RequireComponent(typeof(PredictedRigidbody))]
public class EnemyBasePD : EnemyBasePD<EnemyBasePD.State>
{
    public struct State : IPredictedData<State>
    {
        public EnemyBaseState b;
        public void Dispose() { }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;
}
