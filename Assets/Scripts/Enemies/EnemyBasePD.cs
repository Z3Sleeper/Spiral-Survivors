using System;
using System.Collections;
using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Which spawn-cap bucket an enemy counts against — decided entirely by whichever spawner created it
// (which list it pulled the prefab from), never by the prefab/class itself. This is what lets a boss just
// be an EnemyBasePD spawned via SpawnCategory.Boss instead of needing a dedicated BossBasePD subclass.
public enum EnemySpawnCategory { Normal, Elite, Pleb, Boss }

// Shared attack-indicator sizing math — not enemy-specific (RemoteSpikeBurstPD, a standalone hazard, is
// not an EnemyBasePD<TState> at all), so this lives as a static class rather than a protected method on
// EnemyBasePD<TState>, same reasoning as WeaponDamageMath sitting outside WeaponBasePD<TState>. Project
// convention: line indicators are planes, radial indicators are cylinders — see each method for why the
// axis math differs between the two.
public static class IndicatorMath
{
    // Sizes/positions a line indicator (a plane) along its own local Z so it visually spans from its
    // origin out to `distance` — Z scale is 10% of distance, Z position is half of it (since the plane
    // pivots at its own center, moving it forward by half its own new length makes it start at the origin
    // and reach exactly `distance` away). X/Y of both scale and position are left untouched — they're the
    // indicator's own authored width/thickness/offset.
    public static void ApplyLineTransform(Transform t, float distance)
    {
        if (t == null) return;
        var scale = t.localScale;
        var pos   = t.localPosition;
        t.localScale    = new Vector3(scale.x, scale.y, distance * 0.1f);
        t.localPosition = new Vector3(pos.x, pos.y, distance * 0.5f);
    }

    // Sizes a radial indicator (a cylinder) so its footprint matches `radius` — x2 because a cylinder's
    // localScale.x/z map to diameter, not radius. Y is left untouched (the indicator's own authored
    // flatness).
    public static void ApplyRadialTransform(Transform t, float radius)
    {
        if (t == null) return;
        float diameter = radius * 2f;
        var scale = t.localScale;
        t.localScale = new Vector3(diameter, scale.y, diameter);
    }

    // BoxCollider.size/center are separate from Transform.localScale/localPosition — size is a literal
    // local-space length, not a scale multiplier off some baseline mesh, so unlike ApplyLineTransform this
    // sets Z size to `distance` directly (no 0.1 ratio) and centers it the same way (half of distance, so
    // the near edge sits at the origin and the far edge reaches exactly `distance` away). X/Y of both are
    // left untouched — the hitbox's own authored width/height. A single range field on the enemy script is
    // the source of truth — both this and ApplyLineTransform derive from it, so the indicator and the
    // actual hitbox can never drift out of sync with each other.
    public static void ApplyLineCollider(BoxCollider box, float distance)
    {
        if (box == null) return;
        var size   = box.size;
        var center = box.center;
        box.size   = new Vector3(size.x, size.y, distance);
        box.center = new Vector3(center.x, center.y, distance * 0.5f);
    }
}

public interface IEnemyPD
{
    bool _isDead { get; }
    float CurrentHealth { get; }
    float MaxHealth { get; }
    string EnemyName { get; }
    // 1 = full time remaining before enraging, 0 = enraged (or enrageTimer isn't configured at all — most
    // enemies). Same generic-regardless-of-TState reasoning as every other member here.
    float EnrageProgress { get; }
    Transform transform { get; }
    // Stable, rollback-safe reference to this enemy — safe to store in predicted state (e.g. a
    // PredictedEvent payload), unlike a raw IEnemyPD/Transform reference, since enemies are pooled and
    // reused for new spawns. Trivial passthrough — every PredictedIdentity already carries this.
    PredictedObjectID ObjectId { get; }
    void TakeDamage(float damage, bool isCrit, PlayerID attackerOwner);
    void AddKnockbackVelocity(Vector3 impulse);
    void Stun(float duration);
    void TriggerHitFeedback(float damage, bool isCrit, bool isHighroll, CharacterStatsPD attacker);

