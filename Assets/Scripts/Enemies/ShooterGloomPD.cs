using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Enemies/ShooterGloom.cs. Waits at preferred range, fires on cooldown; if
// the player gets too close first dashes away. EnemyBasePD only re-targets on spawn or when the current
// target goes null/dead (no periodic re-scan), so the whole DashingAway/Windup/Shooting/BetweenShots/
// PostShot sequence stays aimed at the same player it committed to throughout — no locking needed.
// Multishot uses the shared MultishotMath formula (WeaponBasePD.cs) — same behavior as the player's own
// WeaponRangedPD.
public class ShooterGloomPD : EnemyBasePD<ShooterGloomPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   shooterStateId;
        public float shooterStateTimer;
        public float attackCooldownTimer;
        public int   shotsRemaining;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;
    protected override bool IsReadyToAttack
    {
        get
        {
            var ss = (ShooterState)currentState.shooterStateId;
            return ss != ShooterState.Moving && ss != ShooterState.PostShot;
        }
    }

    private static readonly int IdleHash     = Animator.StringToHash("Idle");
    private static readonly int WindupHash   = Animator.StringToHash("ShooterWindup");
    private static readonly int ShootHash    = Animator.StringToHash("ShooterShoot");
    private static readonly int PostShotHash = Animator.StringToHash("ShooterPostShot");
    private static readonly int WindupSpeedHash   = Animator.StringToHash("WindupSpeed");
    private static readonly int ShootSpeedHash    = Animator.StringToHash("ShootSpeed");
    private static readonly int PostShotSpeedHash = Animator.StringToHash("PostShotSpeed");

    [Header("Shooter Settings")]
    // The "too close" trigger threshold — separate from preferredRange (which governs the normal approach/
    // backpedal hold distance via HandleMovement) since a player closing distance faster than backpedal can
    // keep up needs an actual evasive dash, not just movement hysteresis.
    [SerializeField] private float minAttackRange     = 3f;
    [SerializeField] private float attackCooldown     = 5f;
    [SerializeField] private float windupDuration     = 0.5f;
    [SerializeField] private float shootDuration      = 0.1f;
    [SerializeField] private float betweenShotPause   = 0.3f;
    [SerializeField] private float postShotPause      = 0.3f;
    [SerializeField] private int   shotsPerAttack     = 1;
    [SerializeField] private float shotDamageMultiplier = 1f;
    // Multiplier against baseMoveSpeed, matching every other dashing enemy's own dashSpeedMultiplier
    // convention (see StartDashToPoint/StartDashToTarget callers) — NOT old's 33x, which was tuned against
    // old's differently-scaled move speed values.
    [SerializeField] private float dashSpeedMultiplier = 3f;
    // Extra clearance beyond exactly reaching minAttackRange, same buffer old's own EnterDash() used.
    [SerializeField] private float dashDistanceBuffer  = 0.5f;

    [Header("Projectile")]
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField] private float projectileSpeed    = 8f;
    [SerializeField] private float projectileMaxRange = 30f;
    // Spawn offset in front of the enemy, along the fire direction — avoids the projectile immediately
    // self-colliding with the firer's own hurtbox/collider at spawn.
    [SerializeField] private float spawnOffset        = 0.75f;
    // 1 = default size, 1.5 = 50% larger, etc. — manually tuned per prefab variant (elite variants set this
    // higher to match their bigger ScaledVisuals), not derived from anything at runtime.
    [SerializeField] private float projectileScale    = 1f;
    // Null = leave the projectile prefab's own default material. Set per color-variant prefab (e.g. an
    // elite/recolored Shooter Gloom) so its shots visually match — not derived from the enemy's own
    // gun/hat material at runtime, same manual-per-variant convention as projectileScale above.
    [SerializeField] private Material projectileMaterial;

    [Header("Multishot")]
    // 0 = fire 1 bullet straight, no extra shots. See MultishotMath (WeaponBasePD.cs) for exactly how extra
    // shots fan out — one always center, the rest split left-first between the two sides.
    [SerializeField] private int   multishotCount       = 0;
    [SerializeField] private float multishotSpreadAngle = 135f;

    public enum ShooterState { Moving, DashingAway, Windup, Shooting, BetweenShots, PostShot }

    private ShooterState GetShooterState(ref State s) => (ShooterState)s.shooterStateId;
    private void SetShooterState(ref State s, ShooterState st) => s.shooterStateId = (int)st;

    // Scratch buffer for MultishotMath.ComputeAngles — static, immediately drained within one synchronous
    // call, same convention as WeaponRangedPD's own _multishotAngles.
    private static readonly List<float> _multishotAngles = new();

    protected override void LateAwake()
    {
        base.LateAwake();
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.shotsRemaining      = shotsPerAttack;
    }

    // Sets the WindupSpeed/ShootSpeed/PostShotSpeed Animator float parameters so each clip's actual
    // playback duration matches windupDuration/shootDuration/postShotPause exactly (speed = clip.length /
    // desiredDuration) — same pattern as LeaperGloomPD.ApplyAnimatorClipSpeeds(). Deliberately NOT in
    // LateAwake() — EnemyBasePD.Simulate()'s first-tick init block calls _animator.Rebind() (to force a
    // clean pose after pooled reuse), which resets ALL Animator Controller parameters back to their
    // defaults, wiping out whatever LateAwake() computed. OnRespawnReset() runs right after that Rebind()
    // call (on every reuse, including the very first spawn), so re-applying here is what actually sticks.
    private void ApplyAnimatorClipSpeeds()
    {
        if (_animator == null) return;
        foreach (var clip in _animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == "Shooter Gloom Windup")
                _animator.SetFloat(WindupSpeedHash, clip.length / windupDuration);
            else if (clip.name == "Shooter Gloom Shoot")
                _animator.SetFloat(ShootSpeedHash, clip.length / shootDuration);
            else if (clip.name == "Shooter Gloom PostShot")
                _animator.SetFloat(PostShotSpeedHash, clip.length / postShotPause);
        }
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded) EnterWindup(ref state);

        state.attackCooldownTimer -= delta;
        state.shooterStateTimer   -= delta;

        var ss = GetShooterState(ref state);

        switch (ss)
        {
            case ShooterState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    state.shotsRemaining = shotsPerAttack;
                    float dist = Vector3.Distance(transform.position, _target.position);
                    if (dist < minAttackRange) EnterDashAway(ref state, dist);
                    else                        EnterWindup(ref state);
                }
                break;

            case ShooterState.DashingAway:
                // Movement itself is driven by the base HandleDash() call in Simulate(); nothing to do here
                // beyond wait for dashJustEnded (handled above) to advance to Windup.
                break;

            case ShooterState.Windup:
                if (state.shooterStateTimer <= 0f) EnterShooting(ref state);
                break;

            case ShooterState.Shooting:
                if (state.shooterStateTimer <= 0f)
                {
                    if (state.shotsRemaining > 0) EnterBetweenShots(ref state);
                    else                           EnterPostShot(ref state);
                }
                break;

            case ShooterState.BetweenShots:
                // No minAttackRange re-check here — the dash-away decision is made once, up front in
                // Moving (see below), and this multishot burst commits to firing all of shotsPerAttack
                // regardless of how close the target gets in between shots.
                if (state.shooterStateTimer <= betweenShotPause * 0.5f) FaceTargetSmooth(delta);
                if (state.shooterStateTimer <= 0f) EnterWindup(ref state);
                break;

            case ShooterState.PostShot:
                if (state.shooterStateTimer <= postShotPause * 0.5f) FaceTargetSmooth(delta);
                if (state.shooterStateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ss = GetShooterState(ref state);

        // Windup is a stationary telegraphed-attack window — kinematic (Case 2, immovable but still
        // physically present), matching every other enemy's convention. DashingAway is fast movement,
        // handled entirely by the base dash primitives (collider-disabled instead, Case 1) — HandleDash
        // already disables/re-enables _physicsCollider around the dash itself.
        isStunnable = ss == ShooterState.Moving || ss == ShooterState.BetweenShots || ss == ShooterState.PostShot;
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = ss == ShooterState.Windup || ss == ShooterState.Shooting;
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }

        // Belt-and-suspenders with StartDashToPoint/HandleDash's own internal disable/re-enable — matches
        // every other dashing enemy's explicit per-tick assignment (SpearGloomPD, DaggerGloomPD).
        if (_physicsCollider != null)
            _physicsCollider.enabled = ss != ShooterState.DashingAway;
    }

    // ── State transitions ────────────────────────────────────────────────────

    private void EnterDashAway(ref State state, float currentDistance)
    {
        SetShooterState(ref state, ShooterState.DashingAway);

        Vector3 awayDir = transform.position - _target.position;
        awayDir.y = 0f;
        awayDir.Normalize();
        float dashDistance = (minAttackRange - currentDistance) + dashDistanceBuffer;
        Vector3 destination = transform.position + awayDir * dashDistance;

        StartDashToPoint(ref state, baseMoveSpeed * dashSpeedMultiplier, destination);
    }

    private void EnterWindup(ref State state)
    {
        state.shooterStateTimer = windupDuration;
        SnapFaceTarget(ref state);
        SetShooterState(ref state, ShooterState.Windup);
    }

    private void EnterShooting(ref State state)
    {
        state.shooterStateTimer = shootDuration;
        state.shotsRemaining--;
        SetShooterState(ref state, ShooterState.Shooting);
        FireShots();
    }

    private void EnterBetweenShots(ref State state)
    {
        state.shooterStateTimer = betweenShotPause;
        SetShooterState(ref state, ShooterState.BetweenShots);
    }

    private void EnterPostShot(ref State state)
    {
        state.shooterStateTimer   = postShotPause;
        state.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        SetShooterState(ref state, ShooterState.PostShot);
    }

    private void EnterMoving(ref State state)
    {
        // Attack routine's over — reprioritize onto whoever's closest now, rather than sitting on whoever
        // this routine happened to start against. See EnemyBasePD.FindClosestTarget's own doc comment.
        FindClosestTarget();
        SetShooterState(ref state, ShooterState.Moving);
    }

    // ── Firing ────────────────────────────────────────────────────────────────

    private void FireShots()
    {
        if (projectilePrefab == null || _target == null) return;

        Vector3 origin  = transform.position;
        Vector3 baseDir = _target.position - origin;
        baseDir.y = 0f;
        baseDir.Normalize();
        if (baseDir == Vector3.zero) return;

        float dmg = damage * shotDamageMultiplier;
        MultishotMath.ComputeAngles(multishotCount, multishotSpreadAngle, _multishotAngles);

        foreach (float angle in _multishotAngles)
        {
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * baseDir;
            Vector3 spawnPos = origin + dir * spawnOffset;
            var id = hierarchy.Create(projectilePrefab, spawnPos, Quaternion.LookRotation(dir));
            if (!id.HasValue) continue;

            var go = hierarchy.GetGameObject(id.Value);
            if (go != null && go.TryGetComponent(out EnemyProjectilePD projectile))
                projectile.Launch(dmg, dir, projectileSpeed, projectileMaxRange, projectileScale, projectileMaterial);
        }
    }

    // ── View ──────────────────────────────────────────────────────────────────
    private ShooterState _lastVisualState = (ShooterState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        var ss = (ShooterState)viewState.shooterStateId;
        if (ss == _lastVisualState) return;
        _lastVisualState = ss;

        if (_animator == null) return;
        switch (ss)
        {
            case ShooterState.Moving:
                _animator.Play(IdleHash, 0, 0f);
                break;
            case ShooterState.Windup:
                // Only the FIRST windup of an attack sequence plays the actual lower-into-firing-position
                // clip — shotsRemaining == shotsPerAttack means no shot has fired yet this sequence (it's
                // set fresh right before the sequence starts, then EnterShooting decrements it after each
                // shot). Repeat windups between shots in the same multishot burst skip the Play call
                // entirely: the weapon is already at firing level from the previous Shoot's recoil, and
                // forcing WindupHash to restart at frame 0 (which begins at the raised idle pose) would
                // teleport it back up mid-burst instead of holding position between shots.
                if (viewState.shotsRemaining == shotsPerAttack)
                    _animator.Play(WindupHash, 0, 0f);
                break;
            case ShooterState.Shooting:
                _animator.Play(ShootHash, 0, 0f);
                break;
            case ShooterState.PostShot:
                _animator.Play(PostShotHash, 0, 0f);
                break;
        }
    }

    // No windup indicator to hide (unlike Spear/Dagger/Leaper) — only needed here to snap out of whatever
    // attack pose this enemy died mid-animation in (shooterStateId is frozen from here on, so nothing else
    // will do this), same reasoning as SpearGloomPD's own OnDeathStop.
    protected override void OnDeathStop()
    {
        // Guarded on activeInHierarchy — see SpearGloomPD's own comment on the same guard.
        if (_animator != null && gameObject.activeInHierarchy) _animator.Play(IdleHash, 0, 0f);
    }

    protected override void OnRespawnReset()
    {
        ApplyAnimatorClipSpeeds();
        currentState.shooterStateId      = 0;
        currentState.shooterStateTimer   = 0f;
        currentState.shotsRemaining      = shotsPerAttack;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        // In case death happened mid-Windup/Shooting, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (shooterStateId resets to Moving above), but resetting
        // explicitly here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (ShooterState)(-1);
    }
}
