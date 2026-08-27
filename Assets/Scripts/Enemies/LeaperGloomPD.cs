using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Enemies/LeaperGloom.cs — leaps to the target's own live-tracked position and
// deals AoE damage on landing. Old system's aoeHitbox (a trigger Collider, briefly enabled on landing) is
// replaced with a one-shot distance check against CharacterStatsPD.All, matching KingSpikePD's Spike Burst /
// DaggerGloomPD's Spin — cheaper than physics here since it's checking against a small, bounded, already-in-
// memory set (players). The leap itself is a kinematic Vector3.Lerp (with a purely cosmetic sine-arc model
// bob), not the shared dash primitives — old system disables the hurtbox entirely mid-air (can't be hit
// while airborne) but deliberately leaves contact damage untouched, matched here via no CanContactDamage
// override.
//
// Windup live-tracking (2026-08-26) — the leap target is no longer a fixed-radius random-angle offset picked
// once at Windup entry (that offset existed purely so a large, easily-dodgeable landing radius could still be
// avoided without a dash; now that the target is being live-tracked instead, an offset from their ACTUAL
// landing spot would just make the leap less accurate for no reason, so it's gone — leapTarget converges
// straight onto wherever the target actually is). Same two rate-capped-polar-tracking-relative-to-a-fixed-
// origin shape as MortarHazardPD's own "attached" phase (rotationRateDegPerSecond for bearing,
// distanceTrackingRateUnitsPerSecond for distance, both plain-delta not attack-speed-scaled — see
// HandleActiveState's Windup case for why), just inlined directly into this enemy's own State/HandleActiveState
// instead of a separate spawned hazard object, since there's no independent-of-the-firer lifetime need here
// (a leap's target is meaningless once this Leaper itself is gone). Tracking freezes automatically the instant
// Windup hands off to Leaping — HandleActiveState only ever writes state.leapTarget from the Windup case.
// Tracks _windupTrackedTarget (captured once at EnterWindup, see its own doc comment), not live
// _target/_targetStats (2026-08-26 fix) — matches BlitzGloomPD/KingSpikePD/ShooterGloomPD's own identical
// "commit to whoever this attack started against, immune to a taunt reassigning the target mid-Windup"
// convention, confirmed as the consistent rule across every enemy with windup-tracking in this codebase.
public class LeaperGloomPD : EnemyBasePD<LeaperGloomPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int     stateId;              // cast to/from LeapState, same convention as SpearGloomPD
        public float   stateTimer;
        // Counts up from 0 at EnterWindup() through the end of Leaping — unlike stateTimer (which counts
        // DOWN and gets reset to a new baseline at the Windup->Leaping boundary, so it means "time left in
        // THIS sub-phase" rather than one consistent thing), this represents one single quantity ("time
        // since this windup began") for the whole combined span. See UpdateView's fill computation for why
        // that distinction matters for interpolated rendering specifically.
        public float   attackElapsed;
        public float   attackCooldownTimer;
        public Vector3 leapStart;
        public Vector3 leapTarget;            // live-tracked through Windup, frozen the instant Leaping begins
        public int     leapsRemaining;
        // Current tracked bearing/distance from leapStart during Windup — same CompressedFloat/rate-cap shape
        // as MortarHazardPD.State's identical fields, needed in predicted State (not a plain field) for the
        // same reason: MoveTowardsAngle/MoveTowards read their own previous value every tick, which a
        // rollback replay must restore correctly. Meaningless outside Windup (leapTarget itself is what
        // matters once Leaping locks it).
        public CompressedFloat facingAngleDeg;
        public CompressedFloat trackedDistance;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    public enum LeapState { Moving, Windup, Leaping, Landing, PostLeap }

    private LeapState GetLeapState(ref State s) => (LeapState)s.stateId;
    private void SetLeapState(ref State s, LeapState st) => s.stateId = (int)st;

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    private static readonly int IdleHash        = Animator.StringToHash("Idle");
    private static readonly int WindupHash      = Animator.StringToHash("LeaperWindup");
    private static readonly int LeapHash        = Animator.StringToHash("LeaperLeap");
    private static readonly int WindupSpeedHash = Animator.StringToHash("WindupSpeed");
    private static readonly int LeapSpeedHash   = Animator.StringToHash("LeapSpeed");

    // Baseline "clip length / baseline duration" ratios, cached instead of SetFloat'd directly — both need
    // to be re-derived live every frame (ratio * EffectiveAttackSpeed, see UpdateView) instead of set once
    // at spawn, same reasoning as every other attack-speed-scaled enemy in this pass. The leap's own actual
    // traversal speed needs no separate handling here at all — it's driven directly by
    // state.stateTimer / leapDuration (see HandleActiveState's Leaping case), and stateTimer's decrement
    // rate is already scaled by EffectiveAttackSpeed below, so the arc lerp itself speeds up/slows down for
    // free, same effect as scaling dash speed for every other enemy's own attack-speed-scaled dash.
    private float _windupClipRate;
    private float _leapClipRate;

    [Header("Leap Settings")]
    public float landDamageMultiplier = 1f;
    [SerializeField] private float landRadius       = 3f;
    [SerializeField] private float windupDuration   = 0.4f;
    [SerializeField] private float leapDuration     = 0.4f;
    [SerializeField] private float leapArcHeight    = 3f;
    // Reshapes the vertical bob's timing WITHOUT touching horizontal leap speed or any of the actual
    // gameplay timing (hit resolution is still purely state.stateTimer-driven) — purely cosmetic, per
    // explicit design. 1 = plain sine (the old default — already a little fast-in/fast-out, but mild).
    // Values < 1 raise Mathf.Sin(t * pi) to a fractional power, which pulls the whole curve toward a fast
    // rise off the ground, a long flat hang near the peak, then a fast drop back down — smaller values
    // exaggerate this further (e.g. 0.3 is a much snappier hop-like takeoff/landing than 0.7). Values > 1
    // do the opposite (slow, floaty edges; quick pass through the middle). See UpdateView's own arc-bob
    // block for the actual Mathf.Pow application.
    [SerializeField] private float leapArcExponent  = 0.5f;
    [SerializeField] private float postLeapPause    = 0.5f;
    [SerializeField] private float landingDuration  = 0.1f;
    // Tier1-authored baseline, scaled per tier by leapsPerAttackTierGrowth below (applied in
    // OnConfigureSpawn()). See _authoredLeapsPerAttack's own doc comment for why this needs the
    // snapshot-and-reset treatment instead of just incrementing this field directly.
    [SerializeField] private int   leapsPerAttack   = 1;
    // Max bearing turn rate for leapTarget's own live-tracking during Windup (2026-08-26) — copied from
    // MortarGloomPD's identical rotationRateDegPerSecond/rotationRateDegPerSecondTierGrowth (45 base, 0.2
    // tier growth), same tier1-authored-baseline treatment as strikesPerAttack has there. See class doc
    // comment and HandleActiveState's Windup case for the tracking math itself.
    [SerializeField] private float rotationRateDegPerSecond = 45f;
    private float _authoredRotationRateDegPerSecond;
    // Max rate the tracked DISTANCE (from leapStart) can close/open during Windup — same rate-cap idea as
    // rotationRateDegPerSecond above, just for the radial component instead of the angular one, copied from
    // MortarGloomPD's identical distanceTrackingRateUnitsPerSecond.
    [SerializeField] private float distanceTrackingRateUnitsPerSecond = 10f;
    private float _authoredDistanceTrackingRateUnitsPerSecond;

    [Header("Attack Cooldown")]
    [SerializeField] private float attackCooldown = 5f;
    // Captured once in OnAwake(), reset back onto attackCooldown every pooled reuse in LateAwake(), scaled
    // by attackCooldownMultiplier (Threat Level 5) in OnConfigureSpawn() — same "_authoredX snapshot"
    // pattern as _authoredLeapsPerAttack below.
    private float _authoredAttackCooldown;

    [Header("Tier Scaling")]
    // Added onto leapsPerAttack per tier above 1 — see OnConfigureSpawn().
    [SerializeField] private int leapsPerAttackTierGrowth = 1;
    // Same linear-per-tier shape as every other *TierGrowth field — shared by both rotationRateDegPerSecond
    // and distanceTrackingRateUnitsPerSecond above, same reuse convention MortarGloomPD's own identical pair
    // already uses (see distanceTrackingRateUnitsPerSecond's doc comment there).
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;

    // Captured once in OnAwake(), reset back onto leapsPerAttack every pooled reuse in LateAwake() —
    // otherwise OnConfigureSpawn's += would keep compounding across reuses, same "_authoredX snapshot"
    // pattern established for the identical attack-count fields on Blitz/Dagger/Mortar/Spear.
    private int _authoredLeapsPerAttack;

    // The target this attack committed to, captured once at EnterWindup — matches BlitzGloomPD.
    // _windupTrackedTarget/KingSpikePD._chargeWindupTrackedTarget/ShooterGloomPD._windupTrackedTarget
    // exactly (2026-08-26 fix — this used to read live _target/_targetStats directly, which was the one
    // enemy inconsistent with the rest of the codebase's "an ongoing attack stays committed to whoever it
    // started against, immune to a taunt reassigning _target mid-Windup" rule). Drives the Windup tracking
    // block in HandleActiveState below.
    private CharacterStatsPD _windupTrackedTarget;

    // Bobbed vertically (cosmetic only) during Leaping — deliberately a dedicated wrapper (YAxisMovers),
    // not the raw model mesh transform directly: PurrDiction's PredictedTransform tracks a hurtbox/root
    // structure of its own, and driving a transform it also touches fights it (same "YAxisMovers, not
    // Visuals" fix RogueSpinAbilityPD's leap needed for the player). Everything that should visually rise with
    // the leap (model, shell, indicators, etc.) lives under this one wrapper instead.
    [Header("Indicators")]
    [SerializeField] private Transform _yAxisMovers;
    [SerializeField] private GameObject   _windupIndicator;
    [SerializeField] private WindupFillPD _windupFill;

    private Vector3 _yAxisMoversRestPosition;
    private bool    _yAxisMoversRestPositionCaptured;

    // Plain (not predicted State) shadow of state.leapTarget, written in lockstep inside EnterWindup() —
    // the same deterministic Simulate() call that decides leapTarget itself, so this is available
    // instantly on every machine with no network round-trip, unlike reading it back off `verified`.
    // Reading it off `viewState` instead wouldn't work either: PurrDiction linearly interpolates every
    // State field (including plain Vector3s) between simulated ticks for smooth rendering, and leapTarget
    // is a discrete "decided once" value — viewState would show it sliding in from whatever it was
    // previously rather than snapping instantly. A plain field isn't part of State, so it isn't subject to
    // that interpolation at all.
    private Vector3 _leapTargetSnapshot;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private Transform _windupIndicatorParent;

    protected override void OnAwake()
    {
        base.OnAwake();
        _authoredLeapsPerAttack = leapsPerAttack;
        _authoredAttackCooldown = attackCooldown;
        _authoredRotationRateDegPerSecond = rotationRateDegPerSecond;
        _authoredDistanceTrackingRateUnitsPerSecond = distanceTrackingRateUnitsPerSecond;
    }

    protected override void LateAwake()
    {
        base.LateAwake();

        // Reset to the tier1-authored baseline before OnConfigureSpawn (which always runs AFTER LateAwake(),
        // once hierarchy.Create() returns) scales it fresh for this life — see _authoredLeapsPerAttack's own
        // doc comment.
        leapsPerAttack = _authoredLeapsPerAttack;
        attackCooldown = _authoredAttackCooldown;
        rotationRateDegPerSecond = _authoredRotationRateDegPerSecond;
        distanceTrackingRateUnitsPerSecond = _authoredDistanceTrackingRateUnitsPerSecond;

        // Null-coalesced, not unconditional — LateAwake() re-fires on every pooled reuse, but by that
        // point the indicator could already be unparented from a previous life interrupted mid-leap (e.g.
        // death during Leaping). Caching only when still unset means this reliably captures the true
        // original parent exactly once (the first real spawn), without needing a second, separate
        // Awake() method — EnemyBasePD already declares its own private Awake() (which sets up
        // _renderers/_originalColors, _hurtbox, etc.), and adding another same-named private Awake() here
        // risked one silently not running (this is what caused Leaper-only NREs/stuck-red hit-flash: this
        // enemy's _originalColors was never actually populated).
        if (_windupIndicator != null) _windupIndicatorParent ??= _windupIndicator.transform.parent;

        // Guarded to a single real capture per instance, same reasoning as _windupIndicatorParent above —
        // UpdateView() bails out immediately on death (before it ever reaches the branch that resets
        // _yAxisMovers back to rest), so a Leaper that dies mid-Leaping (arc-bobbed up) gets pooled with
        // its visuals still elevated. An unconditional recapture here would then bake that elevated position
        // in as the new "rest" position for the rest of this instance's pooled life — OnRespawnReset()
        // would keep dutifully "resetting" it back to a wrong, floating baseline. This is what caused the
        // "leaper stuck floating" bug.
        if (!_yAxisMoversRestPositionCaptured && _yAxisMovers != null)
        {
            _yAxisMoversRestPosition = _yAxisMovers.localPosition;
            _yAxisMoversRestPositionCaptured = true;
        }

        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        // Re-set again in OnRespawnReset() once leapsPerAttack is actually tier-scaled (that always runs
        // AFTER OnConfigureSpawn, on this life's first Simulate() tick) — this LateAwake() assignment is
        // only ever a placeholder until then.
        currentState.leapsRemaining      = leapsPerAttack;
    }

    // Tier1-authored leapsPerAttack, scaled by leapsPerAttackTierGrowth per tier above 1 (see that field's
    // own doc comment). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call).
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        leapsPerAttack += (spawnTier - 1) * leapsPerAttackTierGrowth;
        rotationRateDegPerSecond = _authoredRotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);
        // Reuses rotationRateDegPerSecondTierGrowth itself (not a separate *TierGrowth field) — see
        // MortarGloomPD.distanceTrackingRateUnitsPerSecond's own identical doc comment.
        distanceTrackingRateUnitsPerSecond = _authoredDistanceTrackingRateUnitsPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        attackCooldown = _authoredAttackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
    }

    // Sets the WindupSpeed/LeapSpeed Animator float parameters so each clip's actual playback duration
    // matches windupDuration/leapDuration exactly (speed = clip.length / desiredDuration). Deliberately
    // NOT in LateAwake() — EnemyBasePD.Simulate()'s first-tick init block calls _animator.Rebind() (to
    // force a clean pose after pooled reuse), which resets ALL Animator Controller parameters back to
    // their defaults, wiping out whatever LateAwake() computed. OnRespawnReset() runs right after that
    // Rebind() call (on every reuse, including the very first spawn), so re-applying here is what
    // actually sticks — this was silently broken (playing at each clip's raw 1x speed instead of the
    // duration-matched speed) until now.
    private void ApplyAnimatorClipSpeeds()
    {
        if (_animator == null) return;
        foreach (var clip in _animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == "Leaper Gloom Windup")
                _windupClipRate = clip.length / windupDuration;
            else if (clip.name == "Leaper Gloom Leap")
                _leapClipRate = clip.length / leapDuration;
        }
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        var ss = GetLeapState(ref state);

        // attackCooldownTimer: only ticks while actually in Moving (2026-08-19 — uniform across every
        // multi-phase enemy's own attack-cadence cooldown now, see OrbiterGloomPD's own fix). stateTimer:
        // scaled live by EffectiveAttackSpeed every tick — see SpearGloomPD's own comment on the identical
        // pattern. This is also what drives the Leaping arc lerp below, so the leap's own traversal speed
        // scales for free too.
        if (ss == LeapState.Moving)
            state.attackCooldownTimer -= delta;
        state.stateTimer -= delta * EffectiveAttackSpeed;

        switch (ss)
        {
            case LeapState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    // leapsRemaining itself is handled by EnterWindup now, not here — see its own comment.
                    EnterWindup(ref state);
                }
                break;

            case LeapState.Windup:
                state.attackElapsed += delta * EffectiveAttackSpeed;
                // Live-tracks _windupTrackedTarget's position (the committed target, captured once at
                // EnterWindup — see that field's own doc comment), NOT live _target/_targetStats
                // (2026-08-26 fix — this used to read those directly, meaning a taunt landing mid-Windup
                // could redirect an already-started leap, inconsistent with every other tracked-windup enemy
                // in this codebase). Rate-capped exactly like MortarHazardPD's own "attached" phase — plain
                // delta, NOT delta * EffectiveAttackSpeed, since this is a fixed real-world tracking speed
                // independent of how fast this Leaper's own Windup timer happens to be counting down (see
                // MortarHazardPD.LateSimulate's identical choice). Holds the last tracked target/bearing/
                // distance (no teleport-to-stale-null) if the committed target died/went invisible-without-
                // taunting mid-Windup — the rate-capped MoveTowardsAngle/MoveTowards simply stop advancing
                // while this condition is false, which IS the "freeze at last known position" behavior (same
                // mechanism MortarHazardPD's own tracking relies on), and resumes live the instant validity
                // returns.
                if (_windupTrackedTarget != null && !_windupTrackedTarget.IsDead &&
                    (!_windupTrackedTarget.IsInvisible || _windupTrackedTarget.IsTaunting))
                {
                    Vector3 targetPos = _windupTrackedTarget.transform.position;
                    targetPos.y = 0f;
                    Vector3 toTarget = targetPos - state.leapStart;
                    toTarget.y = 0f;
                    float targetDistance = toTarget.magnitude;
                    if (targetDistance > 0.0001f)
                    {
                        float targetAngle = Mathf.Atan2(toTarget.z, toTarget.x) * Mathf.Rad2Deg;
                        state.facingAngleDeg = Mathf.MoveTowardsAngle(state.facingAngleDeg, targetAngle, rotationRateDegPerSecond * delta);
                        state.trackedDistance = Mathf.MoveTowards(state.trackedDistance, targetDistance, distanceTrackingRateUnitsPerSecond * delta);

                        float rad = state.facingAngleDeg * Mathf.Deg2Rad;
                        Vector3 dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                        state.leapTarget = state.leapStart + dir * state.trackedDistance;
                        _leapTargetSnapshot = state.leapTarget;
                    }
                }
                if (state.stateTimer <= 0f) EnterLeap(ref state);
                break;

            case LeapState.Leaping:
                state.attackElapsed += delta * EffectiveAttackSpeed;
                // Kinematic arc lerp, not the shared dash primitives — a leap ignores normal ground
                // collision along the way (matching the hurtbox/collider being disabled below), which the
                // velocity-based dash primitives aren't built for.
                if (_rigidbody != null)
                {
                    float t = 1f - Mathf.Clamp01(state.stateTimer / leapDuration);
                    Vector3 flatPos = Vector3.Lerp(state.leapStart, state.leapTarget, t);
                    _rigidbody.MovePosition(flatPos);
                }
                if (state.stateTimer <= 0f) EnterLanding(ref state);
                break;

            case LeapState.Landing:
                // Unconditional (not gated to the back half like Spear/Dagger's pause states) — matches
                // old LeaperGloom.FixedUpdate's identical unconditional FaceTargetSmooth during Landing.
                FaceTargetSmooth(delta);
                if (state.stateTimer <= 0f) EnterPostLand(ref state);
                break;

            case LeapState.PostLeap:
                FaceTargetSmooth(delta);
                if (state.stateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ss = GetLeapState(ref state);

        // Blocks stun/knockback for the whole attack sequence — same reasoning as every other attacker
        // enemy in this rebuild.
        isStunnable = ss == LeapState.Moving ? !stunResistant : false;

        // Windup is a stationary telegraph — kinematic (Case 2), matching the established convention (old
        // Leaper didn't bother with this, but every other enemy built since does, so applying it here too
        // for consistency). Leaping is fast/arcing movement handled by a direct kinematic lerp above
        // (Case 1-equivalent — collider AND hurtbox both disabled, since old system explicitly makes the
        // leaper unhittable mid-air, not just non-interacting).
        bool isWindupOrLeap = ss == LeapState.Windup || ss == LeapState.Leaping;
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = isWindupOrLeap;
            bool kinematicChanging = _rigidbody.isKinematic != shouldBeKinematic;

            // Zero velocity on EITHER transition direction, not just becoming-kinematic. A kinematic
            // rigidbody driven by MovePosition (the Leaping arc lerp above) still reports an implicit
            // velocity derived from its position delta each tick — Unity computes this so the kinematic
            // body can push dynamic bodies it collides with. That leftover velocity was still sitting on
            // the rigidbody the instant Leaping->Landing flipped isKinematic back to false, so it got
            // applied as real physics velocity and shot the leaper off in whatever direction the final
            // leap frame was moving — the "sliding after leap" bug. Matches old LeaperGloom.Land()'s
            // explicit _rb.linearVelocity = Vector3.zero right after un-kinematic-ing.
            //
            // The zero-out has to happen on whichever side of the isKinematic assignment the body is
            // actually non-kinematic — Rigidbody.velocity's setter throws "not supported on a kinematic
            // body" otherwise. Entering kinematic: it's still non-kinematic right up until the assignment
            // below, so zero first. Leaving kinematic: it's still kinematic until that same assignment
            // runs, so the zero has to come after.
            if (kinematicChanging && shouldBeKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
            if (kinematicChanging && !shouldBeKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
        }

        if (_physicsCollider != null) _physicsCollider.enabled = ss != LeapState.Leaping;
        // SetHurtboxDisabled (not a raw _hurtbox.enabled write) — this ALSO grants invulnerability + shows
        // the shell for the same window, per explicit design ("Hurtbox Disabled = Invincible" always, the
        // reverse not necessarily true — see IEnemyPD.SetHurtboxDisabled's own doc comment). Previously this
        // enemy was unhittable mid-air but NOT formally invulnerable, meaning a hit that somehow still
        // landed (e.g. a lingering AoE already resolved against a cached reference) would have dealt real
        // damage nobody could see land — this closes that gap too, not just the visual/messaging one.
        SetHurtboxDisabled(ss == LeapState.Leaping);
    }

    // ── State transitions ──────────────────────────────────────────────────

    private void EnterWindup(ref State state)
    {
        if (_target == null) return;

        // Captured here — the commit moment for this leap — not read live off _targetStats later; matches
        // BlitzGloomPD/KingSpikePD/ShooterGloomPD's own identical _windupTrackedTarget pattern. See that
        // field's own doc comment.
        _windupTrackedTarget = _targetStats;

        // Decrement lives HERE — the actual START of one leap attempt — not at landing (EnterPostLand,
        // where it used to live). A leap cancelled mid-windup or mid-air via Interrupt() now still counts
        // against the budget, since leapsRemaining already dropped the instant this ran. Refill-if-exhausted
        // (not an unconditional reset) — called from both a fresh Moving-triggered attack (leapsRemaining
        // sitting at 0 from a previous fully-completed chain, or a pooled spawn) AND EnterPostLand's own
        // mid-chain continuation (leapsRemaining already > 0, so this is a no-op there). This is also why
        // Moving's own attack-trigger and EnterMoving/OnInterrupted no longer stomp leapsRemaining
        // themselves — this single self-contained check is the only reset point now, so an interrupted
        // chain's leftover count survives into whatever attack comes next instead of being thrown away.
        if (state.leapsRemaining <= 0) state.leapsRemaining = leapsPerAttack;
        state.leapsRemaining--;

        // leapStart doubles as the tracking origin for the whole Windup (this Leaper is kinematic/stationary
        // throughout Windup — see the isWindupOrLeap kinematic block below — so it never needs re-reading
        // live), moved here from EnterLeap (2026-08-26) since live-tracking now needs it from the very start
        // of Windup, not just at the Windup->Leaping handoff.
        state.leapStart = transform.position;

        // Seeds the tracked bearing/distance to match _windupTrackedTarget's CURRENT position relative to
        // leapStart — same seeding MortarHazardPD.Launch() does for its own identical tracking fields, so
        // the first tracked Windup tick's rate-capped MoveTowardsAngle/MoveTowards starts from the real
        // current bearing/range instead of snapping in from a stale (or zeroed) previous life's value.
        Vector3 targetPos = _windupTrackedTarget.transform.position;
        targetPos.y = 0f;
        Vector3 toTarget = targetPos - state.leapStart;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude > 0.0001f)
            state.facingAngleDeg = Mathf.Atan2(toTarget.z, toTarget.x) * Mathf.Rad2Deg;
        state.trackedDistance = toTarget.magnitude;
        state.leapTarget = targetPos;
        _leapTargetSnapshot = state.leapTarget;

        // CharacterStatsPD overload (not the ref-state one) — faces _windupTrackedTarget specifically, not
        // whatever live _target currently is, same reasoning as the rest of this method.
        SnapFaceTarget(_windupTrackedTarget);
        state.stateTimer    = windupDuration;
        state.attackElapsed = 0f;
        SetLeapState(ref state, LeapState.Windup);
    }

    private void EnterLeap(ref State state)
    {
        // leapStart already set at EnterWindup (this Leaper never moves during Windup) — leapTarget is
        // whatever the Windup case's live-tracking last wrote, now permanently frozen since nothing else
        // writes it from here on. This is the whole "lock the indicator and leap target area" moment.
        state.stateTimer = leapDuration;
        SetLeapState(ref state, LeapState.Leaping);
    }

    private void EnterLanding(ref State state)
    {
        // Hard snap, correcting any residual lerp imprecision — matches old Land()'s direct position
        // assignment rather than trusting the final lerp step to land exactly on target.
        transform.position = new Vector3(state.leapTarget.x, transform.position.y, state.leapTarget.z);
        // No velocity to zero here — the rigidbody is still kinematic at this exact point (it doesn't flip
        // back to non-kinematic until the shared post-switch block runs later this same tick). That block
        // is what actually clears the leftover MovePosition-derived velocity on the kinematic->false edge —
        // see its comment for why that leftover velocity exists at all.

        state.stateTimer = landingDuration;
        SetLeapState(ref state, LeapState.Landing);

        // One-shot AoE resolve on landing, matching KingSpikePD.FireSpikeBurst/DaggerGloomPD.EnterSpin — a
        // plain distance check against CharacterStatsPD.All rather than a physics trigger/collider.
        // * size — matches SpearGloomPD/DaggerGloomPD's own size-scaled hit-check precedent.
        float   dmg    = damage * landDamageMultiplier;
        Vector3 origin = transform.position;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            // No IsInvincible pre-check (2026-08-24 fix) — TakeDamage() already no-ops safely against
            // invincibility on its own, AND is the only place Dodge credit gets awarded (see
            // CharacterStatsPD.NotifyDodge's own doc comment); skipping the call here silently ate every
            // Dodge this attack should have granted while the player was dash-invincible.
            if (stats.IsDead) continue;
            if (Vector3.Distance(stats.transform.position, origin) <= landRadius * size)
                stats.TakeDamage(dmg);
        }
    }

    private void EnterPostLand(ref State state)
    {
        // Decrement moved to EnterWindup (attack START, not this completion point) — see its own comment.
        // _target != null guard matters here — EnterWindup() silently no-ops on a null target (it needs one
        // to compute leapTarget), so without this check a target lost mid-chain left the enemy stuck forever
        // in Landing (leapsRemaining spiraling negative every tick, never actually transitioning out).
        // IsTargetStillValid — a target that went invisible-and-non-taunting since the LAST leap in this
        // chain landed shouldn't get chained into another one against them; see that property's own doc
        // comment. The leap that already resolved above is untouched either way.
        // !IsCrowdControlled — a true Stun() landing mid-sequence never interrupts the leap that's already
        // resolved above, but stops the chain from continuing into another one, falling through to PostLeap
        // instead (a Slow does NOT stop the chain — see IsCrowdControlled's own doc comment, hard CC only).
        if (state.leapsRemaining > 0 && _target != null && IsTargetStillValid && !IsCrowdControlled)
        {
            EnterWindup(ref state);
        }
        else
        {
            state.stateTimer          = postLeapPause;
            state.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
            SetLeapState(ref state, LeapState.PostLeap);
        }
    }

    private void EnterMoving(ref State state)
    {
        // leapsRemaining deliberately left untouched here — EnterWindup owns resetting it for the natural
        // target-lost/crowd-controlled early-exit path (refill-if-exhausted, not unconditional).
        // OnInterrupted() now zeroes it explicitly itself, BEFORE calling this, for the Interrupt()-driven
        // path — see its own doc comment.
        // Attack routine's over — reprioritize onto whoever's closest now, rather than sitting on whoever
        // this routine happened to start against. See EnemyBasePD.FindClosestTarget's own doc comment.
        FindClosestTarget();
        SetLeapState(ref state, LeapState.Moving);
    }

    // Fully aborts the attack chain (2026-08-12, reworked from "leave leapsRemaining untouched") — an
    // interrupted attack no longer retries with whatever leaps it had left; it zeroes the counter AND puts
    // the whole sequence on a full fresh cooldown (same formula EnterPostLand's own natural chain-end
    // already uses), so the very next Moving-state check can't immediately refill-and-restart. Moving itself
    // is stunnable (see isStunnable's own per-tick assignment above), so a Stun() paired with the same hit as
    // this Interrupt() (always called BEFORE a paired Stun(), see IEnemyPD.Interrupt()'s own doc comment)
    // freezes this enemy here via EnemyBasePD.Simulate()'s own isStunnable-gated freeze for the stun's whole
    // duration before it's ever able to attack again — no extra handling needed for that here.
    protected override void OnInterrupted()
    {
        currentState.leapsRemaining      = 0;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        EnterMoving(ref currentState);

        // Predicted-instant hide, not left for UpdateView's own stateId-driven redraw to eventually catch
        // up — Interrupt() mutates currentState out-of-band (from a different identity's own Simulate(),
        // not this enemy's own HandleActiveState flow this tick), which was leaving the windup indicator
        // visibly stuck for a stray tick before UpdateView's viewState caught up to the interrupt. Also
        // re-parents back immediately (Windup unparents the indicator to world root — see UpdateView's own
        // reparenting block) since an interrupt only ever fires during Windup (Leaping isn't interruptible
        // per CanBeInterrupted below), so it's always unparented at this exact point.
        // _lastVisualState deliberately NOT written here — see SpearGloomPD.OnInterrupted's own comment for
        // why (UpdateView's own edge-detection bookkeeping, not ours to set).
        if (_windupIndicator != null)
        {
            _windupIndicator.SetActive(false);
            _windupIndicator.transform.SetParent(_windupIndicatorParent, true);
        }
        if (_windupFill != null) _windupFill.ResetFill();
    }

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above) and
    // meant to be cancelable — CanBeInterrupted now defaults to false (see EnemyBasePD's own doc comment).
    // Leaping stays excluded regardless (per design — already mid-air on a fixed kinematic arc lerp with
    // both its collider AND hurtbox disabled, see HandleActiveState's own comment, so there's no sensible
    // "cancel and snap back to idle" here even if something did reach in).
    protected override bool CanBeInterrupted
    {
        get
        {
            var ss = GetLeapState(ref currentState);
            return ss == LeapState.Windup || ss == LeapState.Landing || ss == LeapState.PostLeap;
        }
    }


    // ── View ───────────────────────────────────────────────────────────────
    private LeapState _lastVisualState = (LeapState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        // Death is handled by base.UpdateView() above (OnDeathStop() + hierarchy.Delete()) — but that
        // doesn't stop execution from continuing into the rest of THIS method afterward. If death happens
        // while stateId still reads Windup/Leaping (e.g. killed by AoE/DoT mid-leap, never transitioning
        // back to Moving first), the SetActive(...) call further down would immediately re-enable the very
        // indicator OnDeathStop() just hid. Since the indicator is unparented to world root during that
        // window, it'd then sit there forever, detached and visible, with nothing left to ever hide it
        // again once this enemy itself is deleted/pooled — this was the "leftover circle on the ground"
        // bug. Bailing here preserves whatever OnDeathStop() already did.
        if (viewState.b.isDead) return;

        // Sourced from _leapTargetSnapshot (a plain field, not predicted State) — instantly available on
        // every machine the moment EnterWindup() decides it, with no network round-trip and no
        // interpolation blend (see the field's own doc comment). Re-applied every frame the state is
        // Windup/Leaping so the indicator always tracks whatever the current leapTarget is, rather than
        // snapshotting once on entry.
        var ss = (LeapState)viewState.stateId;
        if (_windupIndicator != null && (ss == LeapState.Windup || ss == LeapState.Leaping))
            _windupIndicator.transform.position = new Vector3(_leapTargetSnapshot.x, 0.05f, _leapTargetSnapshot.z);
        // * size (2026-08-26 fix) — matches the actual landing hit-check's own landRadius * size below; the
        // indicator wasn't scaling with Elite size at all (unlike DaggerGloomPD's spinRadius * size), so an
        // Elite Leaper's true damage radius silently reached past its own drawn circle.
        if (_windupIndicator != null) IndicatorMath.ApplyRadialTransform(_windupIndicator.transform, landRadius * size);

        // Purely cosmetic arc bob — safe off viewState (a transient misprediction here is an imperceptible
        // visual wobble, not a discrete value that needs to be exactly right).
        if (_yAxisMovers != null)
        {
            if (ss == LeapState.Leaping)
            {
                float t = 1f - Mathf.Clamp01(viewState.stateTimer / leapDuration);
                // Mathf.Sin(t*pi) is already in [0,1] for t in [0,1], so raising it to leapArcExponent is
                // always well-defined — see that field's own doc comment for the fast-liftoff/slow-arc/
                // fast-landing shape this gives at exponent < 1.
                float arcY = Mathf.Pow(Mathf.Sin(t * Mathf.PI), leapArcExponent) * leapArcHeight;
                _yAxisMovers.localPosition = _yAxisMoversRestPosition + new Vector3(0f, arcY, 0f);
            }
            else
            {
                _yAxisMovers.localPosition = _yAxisMoversRestPosition;
            }
        }

        // Live, every frame regardless of the _lastVisualState-gated block below — reacts immediately to
        // EffectiveAttackSpeed changing mid-state, same reasoning as every other attack-speed-scaled enemy
        // in this pass. The fill spans both Windup and Leaping as one combined span, driven off
        // attackElapsed (counts up continuously across the whole span, reset once at EnterWindup) rather
        // than stateTimer. stateTimer counts DOWN and gets given a brand new baseline the instant Windup
        // hands off to Leaping, so it means two different things on either side of that handoff — fine for
        // verified/simulated reads, but PurrDiction's viewState is interpolated between ticks for smooth
        // rendering, and blending "windup time remaining" against "leap time remaining" across that one
        // tick boundary produced a real, visible glitch (the fill briefly reading a wrong value) for
        // whichever single render frame straddled the transition — worst-case-visible exactly when
        // windupDuration == leapDuration, i.e. right at the midpoint. attackElapsed has one consistent
        // meaning for the WHOLE span, so interpolating it across that same boundary is safe.
        if (_windupFill != null)
        {
            float combined = windupDuration + leapDuration;
            if (ss == LeapState.Windup || ss == LeapState.Leaping)
                _windupFill.SetProgress(viewState.attackElapsed / combined);
        }
        if (_animator != null)
        {
            if (ss == LeapState.Windup)
                _animator.SetFloat(WindupSpeedHash, _windupClipRate * EffectiveAttackSpeed);
            else if (ss == LeapState.Leaping)
                _animator.SetFloat(LeapSpeedHash, _leapClipRate * EffectiveAttackSpeed);
        }

        if (ss == _lastVisualState) return;
        bool wasWindupOrLeap = _lastVisualState == LeapState.Windup || _lastVisualState == LeapState.Leaping;
        bool isWindupOrLeap  = ss == LeapState.Windup || ss == LeapState.Leaping;
        _lastVisualState = ss;

        // Unparented for the whole Windup->Leaping window (not re-parented in between, matching old
        // LeaperGloom's identical behavior) — Visuals continuously interpolates toward the leaper's own
        // rapidly-moving body during Leaping, and a child whose world position is being explicitly
        // overridden every frame gets visibly dragged along by that interpolation between corrections,
        // producing a violent jitter. Detaching entirely removes any parent to fight against.
        if (_windupIndicator != null)
        {
            if (isWindupOrLeap && !wasWindupOrLeap)
                _windupIndicator.transform.SetParent(null, true);
            else if (!isWindupOrLeap && wasWindupOrLeap)
                _windupIndicator.transform.SetParent(_windupIndicatorParent, true);
        }

        if (_windupIndicator != null)
            _windupIndicator.SetActive(ss == LeapState.Windup || ss == LeapState.Leaping);
        // Reset once the whole Windup+Leaping span is over (Leaping->Landing) — the live SetProgress block
        // above already covers driving it forward throughout both states, this just clears it back to 0 on
        // the way out (Landing/PostLeap/Moving all have nothing to show).
        if (_windupFill != null && !isWindupOrLeap) _windupFill.ResetFill();

        if (_animator == null) return;
        switch (ss)
        {
            case LeapState.Windup:
                _animator.Play(WindupHash, 0, 0f);
                break;
            case LeapState.Leaping:
                _animator.Play(LeapHash, 0, 0f);
                break;
            default:
                _animator.Play(IdleHash, 0, 0f);
                break;
        }
    }

    protected override void OnDeathStop()
    {
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.ResetFill();
        // Snaps out of whatever attack pose this enemy died mid-animation in — see SpearGloomPD's own
        // OnDeathStop comment for why (stateId is frozen from here on, so nothing else will do this).
        // Guarded on activeInHierarchy — see SpearGloomPD's own comment on the same guard.
        if (_animator != null && gameObject.activeInHierarchy) _animator.Play(IdleHash, 0, 0f);
    }

    protected override void OnRespawnReset()
    {
        ApplyAnimatorClipSpeeds();
        currentState.stateId             = 0;
        currentState.leapsRemaining      = leapsPerAttack;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.stateTimer          = 0f;
        currentState.attackElapsed       = 0f;
        if (_yAxisMovers != null) _yAxisMovers.localPosition = _yAxisMoversRestPosition;
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.ResetFill();
        // In case death happened mid-Windup/Leaping, while the indicator was unparented — a fresh reuse
        // should never inherit a previous life's detached indicator.
        if (_windupIndicator != null && _windupIndicator.transform.parent != _windupIndicatorParent)
            _windupIndicator.transform.SetParent(_windupIndicatorParent, true);
        // In case death happened mid-Windup/Leaping, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (stateId resets to Moving above), but resetting explicitly
        // here avoids even a one-tick window where a freshly-reused instance reads as kinematic, and
        // ensures a dead-mid-air leaper doesn't stay permanently unhittable.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        if (_hurtbox != null)         _hurtbox.enabled         = true;
        _lastVisualState = (LeapState)(-1);
        _windupTrackedTarget = null;
    }
}