    // Lets a spawner mark a freshly hierarchy.Create()'d enemy with which category spawned it (deciding
    // which Active*Count bucket it counts against) and apply co-op/elite stat scaling, generically,
    // regardless of which concrete TState the enemy's subclass uses — same reason this interface exists
    // at all.
    void ConfigureSpawn(EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier);
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
    // Live, ticking-down countdown — kept separate from EnemyBasePD.enrageTimer (the configured starting
    // duration, a plain never-mutated field) for the same reason WeaponRangedPD's fireTimer isn't just a
    // plain field: anything that changes every tick during Simulate() has to live in predicted State to
    // resimulate correctly, a plain field mutated in-place wouldn't roll back and replay consistently.
    public float enrageCountdown;
    public bool  enraged;
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

    [Header("Enrage")]
    // 0 = no enrage at all (the default, opt-in only) — anything <= 0 is treated as "not configured" and
    // never ticks or fires OnEnrage(), so this is a no-op for every enemy that doesn't set it.
    public float enrageTimer = 0f;

    // Fires exactly once, the tick enrageCountdown first reaches 0 — stub by default, a subclass decides
    // what enraging actually means for it (King Spike: reassigns its own attackCooldown and changes when
    // its next-attack cooldown gets armed).
    protected virtual void OnEnrage() { }

    // Virtual, not a plain field, so a subclass can override it with zero lifecycle-ordering risk — a
    // plain field reassigned in some override hook could easily run after GetInitialState() already read
    // it (same class of bug as the field-init-before-OnSpawned issue elsewhere in this project), whereas a
    // property is always current whenever it's actually read.
    protected virtual float FindTargetInterval => 1f;
    protected Transform _target;

    // Public for cross-script reads (AddKnockbackVelocity, Stun()), not because it's meant to be hand-
    // tuned — it's fully derived from stunResistant at Awake() and, for state-driven enemies, reassigned
    // every tick from current attack state. Hidden so it doesn't read as a real Inspector setting
    // alongside stunResistant, which actually is one.
    [HideInInspector] public bool isStunnable = true;
    public bool IsStunned => B.stunTimer > 0f;
    protected virtual bool IsReadyToAttack => false;
    public bool _isDead => B.isDead;

    public static int ActiveCount = 0;
    public static int ActiveNormalCount = 0;
    public static int ActiveEliteCount = 0;
    public static int ActivePlebCount = 0;
    public static int ActiveBossCount = 0;
    // Which spawn-cap bucket this instance counts against — set by whichever spawner created it via
    // ConfigureSpawn(), not authored per-prefab (a single prefab can be spawned as different categories
    // by different spawn calls, e.g. the same enemy used as both a tier's normal and elite variant).
    [NonSerialized] public EnemySpawnCategory spawnCategory = EnemySpawnCategory.Normal;

    // Static fields never reset themselves — there's no domain reload in a built game (that's an Editor-
    // only Play Mode concept), and a player going Lobby -> Start -> Lobby -> Start within one launch is a
    // scene load, not a process restart. Worse, enemies still alive when a session ends get torn down by
    // scene unload rather than their own Die(), so the normal decrement never runs for them. Called from
    // every point a fresh game session could begin or a stale one could end — see EnemySpawnerPD.Awake(),
    // GameManagerPD.UpdateView() (game-start and game-end transitions), ReturnToLobbyManager (both the
    // server and client leave-game paths), and CharacterRegistry.OnApplicationQuit() — deliberately
    // redundant across all of them rather than trusting any single exit path to always fire cleanly.
    public static void ResetActiveCounts()
    {
        ActiveCount = 0;
        ActiveNormalCount = 0;
        ActiveEliteCount = 0;
        ActivePlebCount = 0;
        ActiveBossCount = 0;
    }

    public virtual float GetDamage() => damage;
    public virtual bool CanContactDamage => true;

    [SerializeField] private Renderer[] _renderers;
    private Color[] _originalColors;
    private bool  _wasDeadLastFrame;
    private float _verifiedHealth;
    private float _verifiedEnrageCountdown;

