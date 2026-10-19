using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Enemies/ShooterGloom.cs. Waits at preferred range, fires on cooldown. The
// DashingAway/Windup/Shooting sequence for one shot (or one multishot burst) is an "ongoing attack" (see
// EnemyBasePD.CaptureAttackAnchor) — it always resolves against whichever target it committed to when it
// started, even if _target gets reassigned mid-sequence (taunt/invisible). Multishot uses the shared
// MultishotMath formula (WeaponBasePD.cs) — same behavior as the player's own WeaponRangedPD.
//
// Rapidfire burst-lock (2026-09-12): only the FIRST shot in a chain commits/tracks a target at all (via
// CommitToCurrentTarget in EnterWindup, then live-tracked/re-snapped through Windup/FirePause/EnterShooting
// as described below). The instant that first shot actually fires, State.burstLocked flips true (in
// EnterShooting) and every later shot in the SAME chain reuses that exact locked aim untouched — no more
// re-tracking or re-facing between shots (see State.burstLocked's own doc comment, and the FirePause/Shooting
// cases' own comments). This is what gives multi-shot attacks their "keep facing and shooting in the same
// direction until all shots are consumed" burst feel, while staying interruptible the entire time (CC rules
// unchanged — see below). burstLocked resets false at the start of every fresh chain (EnterWindup).
//
// Only the FIRST attack in a chain can dash away (2026-08-27) — every later shot in the same multishot
// burst skips Windup entirely and goes straight from Shooting into a brief FirePause (see class doc comment
// below) before firing again, with no range/dash check of its own. BetweenShots (a separate, longer 0.25s
// state with its own FaceTargetSmooth) is gone — FirePause covers both roles now (the beat after a dash,
// AND the beat between shots in a burst), under one committed-attack CC ruleset (CanBeInterrupted, not
// isStunnable — an explicit choice: simpler than the old two-different-CC-rules-depending-on-state shape).
//
// Windup-first reorder (2026-08-26) — the "too close, dash away" check used to run BEFORE Windup even
// started, meaning a Shooter caught up close would evade first and only become vulnerable/interruptible
// once already safely at range. Now Windup always runs first (stationary, interruptible — see
// CanBeInterrupted) regardless of distance, and the range check only happens right at the END of Windup:
// too close means dash away THEN fire immediately (no second windup), otherwise fire immediately in place.
// Both branches funnel through the same EnterShooting(). This is deliberate — it's what makes this enemy
// reliably interruptible up close for the first time (explicit design goal), while keeping the dash-away
// evasion for whenever the player closes distance during the windup itself.
//
// Live-tracks a committed target during Windup instead of a frozen anchor position (2026-08-26) — matches
// BlitzGloomPD/KingSpikePD's own established _windupTrackedTarget pattern (captured once, at the exact
// moment Windup begins, from _targetStats — "the commit moment," never re-read live off _targetStats
// afterward, so a taunt landing mid-Windup can't redirect an already-started attack onto the new taunting
// player; confirmed this is the consistent rule across every other enemy with windup-tracking before this
// one got it). Unlike Blitz/KingSpike, tracking here is NOT rate-capped — explicit design choice, this
// enemy just has plain instant tracking, both for the cosmetic Windup-facing and for the real aim direction
// (re-snapped fresh in EnterShooting, right before firing, in both the dash and no-dash branches) — a much
// longer windupDuration (the whole point of this change) would otherwise leave the shot aimed at a long-
// stale position if it kept using the old anchor-snapshotted-at-Windup-START value.
public class ShooterGloomPD : EnemyBasePD<ShooterGloomPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   shooterStateId;
        public float shooterStateTimer;
        public float attackCooldownTimer;
        public int   shotsRemaining;
        // True once the FIRST shot of this attack chain has actually fired (2026-09-12 rapidfire rework —
        // see class doc comment) — from that point on, every later shot in the same chain reuses the exact
        // same locked facing/aim direction instead of re-tracking the target, giving the "keep facing and
        // shooting in the same direction until all shots are consumed" burst feel. Reset false at the start
        // of every fresh chain (EnterWindup).
        public bool burstLocked;
        // Runtime-scaled — see EnemyBaseState.scaledMaxHealth's own doc comment for why these live in State
        // rather than as a plain field mutated by OnConfigureSpawn (2026-09-22 follow-up pass).
        public int   scaledMultishotCount;
        public float scaledAttackCooldown;
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
    // The "too close" trigger threshold — separate from preferredRange (which governs the normal approach
    // hold distance via HandleMovement, standing still once close rather than backing away — see
    // EnemyBasePD.HandleMovement's own doc comment) since a player closing distance while too close needs an
    // actual evasive dash, not just movement hysteresis.
    [SerializeField] private float minAttackRange     = 8f;
    [SerializeField] private float windupDuration     = 0.25f;
    [SerializeField] private float shootDuration      = 0.1f;
    [SerializeField] private float postShotPause      = 0.5f;
    [SerializeField] private int   shotsPerAttack     = 1;
    [SerializeField] private float shotDamageMultiplier = 1f;
    // Multiplier against baseMoveSpeed, matching every other dashing enemy's own dashSpeedMultiplier
    // convention (see StartDashToPoint/StartOffsetChase callers) — NOT old's 33x, which was tuned against
    // old's differently-scaled move speed values.
    [SerializeField] private float dashSpeedMultiplier = 4f;
    // Extra clearance beyond exactly reaching minAttackRange, same buffer old's own EnterDash() used.
    [SerializeField] private float dashDistanceBuffer  = 0.5f;
    // Shared fire-pause duration (2026-08-26, broadened 2026-08-27) — TWO roles now: (1) the beat between a
    // dash-away landing and the actual fire (without this, the two resolved on the exact same tick,
    // dashJustEnded routing straight into EnterShooting, which read as the dash and the shot going off
    // simultaneously), and (2) the beat between shots in a multishot burst (replaces the old, separate,
    // longer betweenShotPause — every shot past the first now skips Windup entirely and uses this same
    // pause instead, see class doc comment). See FirePause's own case in HandleActiveState.
    [SerializeField] private float firePauseDuration   = 0.1f;

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
    // split left-first between the two sides. Tier1-authored, pristine read-only baseline forever
    // (2026-09-22) — OnConfigureSpawn scales it into State.scaledMultishotCount instead of mutating this in
    // place, so no _authoredX snapshot/reset is needed anymore (see EnemyBaseState.scaledMaxHealth's own
    // doc comment for the full reasoning).
    [SerializeField] private int   multishotCount       = 0;
    [SerializeField] private float multishotSpreadAngle = 90f;

    [Header("Attack Cooldown")]
    // Same pristine-baseline treatment as multishotCount above — scales into State.scaledAttackCooldown.
    [SerializeField] private float attackCooldown = 5f;

    [Header("Tier Scaling")]
    // Added onto multishotCount per tier above 1 — see OnConfigureSpawn().
    [SerializeField] private int multishotCountTierGrowth = 2;

    public enum ShooterState { Moving, DashingAway, Windup, FirePause, Shooting, PostShot }

    private ShooterState GetShooterState(ref State s) => (ShooterState)s.shooterStateId;
    private void SetShooterState(ref State s, ShooterState st) => s.shooterStateId = (int)st;

    // Scratch buffer for MultishotMath.ComputeAngles — static, immediately drained within one synchronous
    // call, same convention as WeaponRangedPD's own _multishotAngles.
    private static readonly List<float> _multishotAngles = new();

    // The target this attack committed to, captured once at EnterWindup — see class doc comment for why
    // (matches BlitzGloomPD._windupTrackedTarget/KingSpikePD._chargeWindupTrackedTarget exactly). Drives
    // both Windup's own cosmetic facing and the real fire-aim direction, both via plain instant SnapFaceTarget
    // calls (no rate cap, per explicit design — unlike Blitz/KingSpike's own rate-capped equivalents).
    private IFriendlyPD _windupTrackedTarget;
    // Mirrors KingOrbiterPD's own _shotTargetLastKnownPosition — only refreshes while _windupTrackedTarget is
    // actually alive+visible, held frozen otherwise (dead/invisible-without-taunting), and resumes live
    // tracking the instant validity returns. SnapFaceTarget(CharacterStatsPD)'s own built-in no-op-on-invalid
    // already gives the FACING calls this same freeze-for-free (tracking here is instant/uncapped, so the
    // last valid tick already snapped exactly onto this same position) — but the range check (Windup case)
    // and fire-aim (FireShots) read a position directly, with no such built-in guard, so they need this
    // explicit fallback to avoid reading a dead/invisible target's transform live (which could have moved,
    // respawned, or teleported since the moment it actually went invalid).
    private Vector3 _windupTrackedTargetLastKnownPosition;

    protected override void LateAwake()
    {
        base.LateAwake();
        // Placeholder only — attackCooldown is the pristine tier1 baseline until OnConfigureSpawn() (always
        // runs AFTER LateAwake(), once hierarchy.Create() returns) writes the real scaled value into
        // State.scaledAttackCooldown.
        currentState.attackCooldownTimer = attackCooldown;
        currentState.shotsRemaining      = shotsPerAttack;
    }

    // Tier1-authored multishotCount, scaled by multishotCountTierGrowth per tier above 1 (see that field's
    // own doc comment). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call). Read live at fire time (FireShots) off State.scaledMultishotCount.
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        currentState.scaledMultishotCount = multishotCount + (spawnTier - 1) * multishotCountTierGrowth;

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        currentState.scaledAttackCooldown = attackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
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
        // Dash-away now happens AFTER Windup, immediately before firing (see class doc comment) — so
        // finishing the dash goes into a brief FirePause, not back into a second Windup (and not straight
        // into Shooting either — see firePauseDuration's own doc comment for why that pause exists).
        if (dashJustEnded) EnterFirePause(ref state);

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
                    // Decrement lives here (the actual start of the attempt, now always Windup — see
                    // class doc comment) so an attempt cancelled via Interrupt() mid-windup/dash still
                    // counts against the budget. See EnterMoving's own comment for why the reset moved here
                    // too.
                    // Unconditional reset to shotsPerAttack (2026-09-14, was "refill only if <= 0") — this
                    // IS the start of a fresh attack sequence, so shotsRemaining should always begin a chain
                    // at the authored max, full stop. Belt-and-suspenders with the Shooting case's own fix
                    // (2026-09-14) that stops a chain from ever ending early just because the target went
                    // invalid mid-sequence — before that fix, a chain truncated that way could leave
                    // shotsRemaining sitting at some nonzero-but-not-full leftover value, and this old
                    // refill-only-if-<=0 logic would silently UNDER-fire the NEXT chain by however many shots
                    // got skipped (confirmed live: a Rogue vanishing right after shot 1 of a 3-shot attack
                    // left shotsRemaining at 2, so the next attack only fired 2 shots instead of 3). An
                    // unconditional reset here removes any possibility of that regardless of what ended the
                    // previous chain.
                    state.shotsRemaining = shotsPerAttack;
                    state.shotsRemaining--;
                    // Ongoing attack starts here — see CaptureAttackAnchor's own doc comment.
                    CaptureAttackAnchor(ref state);
                    // Always Windup first now, regardless of range — the "too close" dash-away check moved
                    // to the END of Windup (see the Windup case below and class doc comment).
                    EnterWindup(ref state);
                }
                break;

            case ShooterState.DashingAway:
                // Movement itself is driven by the base HandleDash() call in Simulate(); nothing to do here
                // beyond wait for dashJustEnded (handled above) to advance into FirePause.
                break;

            case ShooterState.FirePause:
                // Same live-tracking/refresh as Windup's own case below, just for this much shorter beat —
                // keeps the body visibly re-aiming during the pause instead of sitting frozen at whatever
                // angle the previous state ended on, and keeps _windupTrackedTargetLastKnownPosition
                // maximally fresh right up until EnterShooting's own final refresh+fire. Reached either from
                // a dash landing (before ANY shot in this chain has fired — burstLocked still false, see
                // State.burstLocked's own doc comment) OR from Shooting's own chain-continuation branch below
                // (every shot past the first, burstLocked already true by then) — no minAttackRange/dash
                // check here either way; only the FIRST attack in a chain can ever dash (see class doc
                // comment). Gated on !burstLocked (2026-09-12 rapidfire rework) — once the first shot has
                // actually fired, every later shot in the burst deliberately stops re-tracking/re-facing at
                // all, keeping the exact same locked aim for the rest of the chain instead of visibly
                // re-aiming shot to shot as the target moves.
                if (!state.burstLocked)
                {
                    SnapFaceTarget(_windupTrackedTarget);
                    RefreshTrackedTargetLastKnownPosition();
                }
                if (state.shooterStateTimer <= 0f) EnterShooting(ref state);
                break;

            case ShooterState.Windup:
                // Instant (no rate cap, per explicit design — unlike Blitz/KingSpike's own rate-capped
                // windup tracking) live facing toward the committed target for the whole Windup — purely
                // cosmetic here (EnterShooting re-snaps for the real aim direction regardless), but keeps
                // the body visibly locked onto whoever this attack is against the entire time it's
                // vulnerable/interruptible, rather than sitting frozen at whatever angle Windup entry left
                // it at. No-ops safely via SnapFaceTarget's own null/dead/invisible guard if the committed
                // target became invalid mid-Windup. Only ever reached for the FIRST shot in a chain now
                // (2026-08-27) — every later shot skips straight from Shooting into FirePause instead (see
                // class doc comment), so the dash-check below can never fire for anything but shot #1.
                SnapFaceTarget(_windupTrackedTarget);
                // Must run even on the same tick Windup ends, before the range check below reads it.
                RefreshTrackedTargetLastKnownPosition();
                if (state.shooterStateTimer <= 0f)
                {
                    // Range check moved here from Moving (2026-08-26) — see class doc comment. Reads
                    // _windupTrackedTargetLastKnownPosition (the committed target's last VALID position),
                    // not a live transform read, so this stays frozen if the target died/vanished mid-Windup
                    // instead of measuring against a dead body that may have since moved.
                    float dist = _windupTrackedTarget != null
                        ? Vector3.Distance(transform.position, _windupTrackedTargetLastKnownPosition)
                        : float.MaxValue;
                    if (dist < minAttackRange) EnterDashAway(ref state, dist);
                    else                        EnterShooting(ref state);
                }
                break;

            case ShooterState.Shooting:
                if (state.shooterStateTimer <= 0f)
                {
                    // Always fires every remaining shot in the chain now (2026-09-14, IsTargetStillValid
                    // dropped from this condition) — explicit design change: previously a target going
                    // invisible-and-non-taunting (or dying) mid-sequence ended the chain early, leaving
                    // shotsRemaining stranded at a nonzero-but-not-full value (see the Moving case's own
                    // 2026-09-14 fix for the follow-on under-firing bug that caused on the NEXT attack). Now
                    // the chain always burns through shotsPerAttack shots regardless of target validity —
                    // FireShots only ever reads _windupTrackedTargetLastKnownPosition (frozen the instant the
                    // committed target goes invalid, see that field's own doc comment), so losing the target
                    // mid-sequence simply means every remaining shot in this burst fires at their last known
                    // position instead of the chain aborting. Only !IsCrowdControlled still stops it early —
                    // a true hard CC landing mid-sequence never interrupts the shot that just finished above,
                    // but stops the chain from continuing into another one, falling through to PostShot
                    // instead (a Slow does NOT stop the chain — see IsCrowdControlled's own doc comment, hard
                    // CC only). Interrupt() (the other thing that can end a chain, per class design) already
                    // zeroes shotsRemaining itself in OnInterrupted, independent of this check.
                    if (state.shotsRemaining > 0 && !IsCrowdControlled)
                    {
                        // Continuing the chain — skips Windup entirely (2026-08-27, explicit design: only
                        // the FIRST attack can dash, every later one commits straight to firing after a
                        // brief FirePause instead — see class doc comment). Decrement here, the actual start
                        // of THIS shot's own mini-commitment, so an attempt cancelled mid-FirePause via
                        // Interrupt() still counts against the budget — same reasoning the first shot's own
                        // Moving-case trigger already uses. No refill-if-exhausted guard needed here (unlike
                        // Moving/the old BetweenShots) — shotsRemaining > 0 is already this branch's own
                        // entry condition, so it can never be <= 0 at this point.
                        state.shotsRemaining--;
                        CaptureAttackAnchor(ref state);
                        // Deliberately NOT re-committing to the current target here anymore (2026-09-12
                        // rapidfire rework, was CommitToCurrentTarget() — "each shot in a burst re-evaluates
                        // target fresh"). This shot reuses the exact same _windupTrackedTarget/
                        // _windupTrackedTargetLastKnownPosition the FIRST shot locked in (see
                        // State.burstLocked's own doc comment) — explicit design: a rapidfire burst keeps
                        // facing and firing in the same direction for its whole duration rather than visibly
                        // re-aiming shot to shot. IsTargetStillValid above still independently decides
                        // whether the chain continues AT ALL (reads the live _targetStats, not the locked
                        // _windupTrackedTarget) — untouched by this, a genuinely dead/vanished target still
                        // ends the chain via EnterPostShot below, same as before.
                        EnterFirePause(ref state);
                    }
                    else
                    {
                        EnterPostShot(ref state);
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
        // already disables/re-enables _physicsCollider around the dash itself. FirePause is the same
        // stationary-telegraph shape as Windup — it's still part of the same committed attack, whether it's
        // the beat after landing a dash or the beat between shots in a burst (see class doc comment).
        isStunnable = ss == ShooterState.Moving || ss == ShooterState.PostShot;
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = ss == ShooterState.Windup || ss == ShooterState.FirePause || ss == ShooterState.Shooting;
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

        // _windupTrackedTargetLastKnownPosition, not a live transform read — see that field's own doc
        // comment (stays frozen if the committed target died/vanished mid-Windup).
        Vector3 targetPos = _windupTrackedTarget != null ? _windupTrackedTargetLastKnownPosition : transform.position;
        Vector3 awayDir = transform.position - targetPos;
        awayDir.y = 0f;
        awayDir.Normalize();
        float dashDistance = (minAttackRange - currentDistance) + dashDistanceBuffer;
        Vector3 destination = transform.position + awayDir * dashDistance;

        // EffectiveMoveSpeed, not baseMoveSpeed (2026-08-13) — see BlitzGloomPD.EnterBlitz's own comment.
        StartDashToPoint(ref state, EffectiveMoveSpeed * dashSpeedMultiplier, destination);
    }

    // Reached from a dash landing (dashJustEnded), or from Shooting's own chain-continuation branch (every
    // shot past the first) — both already commit/re-commit via CommitToCurrentTarget or EnterWindup before
    // getting here, so this just snaps facing once immediately (matches EnterWindup's own immediate-snap
    // convention, rather than waiting one tick for FirePause's own per-tick SnapFaceTarget call to catch up).
    private void EnterFirePause(ref State state)
    {
        state.shooterStateTimer = firePauseDuration;
        SnapFaceTarget(_windupTrackedTarget);
        SetShooterState(ref state, ShooterState.FirePause);
    }

    // Captures _targetStats as this attack's committed target and seeds _windupTrackedTargetLastKnownPosition
    // to match, in one step — called once per chain now, from EnterWindup only (2026-09-12 rapidfire rework
    // — Shooting's own chain-continuation branch used to call this too, for every later shot; it no longer
    // does, see State.burstLocked's own doc comment for why). See _windupTrackedTarget's own doc comment for
    // why this needs to be captured once, not read live off _targetStats later.
    private void CommitToCurrentTarget()
    {
        _windupTrackedTarget = _targetStats;
        // Seeded immediately, not left at whatever the last attack's value was — see
        // _windupTrackedTargetLastKnownPosition's own doc comment.
        _windupTrackedTargetLastKnownPosition = _windupTrackedTarget != null ? _windupTrackedTarget.transform.position : transform.position;
    }

    private void EnterWindup(ref State state)
    {
        CommitToCurrentTarget();
        state.shooterStateTimer = windupDuration;
        SnapFaceTarget(_windupTrackedTarget);
        SetShooterState(ref state, ShooterState.Windup);
        // Fresh chain — see State.burstLocked's own doc comment. Live tracking resumes for this chain's own
        // first shot until EnterShooting locks it back in.
        state.burstLocked = false;
    }

    private void EnterShooting(ref State state)
    {
        state.shooterStateTimer = shootDuration;
        // Decrement moved to the attack-start sites (Moving/Shooting's own chain-continuation branch,
        // above) — see their own comment.
        SetShooterState(ref state, ShooterState.Shooting);
        if (!state.burstLocked)
        {
            // One more refresh right before firing (2026-08-26) — covers the DashingAway window: unlike
            // Windup, DashingAway has no tracking phase of its own, so if the committed target flickered
            // invalid->valid (or valid->invalid) WHILE dashing, this is what actually catches it before the
            // shot resolves, rather than firing at whatever position was last recorded back at the end of
            // Windup. Both the dash-away-then-fire and fire-directly branches funnel through here, so both
            // get this same freshest-possible check regardless of whether a dash happened at all. Only ever
            // runs for the FIRST shot of a chain (2026-09-12 rapidfire rework) — locks in right after, so
            // every later shot in the same burst keeps reusing this exact aim instead of re-tracking.
            RefreshTrackedTargetLastKnownPosition();
            SnapFaceTarget(_windupTrackedTarget);
            state.burstLocked = true;
        }
        FireShots(ref state);
    }

    // Refreshes _windupTrackedTargetLastKnownPosition only while _windupTrackedTarget is actually valid
    // right now — called from both the Windup case (every tick) and EnterShooting (one more time, right
    // before firing — see its own comment for why that extra call matters). See
    // _windupTrackedTargetLastKnownPosition's own doc comment for the freeze/resume behavior this gives.
    private void RefreshTrackedTargetLastKnownPosition()
    {
        if (_windupTrackedTarget != null && !_windupTrackedTarget.IsDead &&
            (!_windupTrackedTarget.IsInvisible || _windupTrackedTarget.IsTaunting))
            _windupTrackedTargetLastKnownPosition = _windupTrackedTarget.transform.position;
    }

    private void EnterPostShot(ref State state)
    {
        state.shooterStateTimer   = postShotPause;
        state.attackCooldownTimer = state.scaledAttackCooldown;
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
            return ss == ShooterState.Windup || ss == ShooterState.DashingAway ||
                   ss == ShooterState.FirePause || ss == ShooterState.Shooting;
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
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        EnterMoving(ref currentState);
    }

    // ── Firing ────────────────────────────────────────────────────────────────

    // Aims at _windupTrackedTarget's live position (2026-08-26, was the anchor captured at Windup-START —
    // see class doc comment for why that went stale once Windup got much longer), not live _target — this
    // is the actual functional reason _windupTrackedTarget exists for Shooter specifically: unlike
    // Leaper/Dagger's position-based self-centered AoEs, a shot is a genuinely directional projectile that
    // WOULD visibly redirect toward a different player mid-windup if it read _target live. Computed directly
    // from position math (not transform.forward) so it's correct the instant EnterShooting's own preceding
    // SnapFaceTarget call runs — Rigidbody.MoveRotation doesn't apply until the next physics step (see
    // BlitzGloomPD.TrackWindupTarget's own doc comment for the confirmed bug that reading transform.rotation
    // synchronously after a MoveRotation call would reproduce).
    private void FireShots(ref State state)
    {
        if (projectilePrefab == null || _windupTrackedTarget == null) return;

        Vector3 origin  = transform.position;
        Vector3 baseDir = _windupTrackedTargetLastKnownPosition - origin;
        baseDir.y = 0f;
        baseDir.Normalize();
        if (baseDir == Vector3.zero) return;

        float dmg = B.scaledDamage * shotDamageMultiplier;
        MultishotMath.ComputeAngles(state.scaledMultishotCount, multishotSpreadAngle, _multishotAngles);

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
                // Unconditional now (2026-08-27) — Windup is only ever reached for the FIRST shot in a
                // chain (every later shot skips it entirely, going straight from Shooting into FirePause —
                // see class doc comment), so there's no more "repeat windup mid-burst" case to guard
                // against; the raise-into-firing-position clip always plays exactly once per chain.
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
        currentState.burstLocked         = false;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        // In case death happened mid-Windup/Shooting, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (shooterStateId resets to Moving above), but resetting
        // explicitly here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (ShooterState)(-1);
        _windupTrackedTarget = null;
        _windupTrackedTargetLastKnownPosition = default;
    }
}
