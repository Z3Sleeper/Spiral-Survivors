using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Enemies/BlitzGloom.cs — a straight-line dash attacker. Old system's
// windup-indicator unparenting hack (SetParent(null) + manually driving world position/rotation every
// frame) isn't needed here — the indicator just stays nested under Visuals like SpearGloomPD's own windup
// indicator, inheriting facing naturally once the body itself rotates. Damage comes entirely from the
// generic ContactDamageCollider + CanContactDamage (matching old's CanContactDamage => Blitzing) — not a
// dedicated hitbox, since Blitz's "attack" is just its own body moving fast into whatever it touches,
// unlike SpearGloomPD's separate stab hitbox.
//
// Live-tracks its committed target for the whole Windup, then LOCKS the instant Blitzing begins (2026-08-21,
// same "follow the player during the telegraph, then commit" feel KingOrbiterPD's own Shoot-phase indicators
// and MortarGloomPD's own hazard already have) — much simpler here than either of those, since there's no
// separately-spawned object with its own independent clock to keep synced: the body itself just keeps
// re-facing _windupTrackedTarget every Windup tick (TrackWindupTarget, called from HandleActiveState's own
// Windup case, which already runs at this enemy's own attack-speed-scaled pace by construction — nothing
// extra needed for that), and the indicator inherits the new facing for free via ordinary parent-child
// transform inheritance, exactly as it always did for the old one-time SnapFaceAnchor. Locking is equally
// free: nothing updates blitzDirection once EnterBlitz's own SetBlitzState flips this out of Windup, so it
// naturally stays exactly wherever TrackWindupTarget last left it — no separate flag/Lock() call needed.
public class BlitzGloomPD : EnemyBasePD<BlitzGloomPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int     stateId;              // cast to/from BlitzState, same convention as SpearGloomPD
        public float   stateTimer;
        public float   attackCooldownTimer;
        public Vector3 blitzDirection;        // locked in at Windup entry, reused through Blitzing
        public int     blitzesRemaining;
        // 0..1 how far through the fixed dash corridor the body has actually traveled, written
        // deterministically every Simulate tick while Blitzing (see HandleActiveState's own doc comment) —
        // drives the windup indicator's own "collapse in behind the body" shrink in UpdateView. Reset to 0 at
        // EnterBlitz (fresh dash, fresh progress); meaningless outside Blitzing.
        public float   blitzTravelProgress;
        // Runtime-scaled — see EnemyBaseState.scaledMaxHealth's own doc comment for why these live in State
        // rather than as a plain field mutated by OnConfigureSpawn (2026-09-22 follow-up pass).
        public int   scaledBlitzesPerAttack;
        public float scaledAttackCooldown;
        public float scaledRotationRateDegPerSecond;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    public enum BlitzState { Moving, Windup, Blitzing, PostBlitz }

    private BlitzState GetBlitzState(ref State s) => (BlitzState)s.stateId;
    private void SetBlitzState(ref State s, BlitzState st) => s.stateId = (int)st;

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    // Only the dash itself deals contact damage — matches old's identical CanContactDamage override.
    public override bool CanContactDamage => (BlitzState)currentState.stateId == BlitzState.Blitzing;

    // Contact damage IS this enemy's whole attack (the Blitz dash itself), never passive — since
    // CanContactDamage above already gates it to exactly the Blitzing state, every contact hit that can even
    // happen is a real attack, so this is unconditionally false whenever contact damage is possible at all.
    protected override bool ContactDamageIsPassive => false;

    [Header("Blitz Settings")]
    [SerializeField] private float windupDuration       = 0.5f;
    [SerializeField] private float postBlitzPause       = 0.5f;
    [SerializeField] private float blitzDistance        = 12f;
    // Scaled by size (2026-10-14 fix — was read raw everywhere, so an Elite/bigger variant's dash fell
    // visibly short of its own windup indicator's footprint, same "model scales up via size, the reach it
    // implies has to scale with it or it silently clips/undershoots" reasoning SpearGloomPD.stabRange and
    // OrbiterGloomPD.EffectiveHoldOrbitRadius already establish). Single source of truth for every call site
    // below (EnterBlitz's actual endpoint, LateSimulate's travel-progress math, UpdateView's indicator) so
    // none of them can drift out of sync with each other.
    private float EffectiveBlitzDistance => blitzDistance * size;
    [SerializeField] private float blitzSpeedMultiplier = 3.5f;
    // Tier1-authored, pristine read-only baseline forever (2026-09-22) — OnConfigureSpawn scales it into
    // State.scaledBlitzesPerAttack instead of mutating this in place, so no _authoredX snapshot/reset is
    // needed anymore (see EnemyBaseState.scaledMaxHealth's own doc comment for the full reasoning).
    [SerializeField] private int   blitzesPerAttack     = 1;

    // Max turn rate while tracking a target during Windup (2026-08-21) — copied from ShielderGloomPD's own
    // rotationRateDegPerSecond/rotationRateDegPerSecondTierGrowth (45 base, 0.2 tier growth). Caps how fast
    // this enemy can re-aim mid-windup so circling it (or timing a dash to arrive late in the windup) is a
    // real counterplay option — an unbounded instant snap (the old behavior) meant no amount of player
    // movement could ever throw off the eventual dash direction. Same pristine-baseline treatment as
    // blitzesPerAttack above — scales into State.scaledRotationRateDegPerSecond instead.
    [SerializeField] private float rotationRateDegPerSecond = 45f;

    [Header("Attack Cooldown")]
    // Same pristine-baseline treatment as blitzesPerAttack above — scales into State.scaledAttackCooldown.
    [SerializeField] private float attackCooldown = 5f;

    [Header("Tier Scaling")]
    // Added onto blitzesPerAttack per tier above 1 — see OnConfigureSpawn().
    [SerializeField] private int blitzesPerAttackTierGrowth = 1;
    // Same linear-per-tier shape as every other *TierGrowth field — see ShielderGloomPD's own identical
    // field for the precedent this was copied from.
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;

    [Header("Indicators")]
    [SerializeField] private GameObject   windupIndicator;
    [SerializeField] private WindupFillPD windupFill;

    // The SPECIFIC player this Windup committed to, captured once at EnterWindup — see class doc comment.
    // Plain field (not State), same "commit once, keep reading that same reference" contract as
    // KingOrbiterPD's own _shotTargetSnapshot — immune to _targetStats getting reassigned to someone else
    // mid-Windup (taunt/invisible-retarget). Only ever read while actually in Windup.
    private IFriendlyPD _windupTrackedTarget;

    // Blitzing unparents windupIndicator to freeze it at the fixed dash corridor for the whole dash (2026-09-14
    // — see UpdateView's own Blitzing-entry comment for why), so these are needed to know where/how to put it
    // back once Blitzing ends — same exact pattern (and the same worldPositionStays:true bug already fixed
    // there) as LeaperGloomPD's own _windupIndicatorParent/_windupIndicatorLocalPos/_windupIndicatorLocalRot —
    // see that class's own doc comments for the full "why" on each.
    private Transform  _windupIndicatorParent;
    private Vector3    _windupIndicatorLocalPos;
    private Quaternion _windupIndicatorLocalRot;
    private bool       _windupIndicatorLocalPoseCaptured;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void LateAwake()
    {
        base.LateAwake();

        // Guarded to a single real capture per instance — see LeaperGloomPD's own identical
        // _windupIndicatorParent/_windupIndicatorLocalPos capture for the full reasoning (a pooled reuse could
        // already be mid-Blitzing, unparented, by the time LateAwake() re-fires).
        if (windupIndicator != null) _windupIndicatorParent ??= windupIndicator.transform.parent;
        if (windupIndicator != null && !_windupIndicatorLocalPoseCaptured)
        {
            _windupIndicatorLocalPos = windupIndicator.transform.localPosition;
            _windupIndicatorLocalRot = windupIndicator.transform.localRotation;
            _windupIndicatorLocalPoseCaptured = true;
        }
        currentState.attackCooldownTimer = attackCooldown;
        // Set again in OnConfigureSpawn() once blitzesPerAttack is actually tier-scaled — this LateAwake()
        // assignment runs BEFORE that scaling lands, so it's only ever a placeholder for the brief window
        // in between.
        currentState.blitzesRemaining    = blitzesPerAttack;
    }

    // Reparents windupIndicator back under _windupIndicatorParent AND restores its exact authored local pose
    // — see LeaperGloomPD.ReparentWindupIndicatorHome's own doc comment for why this restores the pose
    // explicitly instead of trusting SetParent(parent, true)'s own worldPositionStays.
    private void ReparentWindupIndicatorHome()
    {
        if (windupIndicator == null) return;
        windupIndicator.transform.SetParent(_windupIndicatorParent, false);
        windupIndicator.transform.localPosition = _windupIndicatorLocalPos;
        windupIndicator.transform.localRotation = _windupIndicatorLocalRot;
    }

    // Tier1-authored blitzesPerAttack, scaled by blitzesPerAttackTierGrowth per tier above 1 (see that
    // field's own doc comment). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call).
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        currentState.scaledBlitzesPerAttack = blitzesPerAttack + (spawnTier - 1) * blitzesPerAttackTierGrowth;
        currentState.blitzesRemaining = currentState.scaledBlitzesPerAttack;
        currentState.scaledRotationRateDegPerSecond = rotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);

        // Threat Level 5 — halves attackCooldown (attackCooldownMultiplier is 0.5f once difficulty >= 5,
        // else 1f — see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set the placeholder
        // attackCooldownTimer LateAwake() already assigned above, using the now-scaled value, same
        // "set again once the real scaling lands" reasoning as blitzesRemaining above.
        currentState.scaledAttackCooldown = attackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    // Overridden specifically so the travel-progress computation below runs EVERY tick, including while
    // actually Blitzing (2026-09-14 fix — confirmed live: the shrink never advanced past its initial 0 at
    // all). This used to live inside HandleActiveState, but EnemyBasePD.Simulate() only ever calls
    // HandleActiveState while !b.isDashing — see its own dash-dispatch block ("if (!b.isDashing &&
    // !b.isOffsetChasing) HandleActiveState(...)") — meaning it was SKIPPED ENTIRELY for this enemy's own
    // ENTIRE Blitzing duration, the one state that actually needed it. Same "override Simulate(), call
    // base.Simulate() first, then layer extra unconditional per-tick logic on top" pattern
    // KingOrbiterPD.Simulate() already establishes for its own Persistent Pleb Shots.
    protected override void Simulate(ref State state, float delta)
    {
        base.Simulate(ref state, delta);

        // Drives the windup indicator's own "collapse in behind the body as it travels" shrink — see
        // UpdateView's own doc comment for the full visual reasoning. Computed HERE, deterministically, off
        // the REAL tick-accurate transform.position (safe — this is exactly the same "read the real physics
        // position inside Simulate/LateSimulate, never a live/smoothed transform from view code" rule every
        // other jitter fix this session already established — this runs unconditionally every tick, same
        // Simulate-context safety as before, just no longer gated behind HandleActiveState actually running),
        // then stored into predicted State so UpdateView only ever reads the derived RESULT, never re-deriving
        // it from a live transform itself. origin is re-derived from state.b.dashDestination/
        // state.blitzDirection (both fixed for the whole dash) rather than a separate snapshot — same
        // reasoning as UpdateView's own identical derivation.
        if (GetBlitzState(ref state) == BlitzState.Blitzing)
        {
            Vector3 destination = state.b.dashDestination;
            float effectiveBlitzDistance = EffectiveBlitzDistance;
            Vector3 origin = destination - state.blitzDirection * effectiveBlitzDistance;
            float traveled = effectiveBlitzDistance > 0.0001f
                ? Vector3.Dot(transform.position - origin, state.blitzDirection)
                : effectiveBlitzDistance;
            state.blitzTravelProgress = Mathf.Clamp01(traveled / Mathf.Max(effectiveBlitzDistance, 0.0001f));
        }
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded)
        {
            // Decrement moved to EnterWindup (attack START, not this completion point) — see its own
            // comment for why. blitzesRemaining already reflects the blitz that just ended by the time we
            // get here.
            // _target == null guard matters here — without it, a chain that lost its target mid-way would
            // charge again in whatever the PREVIOUS charge's stale direction was (SnapFaceAnchor no-ops
            // on a zero direction, leaving blitzDirection untouched) instead of ending the chain.
            // IsTargetStillValid — a target that went invisible-and-non-taunting since the last charge
            // shouldn't get chained into another one against them; see that property's own doc comment.
            // !IsCrowdControlled — a true Stun() landing mid-sequence never interrupts the dash that just
            // ended above, but stops the chain from continuing into another one, falling through to
            // PostBlitz instead (a Slow does NOT stop the chain — see IsCrowdControlled's own doc comment,
            // hard CC only).
            if (state.blitzesRemaining > 0 && _target != null && IsTargetStillValid && !IsCrowdControlled)
            {
                CaptureAttackAnchor(ref state);
                EnterWindup(ref state, delta);
            }
            else EnterPostBlitz(ref state);
        }

        var ss = GetBlitzState(ref state);

        // attackCooldownTimer: only ticks while actually in Moving (2026-08-19 — uniform across every
        // multi-phase enemy's own attack-cadence cooldown now, see OrbiterGloomPD's own fix). stateTimer:
        // scaled live by EffectiveAttackSpeed every tick — see SpearGloomPD's own comment on the identical
        // pattern. Note the actual Blitz dash itself needs no separate handling here at all: CanContactDamage
        // is gated to the whole Blitzing state, and HandleDash already applies EffectiveAttackSpeed live to
        // the dash's own velocity (see EnemyBasePD.HandleDash), so "attack speed = dash speed" already holds
        // for free.
        if (ss == BlitzState.Moving)
            state.attackCooldownTimer -= delta;
        state.stateTimer -= delta * EffectiveAttackSpeed;

        switch (ss)
        {
            case BlitzState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    // blitzesRemaining itself is handled by EnterWindup now, not here — see its own comment.
                    // Ongoing attack starts here — see CaptureAttackAnchor's own doc comment.
                    CaptureAttackAnchor(ref state);
                    EnterWindup(ref state, delta);
                }
                break;

            case BlitzState.Windup:
                TrackWindupTarget(ref state, delta);
                if (state.stateTimer <= 0f) EnterBlitz(ref state);
                break;

            case BlitzState.PostBlitz:
                if (state.stateTimer <= postBlitzPause * 0.5f) FaceTargetSmooth(delta);
                if (state.stateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ss = GetBlitzState(ref state);

        // Reworked (2026-08-10) — previously blocked stun/knockback for the ENTIRE non-Moving sequence
        // (including PostBlitz) as a deliberate per-enemy choice; now matches every other multi-phase
        // enemy's own convention instead (SpearGloomPD/DaggerGloomPD/ShooterGloomPD/MortarGloomPD): only the
        // actual committed windup+attack blocks it, PostBlitz recovers to stunnable/vulnerable like any
        // other Post/Between beat.
        bool isWindupOrBlitzing = ss == BlitzState.Windup || ss == BlitzState.Blitzing;
        isStunnable = !isWindupOrBlitzing;

        // Windup is a stationary telegraphed-attack window — kinematic (Case 2, immovable but still
        // physically present), matching SpearGloomPD/KingSpikePD's convention. Blitzing is fast movement
        // handled entirely by the base dash primitives (collider-disabled instead, Case 1) — HandleDash
        // already disables/re-enables _physicsCollider around the dash itself, but re-deriving it here
        // every tick (rather than relying solely on that) matches SpearGloomPD's own explicit convention.
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = ss == BlitzState.Windup;
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }

        if (_physicsCollider != null)
            _physicsCollider.enabled = ss != BlitzState.Blitzing;
    }

    // ── State transitions ────────────────────────────────────────────────────

    private void EnterWindup(ref State state, float delta)
    {
        // Decrement lives HERE — the actual START of one blitz attempt — not at the dash's own completion
        // (dashJustEnded, above, where it used to live). A blitz cancelled mid-windup or mid-dash via
        // Interrupt() now still counts against the budget, since blitzesRemaining already dropped the
        // instant this ran. Refill-if-exhausted (not an unconditional reset) — called from both a fresh
        // Moving-triggered attack (blitzesRemaining sitting at 0 from a previous fully-completed sequence,
        // or a pooled spawn) AND the dashJustEnded continuation branch above (blitzesRemaining already > 0,
        // so this is a no-op there). This is also why Moving's own attack-trigger and EnterMoving/
        // OnInterrupted no longer stomp blitzesRemaining themselves — this single self-contained check is
        // the only reset point now, so an interrupted sequence's leftover count survives into whatever
        // attack comes next instead of being thrown away. See KingSpikePD.EnterChargeWindup for the same
        // pattern.
        if (state.blitzesRemaining <= 0) state.blitzesRemaining = state.scaledBlitzesPerAttack;
        state.blitzesRemaining--;

        // Captured here — the commit moment for this strike — not read live off _targetStats later; see
        // class doc comment and _windupTrackedTarget's own field comment.
        _windupTrackedTarget = _targetStats;
        state.stateTimer = windupDuration;
        SetBlitzState(ref state, BlitzState.Windup);
        // Snap to face the target FIRST (2026-08-24 fix), THEN let TrackWindupTarget's own rate-limited
        // tracking take over from subsequent ticks — without this, the body kept whatever rotation the
        // PREVIOUS attack (or Moving's own chase) left it at, and rotationRateDegPerSecond alone often isn't
        // fast enough to fully turn around before the windup ends if the target's now roughly behind it
        // (confirmed live — the exact same bug KingSpikePD.EnterChargeWindup had). Only writes
        // state.blitzDirection on a non-zero return — see EnemyBasePD.SnapFaceTarget(CharacterStatsPD, bool)
        // own doc comment for the guard/degenerate cases that return Vector3.zero.
        Vector3 snapDir = SnapFaceTarget(_windupTrackedTarget);
        if (snapDir != Vector3.zero) state.blitzDirection = snapDir;
    }

    // Re-faces the body toward _windupTrackedTarget's LIVE position every Windup tick, capped at
    // rotationRateDegPerSecond, via Vector3.RotateTowards off state.blitzDirection — the PREDICTED-STATE
    // value, not transform.rotation (2026-08-24 fix, was Quaternion.RotateTowards off transform.rotation —
    // confirmed live bug: chained blitzesPerAttack > 1 attacks never actually re-snapped between blitzes).
    // Root cause: Rigidbody.MoveRotation doesn't update transform.rotation synchronously — Unity applies it
    // at the next physics step. HandleActiveState's dashJustEnded block calls EnterWindup (which snaps via
    // MoveRotation and correctly sets state.blitzDirection) BEFORE the switch statement below re-runs THIS
    // SAME TICK (ss is re-read after the transition) — so this method's OLD transform.rotation read still
    // saw the STALE pre-snap rotation, computed a tiny rate-capped step from the WRONG starting point, and
    // clobbered both the queued MoveRotation and the just-snapped blitzDirection. A freshly-triggered attack
    // (Moving case calling EnterWindup, then `break`ing) never hit this — no same-tick switch re-entry there
    // — which is why only chained blitzes (dashJustEnded path) showed the bug. Using state.blitzDirection as
    // the rotate-FROM basis instead sidesteps the whole physics-step-lag problem: it's written and read
    // synchronously within the same Simulate() tick, immune to how many times this gets called in one tick.
    // Keeps state.blitzDirection updated to match every tick, so whatever EnterBlitz reads the instant Windup
    // ends is always the freshest reachable direction — see class doc comment for why that alone is enough to
    // make this "lock" automatically. No-op (holds last direction) if the target's died/gone
    // invisible-without-taunting mid-Windup, or if the computed direction is degenerate (target exactly on
    // top of this enemy) — same "freeze on invalid, don't stomp with zero" convention the old SnapFaceAnchor
    // call already had. Rate scaled by EffectiveAttackSpeed (2026-08-31) — see
    // KingSpikePD.TrackChargeWindupTarget's own identical 2026-08-31 doc comment for why.
    private void TrackWindupTarget(ref State state, float delta)
    {
        if (_windupTrackedTarget == null || _windupTrackedTarget.IsDead ||
            (_windupTrackedTarget.IsInvisible && !_windupTrackedTarget.IsTaunting)) return;

        Vector3 dir = _windupTrackedTarget.transform.position - transform.position;
        dir.y = 0f;
        if (dir == Vector3.zero) return;
        dir.Normalize();

        Vector3 newDir = Vector3.RotateTowards(state.blitzDirection, dir, state.scaledRotationRateDegPerSecond * Mathf.Deg2Rad * delta * EffectiveAttackSpeed, 0f);
        _rigidbody.MoveRotation(Quaternion.LookRotation(newDir));
        state.blitzDirection = newDir;
    }

    private void EnterBlitz(ref State state)
    {
        Vector3 endPoint = transform.position + state.blitzDirection * EffectiveBlitzDistance;
        SetBlitzState(ref state, BlitzState.Blitzing);
        state.blitzTravelProgress = 0f; // fresh dash, fresh progress — see its own doc comment on State
        // EffectiveMoveSpeed, not baseMoveSpeed (2026-08-13) — so this scales with Boss enrage stacking and
        // stays proportionate to whatever EnemySpawnerPD's own tier scaling did to moveSpeed, same as every
        // other dashing enemy's own dash-speed formula (see EnemyBasePD.StartDashToPoint's own doc comment).
        StartDashToPoint(ref state, EffectiveMoveSpeed * blitzSpeedMultiplier, endPoint);
    }

    private void EnterPostBlitz(ref State state)
    {
        state.stateTimer = postBlitzPause;
        // Flat, not randomized — only the initial spawn-time cooldown gets randomized (to desync multiple
        // simultaneously-spawned Blitzers), not every recurring attack cooldown afterward.
        state.attackCooldownTimer = state.scaledAttackCooldown;
        SetBlitzState(ref state, BlitzState.PostBlitz);
    }

    private void EnterMoving(ref State state)
    {
        // blitzesRemaining deliberately left untouched here — EnterWindup owns resetting it for the natural
        // target-lost/crowd-controlled early-exit path (refill-if-exhausted, not unconditional).
        // OnInterrupted() now zeroes it explicitly itself, BEFORE calling this, for the Interrupt()-driven
        // path — see its own doc comment.
        // Attack routine's over — reprioritize onto whoever's closest now, rather than sitting on whoever
        // this routine happened to start against. See EnemyBasePD.FindClosestTarget's own doc comment.
        FindClosestTarget();
        SetBlitzState(ref state, BlitzState.Moving);
    }

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above, fixed
    // 2026-08-10 to match the rest of the roster) and meant to be cancelable — CanBeInterrupted now defaults
    // to false (see EnemyBasePD's own doc comment). PostBlitz is stunnable/vulnerable, so is correctly
    // excluded here too — no longer "everything except Moving."
    protected override bool CanBeInterrupted
    {
        get
        {
            var ss = GetBlitzState(ref currentState);
            return ss == BlitzState.Windup || ss == BlitzState.Blitzing;
        }
    }

    // Fully aborts the attack chain (2026-08-12, reworked from "leave blitzesRemaining untouched") — an
    // interrupted attack no longer retries with whatever reps it had left; it zeroes the counter AND puts
    // the whole sequence on a full fresh cooldown (flat, not randomized — matches EnterPostBlitz's own
    // explicit "only the initial spawn-time cooldown gets randomized" choice), so the very next Moving-state
    // check can't immediately restart. Moving itself is stunnable (see isStunnable's own per-tick assignment
    // above), so a Stun() paired with the same hit as this Interrupt() (always called BEFORE a paired
    // Stun(), see IEnemyPD.Interrupt()'s own doc comment) freezes this enemy here via EnemyBasePD.Simulate()'s
    // own isStunnable-gated freeze for the stun's whole duration before it's ever able to attack again — no
    // extra handling needed for that here.
    protected override void OnInterrupted()
    {
        currentState.blitzesRemaining    = 0;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        EnterMoving(ref currentState);

        // Predicted-instant hide, not left for UpdateView's own stateId-driven redraw to eventually catch
        // up — Interrupt() mutates currentState out-of-band (from a different identity's own Simulate(),
        // not this enemy's own HandleActiveState flow this tick), which could otherwise leave the windup
        // indicator visibly stuck for a stray tick before UpdateView's viewState catches up to the
        // interrupt. Same fix applied to every other enemy with a windup indicator (Spear/Dagger/Leaper/
        // KingSpike) — see KingSpikePD.OnInterrupted's own comment.
        // _lastVisualState deliberately NOT written here — see SpearGloomPD.OnInterrupted's own comment for
        // why (UpdateView's own edge-detection bookkeeping, not ours to set).
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null) windupFill.Hide();
        // Explicit reparent, not left for UpdateView's own edge-detected transition to eventually catch up —
        // an interrupt during Blitzing leaves windupIndicator unparented (see UpdateView's own transition
        // comment); Blitzing IS interruptible (see CanBeInterrupted below), so this is a real, reachable case,
        // not just defensive. Same fix/reasoning as LeaperGloomPD.OnInterrupted's own identical check.
        if (windupIndicator != null && windupIndicator.transform.parent != _windupIndicatorParent)
            ReparentWindupIndicatorHome();
    }

    // ── View ──────────────────────────────────────────────────────────────────
    private BlitzState _lastVisualState = (BlitzState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        // Same reasoning as SpearGloomPD's guard — this can run while a pooled instance is transiently
        // inactive during replay; bail before recording _lastVisualState so the transition retries once
        // actually active, instead of leaving stale indicator state from a previous pooled life.
        if (!gameObject.activeInHierarchy) return;

        // Death is handled by base.UpdateView() above (OnDeathStop() + hierarchy.Delete()) — but that
        // doesn't stop execution from continuing into the rest of THIS method afterward. Confirmed live
        // (2026-10-14, client-only): if a PredictedHierarchy reconciliation rebuilds this instance AFTER it
        // died mid-Blitzing, the fresh _lastVisualState default no longer matches the current (still
        // Blitzing) stateId, so the transition block below re-runs as if entering Blitzing for the first
        // time — its own windupIndicator.SetActive(isWindupOrBlitzing) call would immediately re-enable the
        // very indicator OnDeathStop() just hid, moments earlier in this same call. Bailing here preserves
        // whatever OnDeathStop() already did. Same fix/reasoning as LeaperGloomPD.UpdateView's own identical
        // guard.
        if (viewState.b.isDead) return;

        var ss = (BlitzState)viewState.stateId;
        bool isWindupOrBlitzing = ss == BlitzState.Windup || ss == BlitzState.Blitzing;

        // Re-applied every frame, not just once at spawn — see SpearGloomPD/KingSpikePD's identical
        // reasoning: if blitzDistance is ever changed at runtime, the indicator's visual footprint should
        // never silently drift out of sync with it. Reads EffectiveBlitzDistance (size-scaled), not
        // blitzDistance directly, same reasoning as every other call site — see that property's own doc
        // comment.
        //
        // Blitzing branch (2026-09-14) — "collapses in behind the body" as it travels: the shown rectangle's
        // near edge advances from the fixed dash origin toward the fixed destination as
        // viewState.blitzTravelProgress climbs 0->1, while the far edge (the destination) never moves, so the
        // length shown is always exactly "how much of the dash is still ahead" — matching the real hitbox
        // exactly (contact damage is live for this whole span, see CanContactDamage). blitzTravelProgress is
        // written deterministically in Simulate (see HandleActiveState's own doc comment) off the REAL
        // tick-accurate transform.position — this method itself never reads a live/smoothed transform for the
        // shape math, only the already-derived viewState result, so this can't reintroduce the read-timing
        // jitter class of bug every other indicator fix this session had to solve. origin/destination are
        // both fixed for the whole dash (see State.blitzDirection's own doc comment, and
        // EnemyBasePD.StartDashToPoint's own doc comment for dashDestination), so reading them off viewState
        // is safe — interpolating a constant against itself is a no-op. windupIndicator is already unparented
        // by the time this runs during Blitzing (see the transition block below), so this world-space
        // SetPositionAndRotation composes correctly every frame, same as Leaper's own frozen-Leaping write.
        //
        // Windup branch — full, unshrunk telegraph length, unchanged from before; windupIndicator is still
        // passively inherited under Visuals at this point (not yet unparented), so position/rotation are
        // deliberately left untouched here — only the shape (ShowRectangle) is driven.
        if (windupIndicator != null && windupFill != null)
        {
            if (ss == BlitzState.Blitzing)
            {
                Vector3 destination = viewState.b.dashDestination;
                Vector3 origin = destination - viewState.blitzDirection * EffectiveBlitzDistance;
                float progress = Mathf.Clamp01(viewState.blitzTravelProgress);
                Vector3 nearEdge = Vector3.Lerp(origin, destination, progress);
                float remaining = EffectiveBlitzDistance * (1f - progress);
                windupIndicator.transform.SetPositionAndRotation(nearEdge, Quaternion.LookRotation(viewState.blitzDirection));
                windupFill.ShowRectangle(remaining, centerForwardOffset: remaining * 0.5f);
            }
            else
            {
                windupFill.ShowRectangle(EffectiveBlitzDistance, centerForwardOffset: EffectiveBlitzDistance * 0.5f);
            }
        }

        // Live, every frame regardless of the _lastVisualState-gated block below — see SpearGloomPD's own
        // comment on the identical live-fill pattern. Gate widened to cover Blitzing too (2026-09-14, was
        // Windup-only) — per explicit design, the fill must read as CLEANLY, exactly fully filled (relative
        // to whatever the CURRENT, possibly-already-shrunk length is) for the entire Blitzing dash, not just
        // whatever near-1 (but not exactly 1) value Windup's own last tick happened to leave it at. This
        // formula already gives exactly that for free during Blitzing: stateTimer keeps counting down past 0
        // into negative territory for the whole dash (nothing resets it at the Windup->Blitzing handoff), so
        // Clamp01 of a negative ratio floors to 0, and 1-0 = 1 — no separate branch needed, unlike
        // KingOrbiterPD's own Shoot/Pull/Persistent fills, which DO need an explicit post-fire branch since
        // their own underlying timers either don't go negative or (Persistent) get reset entirely on fire —
        // see UpdatePersistentIndicatorFills's own doc comment for that one.
        if (windupFill != null && isWindupOrBlitzing)
            windupFill.SetProgress(1f - Mathf.Clamp01(viewState.stateTimer / windupDuration));

        if (ss == _lastVisualState) return;
        BlitzState previousState = _lastVisualState;
        _lastVisualState = ss;

        // Per explicit design (2026-09-14) — the indicator must stay visible for the Blitz's ENTIRE
        // movement, not just its Windup telegraph, matching the same "hold until the referenced enemy has
        // actually finished its own movement" convention just established for King Orbiter/Orbiter's own
        // pull/shot indicators.
        if (windupIndicator != null)
        {
            if (ss == BlitzState.Blitzing && previousState != BlitzState.Blitzing)
            {
                // Entering Blitzing — unparent once; the live-every-frame block above (which runs BEFORE this
                // gated block, same tick) already wrote this frame's correct world position/rotation/shape
                // using progress=0 (blitzTravelProgress was just reset to 0 in EnterBlitz), so there's nothing
                // left to do here except detach from Visuals' continuously-interpolating hierarchy — same
                // "unparent so a still-parented smoothed object can't drag this along with it" reasoning
                // Leaper's own Leaping-entry transition uses.
                windupIndicator.transform.SetParent(null, true);
            }
            else if (previousState == BlitzState.Blitzing && ss != BlitzState.Blitzing)
            {
                ReparentWindupIndicatorHome();
            }
            windupIndicator.SetActive(isWindupOrBlitzing);
        }
        if (windupFill != null && !isWindupOrBlitzing) windupFill.Hide();
    }

    // Restores the windup indicator on a misprediction-revert — same class of bug as LeaperGloomPD's own
    // identical fix (see its OnHiddenStateChanged doc comment for the full mechanism): OnDeathStop() below
    // is a one-way latch that hides+unparents the indicator the instant isDead is predicted true, and
    // nothing else re-shows it on a revert since stateId never actually left Windup/Blitzing (only the
    // separate isDead flag mispredicted), so UpdateView's _lastVisualState edge-detect never re-fires. No
    // explicit position/rotation write needed here, even for Blitzing — this fires from WITHIN
    // base.UpdateView(), called before this override's own "live every frame" block further down runs in
    // this SAME UpdateView() call, which already re-derives the correct world position every frame it's
    // Blitzing; unparenting here is enough for that block to then write it correctly moments later, same
    // frame.
    protected override void OnHiddenStateChanged(bool hidden)
    {
        base.OnHiddenStateChanged(hidden);
        if (hidden || windupIndicator == null) return;

        var ss = (BlitzState)currentState.stateId;
        if (ss != BlitzState.Windup && ss != BlitzState.Blitzing) return;

        if (ss == BlitzState.Blitzing)
            windupIndicator.transform.SetParent(null, true);
        windupIndicator.SetActive(true);
    }

    protected override void OnDeathStop()
    {
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null)      windupFill.Hide();
        // Explicit reparent, fixed immediately rather than left for OnRespawnReset's own defensive check to
        // catch on the NEXT pooled reuse — same "leftover indicator stuck mid-air" class of bug
        // OnInterrupted's own identical fix addresses. Dying mid-Blitzing leaves windupIndicator unparented
        // (see UpdateView's own transition comment).
        if (windupIndicator != null && windupIndicator.transform.parent != _windupIndicatorParent)
            ReparentWindupIndicatorHome();
    }

    protected override void OnRespawnReset()
    {
        currentState.stateId             = 0;
        currentState.stateTimer          = 0f;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        currentState.blitzesRemaining    = currentState.scaledBlitzesPerAttack;
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null)      windupFill.Hide();
        // In case death happened mid-Blitzing, while the indicator was unparented — a fresh reuse should
        // never inherit a previous life's detached indicator (defensive backstop — OnDeathStop's own
        // identical check should already have caught this, but this covers any path that skipped it).
        if (windupIndicator != null && windupIndicator.transform.parent != _windupIndicatorParent)
            ReparentWindupIndicatorHome();
        // In case death happened mid-Windup, while kinematic was on — HandleActiveState would correct this
        // on the next tick anyway (stateId resets to Moving above), but resetting explicitly here avoids
        // even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (BlitzState)(-1);
        _windupTrackedTarget = null;
    }
}
