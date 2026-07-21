using System;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Packing;
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
    // Whether the specific hit that killed this enemy was a crit — read from an OnEnemyDeath reaction so a
    // kill-triggered effect (e.g. OnKillLaunchEnemiesAugment) can inherit the killing hit's crit status
    // instead of rolling its own independent chance.
    bool WasKilledByCrit { get; }
    string EnemyName { get; }
    // 1 = full time remaining before enraging, 0 = enraged (or enrageTimer isn't configured at all — most
    // enemies). Same generic-regardless-of-TState reasoning as every other member here.
    float EnrageProgress { get; }
    Transform transform { get; }
    // The smoothly render-frame-interpolated visuals child — see EnemyBasePD.Visuals's own doc comment.
    Transform Visuals { get; }
    // Stable, rollback-safe reference to this enemy — safe to store in predicted state (e.g. a
    // PredictedEvent payload), unlike a raw IEnemyPD/Transform reference, since enemies are pooled and
    // reused for new spawns. Trivial passthrough — every PredictedIdentity already carries this.
    PredictedObjectID ObjectId { get; }
    // Increments every time this pooled slot starts a new logical enemy life (see EnemyBasePD.Simulate's
    // !b.initialized block). A raw Transform/IEnemyPD reference alone can't tell you whether the enemy it
    // originally pointed at is still alive — pooling means the same GameObject can get silently reused for
    // a completely different spawn, so anything that needs to track "is this SPECIFIC enemy still the one
    // I started tracking" (e.g. DamageNumber following an enemy that dies) must compare a captured
    // SpawnGeneration snapshot against this live value, not just null/active-check the Transform.
    int SpawnGeneration { get; }
    void TakeDamage(float damage, bool isCrit, PlayerID attackerOwner);
    void AddKnockbackVelocity(Vector3 impulse);
    void Stun(float duration);
    void TriggerHitFeedback(float damage, bool isCrit, bool isHighroll, CharacterStatsPD attacker, bool isPriority = false);

    // Lets a spawner mark a freshly hierarchy.Create()'d enemy with which category spawned it (deciding
    // which Active*Count bucket it counts against) and apply co-op/elite stat scaling, generically,
    // regardless of which concrete TState the enemy's subclass uses — same reason this interface exists
    // at all.
    void ConfigureSpawn(EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier);

    // Generic death-hold API — see EnemyBasePD.RequestDeathHold's own doc comment. Only safe to call from a
    // verified-state reaction (e.g. CharacterStatsPD.OnEnemyDeath), never from raw Simulate()-context code.
    void RequestDeathHold();
    void ReleaseDeathHold();
    // Starts the on-kill launch effect (OnKillLaunchEnemiesAugment) — see EnemyBasePD.StartKillLaunch's own
    // doc comment. Die() already reserved this effect's death-hold via the killer's killEventCount; the
    // caller doesn't need to request or track one itself, only trust HandleKillLaunch to release it.
    void StartKillLaunch(Vector3 direction, float speed, float distance, float damage, bool isCrit = false, bool isHighroll = false);
}

// Per-target contact damage cooldown — contact damage repeats over time (unlike a one-shot attack),
// so each player touching the enemy needs their own independent cooldown, not a single shared one.
public struct ContactCooldown
{
    public PlayerID player;
    public float remaining;
}