    // ── Subclass contract ──────────────────────────────────────────────────
    protected abstract ref EnemyBaseState GetBase(ref TState state);

    // Shorthand for accessing base fields on currentState
    protected ref EnemyBaseState B => ref GetBase(ref currentState);

    public float CurrentHealth => _verifiedHealth;

    // Verified (not predicted) same reasoning as CurrentHealth — reading straight off B.enrageCountdown
    // would jitter/flicker on any misprediction rollback, since that's live predicted state, not confirmed.
    // 0 for any enemy that doesn't configure enrageTimer at all (most enemies) — explicit "empty," not
    // "full," so it doesn't visually imply an enrage that will never happen.
    public float EnrageProgress => enrageTimer <= 0f ? 0f : Mathf.Clamp01(_verifiedEnrageCountdown / enrageTimer);

    public float MaxHealth => maxHealth;

    public string EnemyName => enemyName;

    public PredictedObjectID ObjectId => id.objectId;

    // ── State ──────────────────────────────────────────────────────────────
    protected override TState GetInitialState()
    {
        var s = default(TState);
        ref var b = ref GetBase(ref s);
        b.health = maxHealth;
        // 0, not FindTargetInterval — a freshly spawned (or pooled-reused) enemy should search for a
        // target on its very first tick, not sit idle for a full interval first. Only the RENEWAL after
        // that first search (in Simulate(), below) uses the full FindTargetInterval.
        b.findTargetTimer = 0f;
        b.contactCooldowns = DisposableList<ContactCooldown>.Create(4);
        b.enrageCountdown = enrageTimer;
        return s;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────
    private void Awake()
    {
        // Hurtbox lives directly on the root (not a child, unlike PhysicsCollider/ContactDamageCollider
        // below) so hit-detection code can resolve it to IEnemyPD via a same-GameObject GetComponent()
        // instead of GetComponentInParent() — cheap either way here since the hierarchy was already
        // shallow, but this is checked on every physics-hit-result, every tick, across every active
        // weapon/projectile, so avoiding the wasted extra hop adds up.
        _hurtbox = GetComponent<Collider>();

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
        AdjustCategoryCount(spawnCategory, +1);
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

        var stats = other.GetComponent<CharacterStatsPD>();
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
            // Guarded by !isKinematic — SpearGloomPD/KingSpikePD's Windup/Stabbing-style kinematic windows
            // mean this rigidbody can be kinematic when pause hits, and PredictedRigidbody's velocity
            // setters throw ("not supported") if attempted on a kinematic body.
            if (_rigidbody != null && !_rigidbody.isKinematic)
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

            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

            foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = true;
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = _originalColors[i];

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

        // enrageTimer <= 0 means "not configured" for this enemy — never ticks, OnEnrage() never fires,
        // a no-op for every enemy that doesn't set it. Same "don't tick while stunned" behavior as old
        // BossBase. !b.enraged guards it to firing exactly once.
        if (!b.enraged && enrageTimer > 0f)
        {
            b.enrageCountdown -= delta;
            if (b.enrageCountdown <= 0f)
            {
                b.enraged = true;
                OnEnrage();
            }
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

                CharacterStatsPD.All.TryGetValue(vb.killerOwner, out var killer);
                CharacterStatsPD.RaiseOnEnemyDeath(killer, this);
                if (killer != null && killer.isOwner) CharacterStatsPD.RaiseOnEnemyKill(killer, this);
                if (GameManagerPD.instance != null) GameManagerPD.instance.AwardUpgradePoints(xpValue);

                hierarchy.Delete(this);
            }

            _wasDeadLastFrame = vb.isDead;
            _verifiedHealth   = vb.health;
            _verifiedEnrageCountdown = vb.enrageCountdown;
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

    public void TriggerHitFeedback(float damage, bool isCrit, bool isHighroll, CharacterStatsPD attacker)
    {
        // PD weapons (WeaponMeleePD, WeaponRangedProjectilePD) queue hit feedback in Simulate()/
        // LateSimulate() but only drain and fire it here in UpdateView(), at render rate — by the time it
        // fires, this pooled enemy instance can already have been reused for a new spawn (isDead reset to
        // false) while transiently still inactive, same as the respawn-reset/animator cases elsewhere in
        // this class.
        if (!B.isDead && gameObject.activeInHierarchy) StartCoroutine(FlashCoroutine());
        if (showDamageNumbers) ShowDamageNumber(damage, attacker.owner ?? default, isCrit, isHighroll);
        if (attacker.isOwner && SFXManager.instance != null)
            SFXManager.instance.Play(attacker.hitSfx, transform.position, 1f, 0.05f);
    }

    protected virtual void Die()
    {
        B.isDead = true;
        ActiveCount--;
        AdjustCategoryCount(spawnCategory, -1);

        if (XPOrbManagerPD.instance != null)
            XPOrbManagerPD.instance.SpawnOrbs(hierarchy, transform.position, xpValue);
    }

    public void Stun(float duration)
    {
        if (_isDead || stunResistant || !isStunnable) return;
        if (IsReadyToAttack) return;
        B.stunTimer = Mathf.Max(B.stunTimer, duration);
    }

    // Called by a spawner right after hierarchy.Create() returns, telling this instance which category
    // spawned it. spawnCategory is [NonSerialized] and always starts Normal — LateAwake() has already
    // bucketed this instance into ActiveNormalCount using that default by the time this runs, so the
    // bucket is corrected here rather than assumed, regardless of exactly when LateAwake() ran relative to
    // this call. Health scaling mutates B.health directly (not just maxHealth) since GetInitialState()
    // already baked b.health = maxHealth before the spawner got a chance to scale it — same "mutate
    // currentState right after Create()" pattern as XPOrbPD.SetValue(). damage doesn't need that trick
    // (nothing bakes it into predicted state at spawn time — it's read live off the field every use), but
    // its base* snapshot (captured in Awake(), before this ever runs) DOES need to be scaled in lockstep,
    // since subclasses can derive further values from it.
    public void ConfigureSpawn(EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier)
    {
        if (category != spawnCategory)
        {
            AdjustCategoryCount(spawnCategory, -1);
            spawnCategory = category;
            AdjustCategoryCount(spawnCategory, +1);
        }

        xpValue *= xpMultiplier;
        baseXpValue *= xpMultiplier;

        if (healthMultiplier != 1f)
        {
            maxHealth *= healthMultiplier;
            baseHealth *= healthMultiplier;
            B.health = maxHealth;
        }

        if (damageMultiplier != 1f)
        {
            damage *= damageMultiplier;
            baseDamage *= damageMultiplier;
        }
    }

    private static void AdjustCategoryCount(EnemySpawnCategory category, int delta)
    {
        switch (category)
        {
            case EnemySpawnCategory.Normal: ActiveNormalCount += delta; break;
            case EnemySpawnCategory.Elite:  ActiveEliteCount  += delta; break;
            case EnemySpawnCategory.Pleb:   ActivePlebCount   += delta; break;
            case EnemySpawnCategory.Boss:   ActiveBossCount   += delta; break;
        }
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

    private void ShowDamageNumber(float amount, PlayerID attackerOwner, bool isCrit = false, bool isHighroll = false)
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
            DamageNumberManager.instance.Spawn(amount, pos, HashCode.Combine(GetInstanceID(), attackerOwner.GetHashCode()), isCrit, isHighroll, attackerOwner);
    }

    private IEnumerator FlashCoroutine()
    {
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = Color.red;
        yield return new WaitForSeconds(0.08f);
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = _originalColors[i];
    }

    // Guards against both a null/empty _renderers array and individual entries lacking a material —
    // .material returns null (not a thrown exception, just a silent null) for a Renderer with no materials
    // assigned, which previously caused a NullReferenceException here and, more importantly, wherever
    // _originalColors[i] later got read against a color that never got cached (EnemyBasePD.Simulate's
    // pooled-reuse init block, FlashCoroutine above).
    private void CacheRenderers()
    {
        _renderers ??= Array.Empty<Renderer>();
        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
            _originalColors[i] = _renderers[i] != null && _renderers[i].material != null
                ? _renderers[i].material.color
                : Color.white;
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
