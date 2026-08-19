using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Enemies/ShooterGloom.cs. Waits at preferred range, fires on cooldown; if
// the player gets too close first dashes away. The DashingAway/Windup/Shooting sequence for one shot (or
// one multishot burst) is an "ongoing attack" (see EnemyBasePD.CaptureAttackAnchor) — it always resolves
// against whichever target it committed to when it started, even if _target gets reassigned mid-sequence
// (taunt/invisible). BetweenShots re-evaluates _target fresh for whether/where the NEXT shot in the chain
// aims. Multishot uses the shared MultishotMath formula (WeaponBasePD.cs) — same behavior as the player's
// own WeaponRangedPD.
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
    [SerializeField] private float minAttackRange     = 8f;
    [SerializeField] private float windupDuration     = 0.25f;
    [SerializeField] private float shootDuration      = 0.1f;
    [SerializeField] private float betweenShotPause   = 0.25f;
    [SerializeField] private float postShotPause      = 0.5f;
    [SerializeField] private int   shotsPerAttack     = 1;
    [SerializeField] private float shotDamageMultiplier = 1f;
    // Multiplier against baseMoveSpeed, matching every other dashing enemy's own dashSpeedMultiplier
    // convention (see StartDashToPoint/StartDashToTarget callers) — NOT old's 33x, which was tuned against
    // old's differently-scaled move speed values.
    [SerializeField] private float dashSpeedMultiplier = 4f;
    // Extra clearance beyond exactly reaching minAttackRange, same buffer old's own EnterDash() used.
    [SerializeField] private float dashDistanceBuffer  = 0.5f;

    [Header("Projectile")]
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField] private float projectileSpeed    = 8f;
    [SerializeField] private float projectileMaxRange = 20f;
    // Spawn offset in front of the enemy, along the fire direction — avoids the projectile immediately
    // self-colliding with the firer's own hurtbox/collider at spawn.
    [SerializeField] private float spawnOffset        = 0.75f;
    // Extra multiplier ON TOP of this enemy's own size (1 = no extra adjustment, scales 1:1 with size) —
    // multiplied by size at the actual Launch() call site below (2026-08-20), so an Elite's bigger
    // ScaledVisuals no longer needs this hand-tuned to match separately; it now tracks size automatically.
    [SerializeField] private float projectileScale    = 1f;

    // Baseline "clip length / baseline duration" ratios, cached instead of SetFloat'd directly — every one
    // of Shooter's own attack beats scales with attack speed (Shooting's damage is a one-shot projectile
    // spawn that already resolved the instant EnterShooting() ran, so there's no lingering window to
    // protect — see SpearGloomPD's own doc comment for the fixed-vs-scaled reasoning), so all three need to
    // be re-derived live every frame (ratio * EffectiveAttackSpeed, see UpdateView) instead of set once.
    private float _windupClipRate;
    private float _shootClipRate;
    private float _postShotClipRate;

    [Header("Multishot")]
    // 0 = fire 1 bullet straight, no extra shots (tier1-authored baseline — "base 1" total shot). See
    // MultishotMath (WeaponBasePD.cs) for exactly how extra shots fan out — one always center, the rest
    // split left-first between the two sides. Scaled per tier by multishotCountTierGrowth below (applied in
    // OnConfigureSpawn()) — see _authoredMultishotCount's own doc comment for why this needs the
    // snapshot-and-reset treatment instead of just incrementing this field directly.
    [SerializeField] private int   multishotCount       = 0;
    [SerializeField] private float multishotSpreadAngle = 90f;
    // Captured once in OnAwake(), reset back onto multishotCount every pooled reuse in LateAwake() —
    // otherwise OnConfigureSpawn's += would keep compounding across reuses, same "_authoredX snapshot"
    // pattern established for the attack-count fields on Blitz/Dagger/Mortar/Spear/Orbiter.
    private int _authoredMultishotCount;

    [Header("Attack Cooldown")]
    [SerializeField] private float attackCooldown = 5f;
    // Captured once in OnAwake(), reset back onto attackCooldown every pooled reuse in LateAwake(), scaled
    // by attackCooldownMultiplier (Threat Level 5) in OnConfigureSpawn() — same "_authoredX snapshot"
    // pattern as _authoredMultishotCount above.
    private float _authoredAttackCooldown;

    [Header("Tier Scaling")]
    // Added onto multishotCount per tier above 1 — see OnConfigureSpawn().
    [SerializeField] private int multishotCountTierGrowth = 2;

    public enum ShooterState { Moving, DashingAway, Windup, Shooting, BetweenShots, PostShot }

    private ShooterState GetShooterState(ref State s) => (ShooterState)s.shooterStateId;
    private void SetShooterState(ref State s, ShooterState st) => s.shooterStateId = (int)st;

    // Scratch buffer for MultishotMath.ComputeAngles — static, immediately drained within one synchronous
    // call, same convention as WeaponRangedPD's own _multishotAngles.
    private static readonly List<float> _multishotAngles = new();

    protected override void OnAwake()
    {
        base.OnAwake();
        _authoredMultishotCount = multishotCount;
        _authoredAttackCooldown = attackCooldown;
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        // Reset to the tier1-authored baseline before OnConfigureSpawn (which always runs AFTER LateAwake(),
        // once hierarchy.Create() returns) scales it fresh for this life — see _authoredMultishotCount's own
        // doc comment.
        multishotCount = _authoredMultishotCount;
        attackCooldown = _authoredAttackCooldown;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.shotsRemaining      = shotsPerAttack;
    }

    // Tier1-authored multishotCount, scaled by multishotCountTierGrowth per tier above 1 (see that field's
    // own doc comment). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call). Read live at fire time (FireShots), not mirrored into State, so no extra reset
    // needed anywhere else.
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        multishotCount += (spawnTier - 1) * multishotCountTierGrowth;

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        attackCooldown = _authoredAttackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
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
                _windupClipRate = clip.length / windupDuration;
            else if (clip.name == "Shooter Gloom Shoot")
                _shootClipRate = clip.length / shootDuration;
            else if (clip.name == "Shooter Gloom PostShot")
                _postShotClipRate = clip.length / postShotPause;
        }
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded) EnterWindup(ref state);

        var ss = GetShooterState(ref state);

        // attackCooldownTimer: only ticks while actually in Moving (2026-08-19 — uniform across every
        // multi-phase enemy's own attack-cadence cooldown now, see OrbiterGloomPD's own fix). shooterStateTimer:
        // scaled live by EffectiveAttackSpeed every tick — see SpearGloomPD's own comment on the identical
        // pattern. DashingAway needs no separate handling — HandleDash already applies EffectiveAttackSpeed
        // live to the dash's own velocity.
        if (ss == ShooterState.Moving)
            state.attackCooldownTimer -= delta;
        state.shooterStateTimer -= delta * EffectiveAttackSpeed;

        switch (ss)
        {
            case ShooterState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    // Decrement/refill lives here (the actual start of the attempt — dash-away OR windup,
                    // whichever runs first — not EnterShooting's own fire-resolve point) so an attempt
                    // cancelled via Interrupt() mid-dash or mid-windup still counts against the budget. See
                    // EnterMoving's own comment for why the reset moved here too.
                    if (state.shotsRemaining <= 0) state.shotsRemaining = shotsPerAttack;
                    state.shotsRemaining--;
                    // Ongoing attack starts here — see CaptureAttackAnchor's own doc comment.
                    CaptureAttackAnchor(ref state);
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
                    // IsTargetStillValid — a target that went invisible-and-non-taunting since the last shot
                    // shouldn't get chained into another one against them; see that property's own doc comment.
                    // !IsCrowdControlled — a true Stun() landing mid-sequence never interrupts the shot that
                    // just finished above, but stops the chain from continuing into another one, falling
                    // through to PostShot instead (a Slow does NOT stop the chain — see IsCrowdControlled's
                    // own doc comment, hard CC only).
                    if (state.shotsRemaining > 0 && IsTargetStillValid && !IsCrowdControlled) EnterBetweenShots(ref state);
                    else                                                EnterPostShot(ref state);
                }
                break;

            case ShooterState.BetweenShots:
                // No minAttackRange re-check here — the dash-away decision is made once, up front in
                // Moving (see below), and this multishot burst commits to firing all of shotsPerAttack
                // regardless of how close the target gets in between shots. Outside the ongoing window
                // (ongoing ends the moment Shooting finishes) — safe to read live _target here.
                if (state.shooterStateTimer <= betweenShotPause * 0.5f) FaceTargetSmooth(delta);
                if (state.shooterStateTimer <= 0f)
                {
                    if (_target == null)
                    {
                        EnterPostShot(ref state);
                    }
                    else
                    {
                        // Same refill-if-exhausted + decrement as the Moving case above — this is the other
                        // funnel into a fresh attempt (continuing an existing burst rather than starting a
                        // brand new one), same reasoning applies.
                        if (state.shotsRemaining <= 0) state.shotsRemaining = shotsPerAttack;
                        state.shotsRemaining--;
                        CaptureAttackAnchor(ref state);
                        EnterWindup(ref state);
                    }
                }
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

        // EffectiveMoveSpeed, not baseMoveSpeed (2026-08-13) — see BlitzGloomPD.EnterBlitz's own comment.
        StartDashToPoint(ref state, EffectiveMoveSpeed * dashSpeedMultiplier, destination);
    }

    private void EnterWindup(ref State state)
    {
        state.shooterStateTimer = windupDuration;
        SnapFaceAnchor(ref state);
        SetShooterState(ref state, ShooterState.Windup);
    }

    private void EnterShooting(ref State state)
    {
        state.shooterStateTimer = shootDuration;
        // Decrement moved to the attack-start sites (Moving/BetweenShots, above) — see their own comment.
        SetShooterState(ref state, ShooterState.Shooting);
        FireShots(ref state);
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

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above) and
    // meant to be cancelable — CanBeInterrupted now defaults to false (see EnemyBasePD's own doc comment),
    // so this opts in precisely where interrupting this enemy actually cancels something committed.
    protected override bool CanBeInterrupted
    {
        get
        {
            var ss = GetShooterState(ref currentState);
            return ss == ShooterState.Windup || ss == ShooterState.DashingAway || ss == ShooterState.Shooting;
        }
    }

    // Fully aborts the attack chain (2026-08-12) — an interrupted attack no longer retries with whatever
    // shots it had left; it zeroes shotsRemaining AND puts the whole sequence on a full fresh cooldown (same
    // formula EnterPostShot's own natural chain-end already uses), so the very next Moving-state check can't
    // immediately refill-and-restart. Moving itself is stunnable (see isStunnable's own per-tick assignment
    // above), so a Stun() paired with the same hit as this Interrupt() (always called BEFORE a paired
    // Stun(), see IEnemyPD.Interrupt()'s own doc comment) freezes this enemy here via EnemyBasePD.Simulate()'s
    // own isStunnable-gated freeze for the stun's whole duration before it's ever able to attack again — no
    // extra handling needed for that here.
    protected override void OnInterrupted()
    {
        currentState.shotsRemaining      = 0;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        EnterMoving(ref currentState);
    }

    // ── Firing ────────────────────────────────────────────────────────────────

    // Aims at the captured attackAnchorPosition (see CaptureAttackAnchor's own doc comment), not live
    // _target — this is the actual functional reason the anchor exists for Shooter specifically: unlike
    // Leaper/Dagger's position-based self-centered AoEs, a shot is a genuinely directional projectile that
    // WOULD visibly redirect toward a different player mid-windup if it read _target live.
    private void FireShots(ref State state)
    {
        if (projectilePrefab == null) return;

        Vector3 origin  = transform.position;
        Vector3 baseDir = state.b.attackAnchorPosition - origin;
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
                projectile.Launch(dmg, dir, projectileSpeed, projectileMaxRange, projectileScale * size, TierAccentMaterial);
        }
    }

    // ── View ──────────────────────────────────────────────────────────────────
    private ShooterState _lastVisualState = (ShooterState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        var ss = (ShooterState)viewState.shooterStateId;

        // Live, every frame regardless of the _lastVisualState-gated block below — see SpearGloomPD's own
        // comment on the identical live-animator-speed pattern.
        if (_animator != null)
        {
            if (ss == ShooterState.Windup)
                _animator.SetFloat(WindupSpeedHash, _windupClipRate * EffectiveAttackSpeed);
            else if (ss == ShooterState.Shooting)
                _animator.SetFloat(ShootSpeedHash, _shootClipRate * EffectiveAttackSpeed);
            else if (ss == ShooterState.PostShot)
                _animator.SetFloat(PostShotSpeedHash, _postShotClipRate * EffectiveAttackSpeed);
        }

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
                // clip — shotsRemaining == shotsPerAttack - 1 means no shot has fired yet this sequence
                // (decrement now happens at the START of each attempt, not after each shot resolves — see
                // the Moving/BetweenShots CaptureAttackAnchor sites — so the first windup's own decrement
                // already dropped a fresh shotsPerAttack by exactly one). Repeat windups between shots in
                // the same multishot burst skip the Play call entirely: the weapon is already at firing
                // level from the previous Shoot's recoil, and forcing WindupHash to restart at frame 0
                // (which begins at the raised idle pose) would teleport it back up mid-burst instead of
                // holding position between shots.
                if (viewState.shotsRemaining == shotsPerAttack - 1)
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