// Common enemy simulation fields; embedded inside each enemy's TState struct.
public struct EnemyBaseState : IDuplicate<EnemyBaseState>
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
    // Always exactly one killer — overwritten unconditionally on every TakeDamage call (not just the lethal
    // one), so by the time Die() runs this already reflects whoever landed the actual final blow. Never a
    // list/multiple; there's no scenario in this game where more than one player is credited for one kill.
    public PlayerID killerOwner;
    // Whether the SPECIFIC hit that killed this enemy was a crit — captured at the exact TakeDamage call
    // that reduced health to <= 0 (see that method's own comment), not inferred later, since isCrit is only
    // in scope as that call's own parameter.
    public bool killedByCrit;
    public bool initialized;
    // Live, ticking-down countdown — kept separate from EnemyBasePD.enrageTimer (the configured starting
    // duration, a plain never-mutated field) for the same reason WeaponRangedPD's fireTimer isn't just a
    // plain field: anything that changes every tick during Simulate() has to live in predicted State to
    // resimulate correctly, a plain field mutated in-place wouldn't roll back and replay consistently.
    public float enrageCountdown;
    public bool  enraged;
    // True while a subclass's attack routine wants _target pinned for its whole duration — see
    // EnemyBasePD.LockTarget's own doc comment for why this exists (FindTarget()'s periodic re-scan is not
    // gated to the Moving state, so without this an attack sequence spanning more than FindTargetInterval
    // could have its target silently swapped mid-sequence).
    public bool  targetLocked;

    // Generic "don't actually hide/delete yet" reference count — see EnemyBasePD.RequestDeathHold's own doc
    // comment. Independent scripts each request/release their own hold; hide+delete both wait until this
    // reaches 0 regardless of how many scripts are currently holding.
    public int deathHoldCount;

    // On-kill launch effect (e.g. OnKillLaunchEnemiesAugment) — generic on EnemyBasePD itself (not the
    // augment) since ANY enemy type can be the one killed, and the augment has no per-tick relationship
    // with the corpse once it fires StartKillLaunch(). See EnemyBasePD.HandleKillLaunch's own doc comment.
    public bool    killLaunchActive;
    public Vector3 killLaunchDirection;
    public float   killLaunchSpeed;
    public float   killLaunchRemainingDistance;
    public float   killLaunchDamage;
    // Locked in once at kill-time (same reasoning as PhantomBladePD: whether this hit crits is a property
    // of the whole launch, not re-rolled per enemy it passes through) and carried through to TakeDamage +
    // each hit's queued damage-number feedback — see HandleKillLaunch's own comment.
    public bool    killLaunchIsCrit;
    public bool    killLaunchIsHighroll;
    // Which enemies this launch has already damaged — travelling through a cluster shouldn't repeatedly
    // hit the same target every tick it stays overlapped.
    public DisposableList<PredictedObjectID> killLaunchHitIds;

    // Lets the packer duplicate this struct via a direct field-copy during snapshot/history/rollback
    // operations instead of falling back to a full serialize/deserialize round-trip — see
    // purrnet.dev/docs/client-side-prediction/disposable-collections. With potentially 50-100+ enemies
    // alive at once, every one of them getting the slow fallback path every tick is a real, compounding CPU
    // cost; this is a plain value-copy (`this`) with just the two DisposableList fields replaced by their
    // own .Duplicate() — struct-copying a DisposableList instead of calling .Duplicate() on it would alias
    // the SAME pooled underlying list between the original and the "copy" (the documented anti-pattern),
    // not create an independent one.
    public EnemyBaseState Duplicate()
    {
        var copy = this;
        copy.contactCooldowns = contactCooldowns.Duplicate();
        copy.killLaunchHitIds = killLaunchHitIds.Duplicate();
        return copy;
    }
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
    private PredictedTransform _predictedTransform;

    // The smoothly render-frame-interpolated "Visuals" child PredictedTransform continuously tracks toward
    // this enemy's tick-accurate physics root (see KingSpikePD's own comments on chargeWindupIndicator for
    // the same reasoning) — falls back to the raw root transform if this enemy has no PredictedTransform,
    // rather than anything that visually follows this enemy jittering between discrete simulation ticks.
    public Transform Visuals => _predictedTransform != null && _predictedTransform.graphics != null
        ? _predictedTransform.graphics
        : transform;

    // Incremented once per real pooled reuse/first spawn (see the !b.initialized block in Simulate()) —
    // see IEnemyPD.SpawnGeneration's own doc comment for why this exists.
    private int _spawnGeneration;
    public int SpawnGeneration => _spawnGeneration;

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
    // Generic per-enemy size scalar — not tied to any one visual/physics system, just a shared number
    // other generic code (e.g. HandleKillLaunch's sphere-cast radius) can read without needing its own
    // separate per-enemy tunable. Default 1, hand-tuned per enemy prefab.
    public float size = 1f;

    [Header("Contact Damage")]
    public bool contactColliderEnabled = true;
    public float contactCooldown = 0.5f;
    public bool stunResistant = false;
    // Dual-purpose: reduces knockback velocity (AddKnockbackVelocity) AND reduces the duration of any
    // other stun source (Stun()) — except the stun knockback itself produces, which is deliberately exempt
    // since that duration is already derived from the already-reduced knockback velocity; applying this
    // resist a second time on top of that would double-dip the same resistance against the same hit.
    [Range(0f, 1f)] public float crowdControlResist = 0f;
    public bool showDamageNumbers = true;
    // Nudges damage number spawn points along world-space +Y/+Z (not this enemy's own orientation) by
    // this many units, so they spawn off the model instead of centered inside it.
    [SerializeField] private float sizeYOffset = 0f;
    [SerializeField] private float sizeZOffset = 0f;

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
    public bool WasKilledByCrit => B.killedByCrit;

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
    // Plain view-layer bookkeeping, not predicted state — guards the instant predicted-hide below from
    // running every frame once already hidden, same pattern as PhantomBladePD/WeaponRangedProjectilePD's
    // _hiddenOnExpire.
    private bool  _hiddenOnDeath;
    // Edge-detected the same way _hiddenOnDeath is, just for OnDeathStop() instead — see that method's own
    // doc comment for why it fires earlier (immediately on death) and independently of _hiddenOnDeath.
    private bool  _deathStopped;
    private float _verifiedHealth;
    private float _verifiedEnrageCountdown;
    // Edge-detected the same way _hiddenOnDeath is — drives "stay red for the whole kill-launch" below,
    // rather than the normal brief FlashCoroutine per hit (which would fight this: it reverts to
    // _originalColors after 0.08s regardless of whether the corpse is still mid-launch).
    private bool  _wasKillLaunchActiveLastFrame;

    // Swapped to on Die() (see its own comment) and restored on pooled reuse — captured once in Awake()
    // since that only ever runs once per GameObject lifetime, unlike the pooled-reuse init block which
    // reruns every reuse and needs the TRUE original to restore to, not whatever Die() last left it as.
    // NOT a static field initializer — LayerMask.NameToLayer isn't allowed to run in a MonoBehaviour's
    // static constructor (fires too early, e.g. during script import/domain reload, before the engine's
    // layer table is ready), so this is resolved lazily on first real use instead, from Awake().
    private static int _noneLayer = -1;
    private int _originalLayer;

    private readonly RaycastHit[] _killLaunchSweepResults = new RaycastHit[16];
    // Queued in Simulate()-context (HandleKillLaunch), drained in UpdateView() at render rate — same
    // "queue during simulation, fire the actual non-idempotent side effect off render/verified timing"
    // split every weapon's own _hitFeedbackQueue already uses, so a resimulation replay of HandleKillLaunch
    // can't spawn duplicate damage numbers for the same conceptual hit.
    private readonly List<(IEnemyPD enemy, float damage, bool isCrit, bool isHighroll, PlayerID attackerOwner)> _killLaunchFeedbackQueue = new();

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
        b.killLaunchHitIds = DisposableList<PredictedObjectID>.Create(4);
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
        _predictedTransform = GetComponent<PredictedTransform>();
        _originalLayer = gameObject.layer;
        if (_noneLayer < 0) _noneLayer = LayerMask.NameToLayer("None");

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

    // Resets renderer visibility + color the INSTANT this GameObject (re)activates — Unity guarantees
    // OnEnable() fires before this frame ever renders, but a pooled reactivation and this identity's own
    // Simulate() tick (whose !b.initialized block also resets this) aren't always perfectly synchronized,
    // especially under client-side replay/reconciliation. Without this, a pooled corpse that was still
    // showing a hit-flash or kill-launch red at the moment it got hidden could show that same stale red for
    // a real visible frame or two on its NEXT life, before Simulate() gets around to resetting it — this
    // was the "enemies spawning red" symptom. _originalColors is populated once in Awake(), which Unity
    // always runs before the very first OnEnable() and never again after, so it's already valid by the time
    // this fires on every subsequent pooled reactivation too. Left in Simulate()'s own reset block as well
    // (redundant, not harmful) rather than removed, in case some future pooling path re-enables the
    // GameObject without this ever having fired.
    private void OnEnable()
    {
        // Defensive — in case a previous life was hidden/pooled mid-flash (_isFlashing still true, timer
        // never got to expire). Without this, TriggerFlash()'s own `if (_isFlashing) return;` guard would
        // think this life is already flashing red and skip re-painting it on the very next hit, even though
        // the color was just reset to _originalColors right above.
        _isFlashing = false;
        if (_renderers == null) return;
        foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = true;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = _originalColors[i];
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
            _spawnGeneration++;
            _target = null;
            _wasDeadLastFrame = false;
            _hiddenOnDeath    = false;
            _deathStopped     = false;
            _verifiedHealth   = maxHealth;
            gameObject.layer  = _originalLayer;

            // Defensive — in case a previous life died mid-kill-launch (still kinematic, physics collider
            // still disabled) and got pooled before that ever got a chance to clean itself up.
            if (_rigidbody != null) _rigidbody.isKinematic = false;
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

        if (b.isDead)
        {
            // The one thing still allowed to run post-death — see HandleKillLaunch's own doc comment for
            // why this lives here (generic on EnemyBasePD) rather than on whatever augment started it.
            if (b.killLaunchActive) HandleKillLaunch(ref b, delta);
            return;
        }

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
            // Skipped while targetLocked — see LockTarget's own doc comment.
            if (!b.targetLocked) FindTarget();
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

        // Fire hit feedback immediately at render rate — bypasses state propagation delay, same convention
        // as every weapon's own _hitFeedbackQueue drain.
        foreach (var (enemy, dmg, isCrit, isHighroll, attackerOwner) in _killLaunchFeedbackQueue)
        {
            if (CharacterStatsPD.All.TryGetValue(attackerOwner, out var attacker))
                enemy.TriggerHitFeedback(dmg, isCrit, isHighroll, attacker);
        }
        _killLaunchFeedbackQueue.Clear();

        // Instant, predicted hide — doesn't wait for server verification, unlike the state-critical
        // one-shot side effects below. Same fix as PhantomBladePD/WeaponRangedProjectilePD: viewState is
        // this machine's own already-simulated prediction (identical deterministic simulation runs on
        // every machine), available immediately, whereas verified only populates for non-owner clients
        // after a network round-trip — that round-trip was the visible "corpse lingers, still visible and
        // still hittable, after it should already be dead" delay. One-way (never un-hides here) since
        // enemy health only ever decreases (TakeDamage's `b.health -= damage`, no enemy-side regen), so a
        // resimulation flipping isDead back to false isn't a real scenario — a fresh pooled reuse (the
        // `!b.initialized` block above) is what resets this for a later spawn. Also gated on
        // deathHoldCount <= 0 — a death with no held effect in play (the overwhelming common case) still
        // hides exactly as instantly as before, since deathHoldCount only ever becomes nonzero via Die()'s
        // own bridging hold (see its comment), itself only placed when the killer's Stats says an effect
        // actually needs it.
        ref var viewB = ref GetBase(ref viewState);

        // Stops attack telegraphs immediately on death — see OnDeathStop's own doc comment for why this is
        // deliberately NOT gated on deathHoldCount the way the render-hide block below is. Deliberately NOT
        // gated on activeInHierarchy either (previously was, briefly) — a windup indicator that's been
        // unparented to world root (LeaperGloomPD's, during Leaping) is NOT a child of this GameObject, so
        // it doesn't get implicitly hidden when this enemy itself is deactivated/pooled; if this enemy gets
        // hidden and pooled before ever becoming active again this life (entirely possible — hide only
        // waits on deathHoldCount, not on this instance passing through an active frame first), gating
        // OnDeathStop() here meant it could simply never fire, leaving that detached indicator visible
        // forever. Individual overrides that call Animator.Play() guard THAT specific call's own
        // activeInHierarchy instead (seeing SpearGloomPD's own OnDeathStop for why), so the indicator-hide
        // half of OnDeathStop always runs immediately while only the animator snap can harmlessly skip.
        if (viewB.isDead && !_deathStopped)
        {
            _deathStopped = true;
            OnDeathStop();
        }

        if (viewB.isDead && viewB.deathHoldCount <= 0 && !_hiddenOnDeath)
        {
            _hiddenOnDeath = true;
            if (_renderers != null)
                foreach (var r in _renderers)
                    if (r != null) r.enabled = false;
            if (_hurtbox != null) _hurtbox.enabled = false;
            if (_physicsCollider != null) _physicsCollider.enabled = false;
            if (_contactDamageCollider != null) _contactDamageCollider.enabled = false;
        }

        // Stay red for the entire kill-launch, not just a brief per-hit flash — same instant/predicted
        // timing as the hide block above (viewState, not verified) since this is purely cosmetic. Edge-
        // detected so it only sets/reverts once each way, rather than fighting FlashCoroutine's own
        // revert-after-0.08s (which TriggerHitFeedback skips entirely while killLaunchActive — see its own
        // comment).
        if (viewB.killLaunchActive && !_wasKillLaunchActiveLastFrame)
        {
            if (_renderers != null)
                foreach (var r in _renderers)
                    if (r != null && r.material != null) r.material.color = Color.red;
        }
        else if (!viewB.killLaunchActive && _wasKillLaunchActiveLastFrame && _renderers != null)
        {
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = _originalColors[i];
        }
        _wasKillLaunchActiveLastFrame = viewB.killLaunchActive;

        // Death is state-critical — only act on server-confirmed dead.
        if (verified.HasValue)
        {
            var v = verified.Value;
            ref var vb = ref GetBase(ref v);

            if (vb.isDead && !_wasDeadLastFrame)
            {
                CharacterStatsPD.All.TryGetValue(vb.killerOwner, out var killer);
                CharacterStatsPD.RaiseOnEnemyDeath(killer, this);
                if (GameManagerPD.instance != null) GameManagerPD.instance.AwardUpgradePoints(xpValue);
            }

            // Deletion re-checked every tick once verified-dead (not just on the edge above) so it
            // naturally waits out however long any real hold takes to clear, however many ticks that is.
            if (vb.isDead && vb.deathHoldCount <= 0)
                hierarchy.Delete(this);

            _wasDeadLastFrame = vb.isDead;
            _verifiedHealth   = vb.health;
            _verifiedEnrageCountdown = vb.enrageCountdown;
        }
    }

    // ── Movement ───────────────────────────────────────────────────────────

    // Pins _target for the duration of a multi-state attack routine (dash/windup/attack/etc.) — call at the
    // moment a subclass commits to attacking (the same tick it reads _target to decide a dash direction,
    // stab target, etc.), and ClearTargetLock() once the routine returns to its idle/Moving state.
    // FindTarget()'s periodic re-scan runs unconditionally on a fixed timer regardless of attack/dash
    // sub-state (see Simulate() above) — without this, an attack sequence that spans more than
    // FindTargetInterval (default 1s) could have _target silently reassigned to a different, now-nearer
    // player partway through, so e.g. a dash-toward-target or a facing/attack decision made later in the
    // same sequence could act on someone other than who the sequence actually started against.
    protected void LockTarget(ref TState state) => GetBase(ref state).targetLocked = true;
    protected void ClearTargetLock(ref TState state) => GetBase(ref state).targetLocked = false;

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

        // Execute — CharacterStatsPD.TakeDamage is the single choke point every damage source (any weapon,
        // Phantom Blade, onhit effect, AOE augment, etc.) already funnels through, so hooking it here
        // covers a crit (or, in the future, ANY hit — see CharacterStatsPD.execute's own doc comment) from
        // anything uniformly, with no per-call-site changes needed anywhere else. Bosses use a separately
        // reduced threshold (BossExecuteThreshold/BossCritExecuteThreshold, -80%) instead of the raw
        // player-facing one, so a boss doesn't get finished off nearly as easily as a normal/elite enemy.
        if (b.health > 0f && CharacterStatsPD.All.TryGetValue(attackerOwner, out var attackerStats) && attackerStats.Execute)
        {
            bool isBoss = spawnCategory == EnemySpawnCategory.Boss;

            // Plain "any hit" execute — futureproofing, nothing sets attackerStats.execute yet.
            float executeThresh = isBoss ? attackerStats.BossExecuteThreshold : attackerStats.executeThreshold;
            if (b.health <= maxHealth * executeThresh)
                b.health = 0f;

            // Crit-gated execute — what CritExecuteAugment actually turns on.
            if (isCrit && b.health > 0f)
            {
                float critExecuteThresh = isBoss ? attackerStats.BossCritExecuteThreshold : attackerStats.critExecuteThreshold;
                if (b.health <= maxHealth * critExecuteThresh)
                    b.health = 0f;
            }
        }

        if (b.health <= 0)
        {
            b.health = 0;
            // Captured here, not inferred later — Die()/OnEnemyDeath need to know whether the specific hit
            // that actually killed this enemy was a crit (e.g. OnKillLaunchEnemiesAugment inheriting crit
            // status rather than rolling its own), and isCrit is only in scope as this call's own parameter.
            b.killedByCrit = isCrit;
            Die();
        }
    }

    // isPriority: true from actual weapon hits, false (default) from onhit-effect/Phantom Blade sources —
    // jumps this hit's damage number to the front of DamageNumberManager's queue for this enemy+attacker
    // ahead of whatever's already waiting, so real weapon damage always reads first regardless of how many
    // onhit effects also fired off the same hit.
    public void TriggerHitFeedback(float damage, bool isCrit, bool isHighroll, CharacterStatsPD attacker, bool isPriority = false)
    {
        // PD weapons (WeaponMeleePD, WeaponRangedProjectilePD) queue hit feedback in Simulate()/
        // LateSimulate() but only drain and fire it here in UpdateView(), at render rate — by the time it
        // fires, this pooled enemy instance can already have been reused for a new spawn (isDead reset to
        // false) while transiently still inactive, same as the respawn-reset/animator cases elsewhere in
        // this class.
        //
        // Gated on !_hiddenOnDeath, not !B.isDead — a kill-launch corpse is still dead (isDead is already
        // true for the killing blow itself, and stays true for every hit it takes while travelling) but
        // stays visible/renderer-enabled the whole time it's held, so it should still show hit feedback for
        // every hit it takes during that window, including the killing blow. Once actually hidden there's
        // nothing left to flash. Also skipped entirely while B.killLaunchActive — UpdateView's own
        // edge-detected block already holds the corpse solid red for the whole launch, and TriggerFlash's
        // own revert-to-_originalColors would fight that.
        if (!_hiddenOnDeath && !B.killLaunchActive && gameObject.activeInHierarchy) TriggerFlash();
        if (showDamageNumbers) ShowDamageNumber(damage, attacker.owner ?? default, isCrit, isHighroll, isPriority);
        if (attacker.isOwner && SFXManager.instance != null)
            SFXManager.instance.Play(attacker.hitSfx, transform.position, 1f, 0.05f);
    }

    protected virtual void Die()
    {
        B.isDead = true;
        ActiveCount--;
        AdjustCategoryCount(spawnCategory, -1);

        // Both moved here (previously only set once the hide block finally ran) per the user's own call —
        // a dead body's physical presence should end immediately regardless of whether hide/delete are
        // being held up for an on-death effect. The layer swap is what actually keeps this corpse out of
        // any layer-mask-based query (weapon hitboxes, this same kill-launch's own sphere-cast against
        // OTHER enemies, etc.) without needing to separately toggle _hurtbox.enabled too.
        if (_physicsCollider != null) _physicsCollider.enabled = false;
        gameObject.layer = _noneLayer;

        // Reserves exactly enough death-holds up front, atomically, for every effect that's going to react
        // to this death — no dependence on OnEnemyDeath listener dispatch (in UpdateView, potentially many
        // ticks later) actually running before hide/delete gets a chance to check deathHoldCount. See
        // CharacterStatsPD.deathEventCount/killEventCount's own doc comment for the summed-vs-killer-only
        // split. This is a pure read of counts that are only ever incremented once each (at augment
        // OnApply()), so it's safe here even though Die() runs from Simulate()-context.
        int holds = 0;
        foreach (var stats in CharacterStatsPD.All.Values)
            holds += stats.deathEventCount;
        bool foundKiller = CharacterStatsPD.All.TryGetValue(B.killerOwner, out var killerStats);
        if (foundKiller)
            holds += killerStats.killEventCount;
        B.deathHoldCount += holds;

        if (XPOrbManagerPD.instance != null)
            XPOrbManagerPD.instance.SpawnOrbs(hierarchy, transform.position, xpValue);
    }

    // ── Death hold ─────────────────────────────────────────────────────────
    // Generic "don't hide/delete yet" API — any number of independent scripts can each request their own
    // hold and release it on their own schedule; hide+delete both wait until deathHoldCount reaches 0
    // regardless of how many are currently outstanding, so one script releasing never ends another's hold.
    // Safe to call from a verified-state reaction (e.g. CharacterStatsPD.OnEnemyDeath, which — like every
    // other "fires exactly once per real occurrence" event in this codebase — is safe to mutate predicted
    // state from), NOT from raw Simulate()-context code, which risks double-counting during resimulation.
    public void RequestDeathHold() => B.deathHoldCount++;
    public void ReleaseDeathHold() => B.deathHoldCount = Mathf.Max(0, B.deathHoldCount - 1);

    // Starts the on-kill launch effect (OnKillLaunchEnemiesAugment) — called from a verified OnEnemyDeath
    // reaction (safe, see RequestDeathHold's own comment). Does NOT request its own hold — Die() already
    // reserved exactly one for this effect via the killer's killEventCount (see Die()'s own comment), since
    // OnKillLaunchEnemiesAugment.OnApply() incremented that count. HandleKillLaunch releases that reserved
    // hold once the corpse finishes travelling.
    public void StartKillLaunch(Vector3 direction, float speed, float distance, float damage, bool isCrit = false, bool isHighroll = false)
    {
        ref var b = ref B;
        b.killLaunchActive = true;
        b.killLaunchDirection = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;
        b.killLaunchSpeed = speed;
        b.killLaunchRemainingDistance = distance;
        b.killLaunchDamage = damage;
        b.killLaunchIsCrit = isCrit;
        b.killLaunchIsHighroll = isHighroll;

        if (_rigidbody != null)
        {
            // Guarded by !isKinematic — same reason Simulate()'s own pause-handling does this (see its
            // comment): SpearGloomPD/KingSpikePD-style Windup/Stabbing sub-states can already have this
            // rigidbody kinematic when the killing blow lands mid-attack, and PredictedRigidbody's velocity
            // setters throw ("not supported") if attempted on an already-kinematic body.
            if (!_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = true;
        }
    }

    // Moves the corpse at a constant speed toward killLaunchRemainingDistance, sphere-casting each step
    // against other enemies (radius 0.5 * size) and dealing killLaunchDamage once per enemy hit — the
    // damage itself was already rolled once at kill-time (StartKillLaunch's caller), not re-rolled here.
    // Lives on EnemyBasePD itself, not the augment, since ANY enemy type can be the one killed and the
    // augment (a separate PredictedIdentity on the player) has no ongoing per-tick relationship with this
    // specific corpse once it fires StartKillLaunch() — the corpse has to track and end its own effect.
    // This enemy's own hurtbox/collider is already off the layer HandleKillLaunch's sphere-cast mask
    // targets (Die() swapped it to NoneLayer), so it can never sphere-cast-detect itself.
    private void HandleKillLaunch(ref EnemyBaseState b, float delta)
    {
        float step = Mathf.Min(b.killLaunchSpeed * delta, b.killLaunchRemainingDistance);
        Vector3 prevPos = transform.position;
        Vector3 newPos  = prevPos + b.killLaunchDirection * step;

        if (_rigidbody != null) _rigidbody.MovePosition(newPos);
        else transform.position = newPos;

        b.killLaunchRemainingDistance -= step;

        if (step > 0.0001f)
        {
            float radius = 0.5f * size;
            int n = Physics.SphereCastNonAlloc(prevPos, radius, b.killLaunchDirection, _killLaunchSweepResults, step, CombatLayerMasks.EnemyHurtboxMask);
            for (int i = 0; i < n; i++)
            {
                if (!_killLaunchSweepResults[i].collider.TryGetComponent(out IEnemyPD enemy)) continue;
                if (enemy._isDead) continue;
                if (b.killLaunchHitIds.Contains(enemy.ObjectId)) continue;
                b.killLaunchHitIds.Add(enemy.ObjectId);
                enemy.TakeDamage(b.killLaunchDamage, b.killLaunchIsCrit, b.killerOwner);
                // TakeDamage alone never shows a damage number (see TriggerHitFeedback's own doc comment on
                // IEnemyPD) — queued here, drained in UpdateView(), same split every weapon's own
                // _hitFeedbackQueue already uses.
                _killLaunchFeedbackQueue.Add((enemy, b.killLaunchDamage, b.killLaunchIsCrit, b.killLaunchIsHighroll, b.killerOwner));
            }
        }

        if (b.killLaunchRemainingDistance <= 0f)
        {
            b.killLaunchActive = false;
            if (_rigidbody != null) _rigidbody.isKinematic = false;
            ReleaseDeathHold();
        }
    }

    // Public stun entry point — every caller outside this class goes through here, so crowdControlResist
    // applies uniformly to any non-knockback CC source. AddKnockbackVelocity below calls ApplyStun()
    // directly instead, bypassing this reduction (see crowdControlResist's own doc comment for why).
    public void Stun(float duration) => ApplyStun(duration * (1f - crowdControlResist));

    private void ApplyStun(float duration)
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
        float effectiveSpeed = impulse.magnitude * (1f - crowdControlResist);
        // Guarded by !isKinematic — same reason StartKillLaunch/Simulate()'s pause-handling do this: a
        // Windup/Stabbing/Spinning-style kinematic sub-state (SpearGloomPD, KingSpikePD, DaggerGloomPD's
        // spin, etc.) means this rigidbody can already be kinematic when a knockback source (e.g.
        // ShieldBreakBurstAugment's AoE) hits, and PredictedRigidbody's velocity setter throws ("not
        // supported") if attempted on an already-kinematic body. The stun below still applies regardless —
        // ApplyStun's own IsReadyToAttack guard already decides whether an active attack sequence should be
        // interruptible, independent of whether the physical push could happen.
        if (!_rigidbody.isKinematic)
        {
            // Zeroed first rather than added to, so knockback doesn't have to fight whatever velocity the
            // enemy's own movement AddForce was already carrying that tick.
            _rigidbody.velocity = impulse.normalized * effectiveSpeed;
        }
        const float drag      = 9f;
        const float threshold = 1f;
        float stunDuration = effectiveSpeed > threshold
            ? Mathf.Log(effectiveSpeed / threshold) / drag
            : 0f;
        // ApplyStun, not Stun — this duration is already derived from effectiveSpeed above (itself already
        // reduced by crowdControlResist), so routing it back through Stun()'s own resist multiplier would
        // apply the same resistance twice against the same knockback hit.
        ApplyStun(stunDuration);
    }

    // ── Visuals ────────────────────────────────────────────────────────────
    // Fires the instant this enemy is dead (viewB.isDead edge, predicted/instant timing, NOT gated on
    // deathHoldCount) — a held corpse (kill-launch, any future on-death effect) should already read as
    // fully "dead" behaviorally the whole time it's held: Simulate() already stops updating movement/attack
    // state once isDead, so this is the visuals-side counterpart — stop any attack telegraph (windup
    // indicator, charge fill, etc.) immediately rather than leaving it visibly active/frozen on a corpse
    // that's still flying around. Deliberately separate from the render-hide block (renderers/hurtbox/
    // physicsCollider disable), which still waits for deathHoldCount <= 0 — the model and healthbar are
    // meant to stay visible for the whole hold, only attack telegraphs stop early.
    protected virtual void OnDeathStop() { }
    protected virtual void UpdateAnimator(TState viewState) { }

    // Called once per (re)spawn, from within the guaranteed-reliable respawn block in Simulate().
    // Subclasses with their own pooled state (attack phases, hitboxes, visual-state caches, etc.)
    // should reset it here instead of OnRemovedFromPool(), which never fires for pooled reuse.
    protected virtual void OnRespawnReset() { }

    private void ShowDamageNumber(float amount, PlayerID attackerOwner, bool isCrit = false, bool isHighroll = false, bool isPriority = false)
    {
        // Passes this (IEnemyPD), not a bare Transform — DamageNumberManager/DamageNumber read .Visuals off
        // it for position (smoothly interpolated PredictedTransform graphics child, not the raw tick-
        // stepped physics root) and .SpawnGeneration to detect pooled reuse: a raw Transform reference
        // alone can't tell a still-alive enemy apart from this same pooled GameObject having since been
        // reused for a completely different spawn, which was causing already-tracking numbers to snap
        // away/disappear when their original enemy died and got recycled before their animation finished.
        if (DamageNumberManager.instance != null)
            DamageNumberManager.instance.Spawn(amount, this, sizeYOffset, sizeZOffset, HashCode.Combine(GetInstanceID(), attackerOwner.GetHashCode()), isCrit, isHighroll, attackerOwner, isPriority);
    }

    private const float FlashDuration = 0.08f;
    private float _flashTimer;
    private bool  _isFlashing;

    // Timer-based, not a StartCoroutine(...) per hit — a coroutine-per-hit meant N hits landing within the
    // same ~0.08s window (very much the normal case for "swarm scale" enemy types getting hit by AoE/multi-
    // hit weapons) spawned N independent coroutines, each on its OWN 0.08s clock. Once the first one's
    // timer expired it would revert to _originalColors[i] while later ones were still mid-flight — if hits
    // kept arriving often enough, there was always at least one still "just started" the moment another
    // reverted, so the enemy could visibly never stop flickering red even though each individual coroutine
    // was behaving correctly in isolation. A single shared timer just gets EXTENDED by every new hit
    // instead of stacking a fresh independent clock, so the revert only ever happens once, `FlashDuration`
    // after the LAST hit actually landed.
    private void TriggerFlash()
    {
        _flashTimer = FlashDuration;
        if (_isFlashing) return;
        _isFlashing = true;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = Color.red;
    }

    private void Update()
    {
        if (!_isFlashing) return;
        _flashTimer -= Time.deltaTime;
        if (_flashTimer > 0f) return;
        _isFlashing = false;

        // Skip the revert if a kill-launch is now active — TriggerHitFeedback's own !B.killLaunchActive
        // guard only stops a NEW flash from starting once the launch is already underway, but the killing
        // blow's own hit-feedback drains off the WEAPON's UpdateView, which can run before or after this
        // enemy's own UpdateView (where OnEnemyDeath dispatch actually flips killLaunchActive true) — pure
        // Unity script execution order, not something worth pinning down. If this flash already started
        // before that flip landed, it would otherwise revert the corpse back to its original color shortly
        // into a launch that's supposed to stay solid red the whole time. UpdateView's own edge-detected
        // red/revert already owns the corpse's color for the launch's actual duration once this bails.
        if (B.killLaunchActive) return;

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
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        // contactCooldowns was never disposed here (pre-existing gap, fixed in passing) — killLaunchHitIds
        // needs the same treatment as every other enemy subclass's own Dispose().
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;
}
