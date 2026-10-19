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
        // Tracked direction/distance from leapStart during Windup, needed in predicted State (not a plain
        // field) since Vector3.RotateTowards/Mathf.MoveTowards both read their own previous value every
        // tick, which a rollback replay must restore correctly. Meaningless outside Windup (leapTarget itself
        // is what matters once Leaping locks it).
        // leapDirection is a plain Vector3 (2026-09-14, replaces a CompressedFloat facingAngleDeg bearing
        // tracked via Mathf.Atan2/Mathf.MoveTowardsAngle) — matches BlitzGloomPD.state.blitzDirection/
        // TrackWindupTarget exactly now. The scalar-angle approach relied on Mathf.DeltaAngle's mod-360
        // wraparound logic, and facingAngleDeg was never re-normalized back into a canonical range between
        // ticks (only reset at each fresh EnterWindup) — a real, confirmed-live bug where the tracked bearing
        // would occasionally snap SEVERAL DEGREES BACKWARDS (opposite the player's own strafe direction)
        // during ordinary Windup tracking, with no equivalent issue on Blitz's identical-purpose vector-based
        // tracking. A 3D direction vector has no periodic/wraparound representation to go wrong in the first
        // place — Vector3.RotateTowards always rotates along the single shortest great-circle arc between two
        // unit vectors, unconditionally, so this class of bug is structurally impossible here.
        public Vector3 leapDirection;
        public CompressedFloat trackedDistance;
        // Runtime-scaled — see EnemyBaseState.scaledMaxHealth's own doc comment for why these live in State
        // rather than as a plain field mutated by OnConfigureSpawn (2026-09-22 follow-up pass).
        public int   scaledLeapsPerAttack;
        public float scaledAttackCooldown;
        public float scaledRotationRateDegPerSecond;
        public float scaledDistanceTrackingRateUnitsPerSecond;
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
    // to be re-derived live every frame instead of set once at spawn. _windupClipRate is still scaled by
    // EffectiveAttackSpeed (see UpdateView) — the windup telegraph scales with attack speed, same as every
    // other enemy's own windup. _leapClipRate now scales by LeapSpeedRatio instead (2026-09-23 fix — see its
    // own doc comment): the leap itself (the actual travel) scales with move speed, not attack speed, so its
    // animation rate has to match whatever's actually driving the arc lerp's real-time completion rate now,
    // or the visible leap animation would drift out of sync with where the Leaper's real body actually is.
    private float _windupClipRate;
    private float _leapClipRate;

    // The Leaping state's own real-time completion rate (2026-09-23 fix — per explicit design, a dash/leap's
    // actual travel scales with move speed, windup scales with attack speed, and neither should scale with
    // both; this used to just reuse EffectiveAttackSpeed for its own state.stateTimer decrement, exactly
    // like every other phase here, which meant the leap's own traversal speed — a "dash" in every sense
    // that matters, just shaped as a fixed-duration arc lerp instead of a velocity — was scaling with attack
    // speed instead of move speed, the same dual-scaling bug just fixed for the other 6 dash-capable
    // enemies' own EnemyBasePD.HandleDash/HandleOffsetChase). leapDuration is this enemy's own tier-1-
    // authored baseline — moveSpeed (EnemyBasePD's own plain field, NOT EffectiveMoveSpeed) is its matching
    // tier-1-authored move speed, so at exactly that baseline (tier 1, no slow, no enrage) this ratio is
    // exactly 1 and a leap takes exactly leapDuration seconds, same as before this fix. Above baseline (a
    // higher tier's own scaledMoveSpeed growth, a move-speed buff, etc.) the leap completes proportionally
    // faster — the same relationship every other dasher's own EffectiveMoveSpeed * dashSpeedMultiplier
    // velocity already has to its own baseline, just expressed as a time-compression ratio instead of a
    // velocity, since this leap is a fixed-duration arc lerp rather than a constant-velocity dash.
    private float LeapSpeedRatio => EffectiveMoveSpeed / Mathf.Max(0.0001f, moveSpeed);

    [Header("Leap Settings")]
    public float landDamageMultiplier = 1f;
    [SerializeField] private float landRadius       = 3f;
    [SerializeField] private float windupDuration   = 0.4f;
    [SerializeField] private float leapDuration     = 0.4f;
    [SerializeField] private float leapArcHeight    = 3f;
    // Reshapes the vertical bob's timing WITHOUT touching horizontal leap speed or any of the actual
    // gameplay timing (hit resolution is still purely state.stateTimer-driven) — purely cosmetic, per
    // explicit design. Piecewise (2026-08-28, replaces a single Mathf.Pow(sin, exponent) formula — that
    // approach had a real, unfixable problem: sin(t*pi) behaves like a straight line near t=0/t=1, so
    // raising it to any exponent below 1 gives the curve an INFINITE slope right at the edges — no exponent
    // choice avoids this, they only differ in how fast the spike happens. A piecewise curve with an
    // explicit, bounded edge duration/speed is the actual fix, not a tuning number).
    //
    // edgeFraction: the fraction of leapDuration spent on EACH of liftoff and landing (e.g. 0.2 = 20%
    // liftoff + 20% landing + 60% hang in the middle). Within that window, height rises/falls LINEARLY —
    // a genuine constant, finite, tunable speed (edgeHeightFraction * leapArcHeight / (edgeFraction *
    // leapDuration)), not a mathematical spike. Clamped to 0.49 at read-time (UpdateView) so the two edge
    // windows can never fully consume the hang phase.
    [SerializeField] private float leapArcEdgeFraction = 0.2f;
    // How much of the TOTAL arc height gets covered during the fast edge windows above — the remaining
    // (1 - edgeHeightFraction) of the height happens during the slow hang phase in the middle, via a gentle
    // sine arc spread across the large remaining time window. Where the fast edge meets the slow hang there
    // IS a small, finite kink in speed (a straight ramp and a sine arc don't naturally match slopes at the
    // handoff) — a deliberate simplification, not a bug; nowhere near the old formula's literal infinite
    // spike, and not worth the extra complexity of a fully slope-matched stitch for a cosmetic bob.
    [SerializeField] private float leapArcEdgeHeightFraction = 0.7f;
    [SerializeField] private float postLeapPause    = 0.5f;
    [SerializeField] private float landingDuration  = 0.1f;
    // Tier1-authored, pristine read-only baseline forever (2026-09-22) — OnConfigureSpawn scales it into
    // State.scaledLeapsPerAttack instead of mutating this in place, so no _authoredX snapshot/reset is
    // needed anymore (see EnemyBaseState.scaledMaxHealth's own doc comment for the full reasoning).
    [SerializeField] private int   leapsPerAttack   = 1;
    // Max bearing turn rate for leapTarget's own live-tracking during Windup (2026-08-26) — copied from
    // MortarGloomPD's identical rotationRateDegPerSecond/rotationRateDegPerSecondTierGrowth (45 base, 0.2
    // tier growth). See class doc comment and HandleActiveState's Windup case for the tracking math itself.
    // Same pristine-baseline treatment as leapsPerAttack above — scales into
    // State.scaledRotationRateDegPerSecond instead.
    [SerializeField] private float rotationRateDegPerSecond = 45f;
    // Max rate the tracked DISTANCE (from leapStart) can close/open during Windup — same rate-cap idea as
    // rotationRateDegPerSecond above, just for the radial component instead of the angular one, copied from
    // MortarGloomPD's identical distanceTrackingRateUnitsPerSecond. Scales into
    // State.scaledDistanceTrackingRateUnitsPerSecond.
    [SerializeField] private float distanceTrackingRateUnitsPerSecond = 5f;

    [Header("Attack Cooldown")]
    // Same pristine-baseline treatment as leapsPerAttack above — scales into State.scaledAttackCooldown.
    [SerializeField] private float attackCooldown = 5f;

    [Header("Tier Scaling")]
    // Added onto leapsPerAttack per tier above 1 — see OnConfigureSpawn(). Authored to 0 (2026-09-30) —
    // per-attack scaling was producing too much visual clutter/forced dash usage relative to other enemies;
    // attackCooldownTierGrowth below takes over instead. Left in place (not removed) in case it's ever
    // reinstated.
    [SerializeField] private int leapsPerAttackTierGrowth = 1;
    // Same linear-per-tier shape as every other *TierGrowth field — shared by both rotationRateDegPerSecond
    // and distanceTrackingRateUnitsPerSecond above, same reuse convention MortarGloomPD's own identical pair
    // already uses (see distanceTrackingRateUnitsPerSecond's doc comment there).
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;
    // TRUE compounding multiplier (2026-09-30, explicit exception to the project's usual "never
    // compounding/exponential" *TierGrowth convention) — replaces leapsPerAttackTierGrowth as this enemy's
    // own tier-scaling lever. scaledAttackCooldown = attackCooldown * attackCooldownTierGrowth^(spawnTier-1),
    // e.g. 0.4 default: T1 = authored, T2 = authored*0.4, T3 = authored*0.4*0.4, T4 = authored*0.4^3 — each
    // tier multiplies the PREVIOUS tier's already-reduced cooldown again, not a flat per-tier percentage off
    // the base. Combined multiplicatively with attackCooldownMultiplier (Threat Level 5's own halving) too.
    [SerializeField] private float attackCooldownTierGrowth = 0.4f;

    // The target this attack committed to, captured once at EnterWindup — matches BlitzGloomPD.
    // _windupTrackedTarget/KingSpikePD._chargeWindupTrackedTarget/ShooterGloomPD._windupTrackedTarget
    // exactly (2026-08-26 fix — this used to read live _target/_targetStats directly, which was the one
    // enemy inconsistent with the rest of the codebase's "an ongoing attack stays committed to whoever it
    // started against, immune to a taunt reassigning _target mid-Windup" rule). Drives the Windup tracking
    // block in HandleActiveState below.
    private IFriendlyPD _windupTrackedTarget;

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

    // Plain (not predicted State) shadow of state.leapTarget, written in lockstep inside EnterWindup()/the
    // Windup tracking loop — the same deterministic Simulate() call that decides leapTarget itself, so this
    // is available instantly on every machine with no network round-trip, unlike reading it back off
    // `verified`. MUST stay exactly equal to the real state.leapTarget the actual leap/hit-detection uses
    // (2026-09-14 — a prior version of this comment proposed reading viewState.trackedDistance/facingAngleDeg
    // instead to avoid a one-frame visual snap; reverted, since that would let the shown indicator diverge
    // from where the attack genuinely resolves — the entire point of a windup indicator is that it's a
    // truthful preview, and a smaller visual imperfection is strictly preferable to indicator/hitbox desync).
    private Vector3 _leapTargetSnapshot;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    // Reinstated (2026-09-14) — Leaping unparents _windupIndicator from wherever it's normally parented (see
    // UpdateView's own Leaping-transition comment for why), so this is needed again to know where to put it
    // back once the leap ends.
    private Transform _windupIndicatorParent;
    // The indicator's own AUTHORED local position/rotation under _windupIndicatorParent, captured once
    // alongside it (2026-09-14 fix) — every reparent-BACK site restores these explicitly instead of trusting
    // SetParent(parent, worldPositionStays: true) to land back on them. worldPositionStays:true computes a
    // BRAND NEW local pose that preserves whatever WORLD pose the indicator happened to be frozen at during
    // Leaping (see UpdateView's own Leaping-entry comment — position is deliberately frozen there, and
    // rotation is whatever it last passively inherited the instant before unparenting) — and nothing ever
    // reset that baked pose back to identity afterward, so it silently persisted into every subsequent Windup
    // for the rest of this Leaper's life. First windup after a fresh spawn/pooled reuse was always correct
    // (the indicator's local pose was still whatever OnRespawnReset/the prefab left it at); ONLY Leapers that
    // had already completed at least one full Leap showed the tracked-indicator lag/offset bug this fixes —
    // confirmed live, this is exactly the "second-leap-onward only" symptom that gave the bug away.
    private Vector3    _windupIndicatorLocalPos;
    private Quaternion _windupIndicatorLocalRot;
    private bool       _windupIndicatorLocalPoseCaptured;

    protected override void LateAwake()
    {
        base.LateAwake();

        // Null-coalesced, not unconditional — LateAwake() re-fires on every pooled reuse, but by that point
        // the indicator could already be unparented from a previous life interrupted mid-Leaping (e.g. death
        // during Leaping). Caching only when still unset means this reliably captures the true original
        // parent exactly once (the first real spawn).
        if (_windupIndicator != null) _windupIndicatorParent ??= _windupIndicator.transform.parent;

        // Guarded the same way as _yAxisMoversRestPosition below — a single real capture per instance, not
        // unconditional, for the identical reason: a pooled reuse could already be mid-Leaping (unparented,
        // local pose meaningless right now) by the time LateAwake() re-fires, so only the FIRST ever capture
        // (fresh spawn, local pose still whatever the prefab authored) is trustworthy. See
        // _windupIndicatorLocalPos/_windupIndicatorLocalRot's own doc comment for why this needs restoring
        // explicitly at all.
        if (_windupIndicator != null && !_windupIndicatorLocalPoseCaptured)
        {
            _windupIndicatorLocalPos = _windupIndicator.transform.localPosition;
            _windupIndicatorLocalRot = _windupIndicator.transform.localRotation;
            _windupIndicatorLocalPoseCaptured = true;
        }

        // Guarded to a single real capture per instance —
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

        currentState.attackCooldownTimer = attackCooldown;
        // Re-set again in OnRespawnReset() once leapsPerAttack is actually tier-scaled (that always runs
        // AFTER OnConfigureSpawn, on this life's first Simulate() tick) — this LateAwake() assignment is
        // only ever a placeholder until then.
        currentState.leapsRemaining      = leapsPerAttack;
    }

    // Reparents _windupIndicator back under _windupIndicatorParent AND restores its exact authored local
    // pose (2026-09-14 fix) — every call site that reparents it back used to pass SetParent(parent, true)
    // (worldPositionStays), which computes a BRAND NEW local pose to preserve whatever WORLD pose the
    // indicator happened to be frozen at during Leaping, instead of restoring the ORIGINAL one — see
    // _windupIndicatorLocalPos/_windupIndicatorLocalRot's own doc comment for the live-confirmed bug this
    // fixes (every Windup after a Leaper's first completed Leap silently inherited a corrupted local
    // rotation/position offset). worldPositionStays:false here is deliberate, not just "doesn't matter" — no
    // visual pop is possible from the switch either way, since every call site below either SetActive(false)s
    // the indicator in the same breath or it's already inactive.
    private void ReparentWindupIndicatorHome()
    {
        if (_windupIndicator == null) return;
        _windupIndicator.transform.SetParent(_windupIndicatorParent, false);
        _windupIndicator.transform.localPosition = _windupIndicatorLocalPos;
        _windupIndicator.transform.localRotation = _windupIndicatorLocalRot;
    }

    // Tier1-authored leapsPerAttack, scaled by leapsPerAttackTierGrowth per tier above 1 (see that field's
    // own doc comment). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call).
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        currentState.scaledLeapsPerAttack = leapsPerAttack + (spawnTier - 1) * leapsPerAttackTierGrowth;
        currentState.scaledRotationRateDegPerSecond = rotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);
        // Reuses rotationRateDegPerSecondTierGrowth itself (not a separate *TierGrowth field) — see
        // MortarGloomPD.distanceTrackingRateUnitsPerSecond's own identical doc comment.
        currentState.scaledDistanceTrackingRateUnitsPerSecond = distanceTrackingRateUnitsPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment), combined
        // multiplicatively with attackCooldownTierGrowth's own TRUE compounding per-tier reduction (see that
        // field's own doc comment — Mathf.Pow, not a linear "1 - (tier-1)*growth" term). Re-set the
        // placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        currentState.scaledAttackCooldown = attackCooldown * attackCooldownMultiplier * Mathf.Pow(attackCooldownTierGrowth, spawnTier - 1);
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
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
        // pattern — EXCEPT while actually Leaping, which scales by LeapSpeedRatio instead (2026-09-23 fix —
        // see that property's own doc comment). This also drives the Leaping arc lerp below, so the leap's
        // own traversal speed follows whichever rate is active.
        if (ss == LeapState.Moving)
            state.attackCooldownTimer -= delta;
        state.stateTimer -= delta * (ss == LeapState.Leaping ? LeapSpeedRatio : EffectiveAttackSpeed);

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
                // in this codebase). Rate scaled by EffectiveAttackSpeed (2026-08-31, was plain delta — see
                // EnemySpawnerPD.difficultyAttackSpeedGrowthPerLevel's own doc comment for why: a fixed
                // real-world tracking speed while the windup ITSELF was speeding up meant re-aiming got
                // relatively stiffer as attack speed climbed, same asymmetry fixed on every other tracked-
                // windup enemy this same pass — MortarHazardPD's own "attached" phase got the identical
                // change). Holds the last tracked target/bearing/distance (no teleport-to-stale-null) if the
                // committed target died/went invisible-without-taunting mid-Windup — the rate-capped
                // MoveTowardsAngle/MoveTowards simply stop advancing while this condition is false, which IS
                // the "freeze at last known position" behavior, and resumes live the instant validity returns.
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
                        Vector3 dir = toTarget / targetDistance;
                        // Vector3.RotateTowards, not Mathf.Atan2+MoveTowardsAngle (2026-09-14) — see
                        // state.leapDirection's own doc comment for the confirmed-live backwards-snap bug this
                        // replaces. Matches BlitzGloomPD.TrackWindupTarget exactly, including the
                        // maxMagnitudeDelta: 0f (both state.leapDirection and dir are already unit vectors, so
                        // magnitude should never change, only direction).
                        Vector3 newDir = Vector3.RotateTowards(state.leapDirection, dir, state.scaledRotationRateDegPerSecond * Mathf.Deg2Rad * delta * EffectiveAttackSpeed, 0f);
                        state.trackedDistance = Mathf.MoveTowards(state.trackedDistance, targetDistance, state.scaledDistanceTrackingRateUnitsPerSecond * delta * EffectiveAttackSpeed);

                        // The Leaper's own REAL body now actually turns to match leapDirection (2026-09-14 —
                        // previously this bearing was computed but never applied to any transform at all,
                        // which is what let it silently drift out of sync with whatever the body visually
                        // faced). Same _rigidbody.MoveRotation(Quaternion.LookRotation(dir)) convention
                        // EnemyBasePD.SnapFaceTarget's own kinematic-rotation writes already use. This is now
                        // the single source of truth for this Leaper's facing — the tick-accurate physics
                        // root's own rotation, not a separate parallel value — so _windupIndicator (a child of
                        // Visuals, which continuously interpolates TOWARD this same tick-accurate rotation)
                        // can go back to plain passive inheritance for its own facing, no explicit per-frame
                        // rotation write or unparenting needed on the indicator side at all (see UpdateView's
                        // own comment).
                        _rigidbody.MoveRotation(Quaternion.LookRotation(newDir));
                        state.leapDirection = newDir;
                        state.leapTarget = state.leapStart + newDir * state.trackedDistance;
                        _leapTargetSnapshot = state.leapTarget;
                    }
                }
                if (state.stateTimer <= 0f) EnterLeap(ref state);
                break;

            case LeapState.Leaping:
                // LeapSpeedRatio, not EffectiveAttackSpeed (2026-09-23 fix) — has to match stateTimer's own
                // rate above, or the windup+leap combined fill (UpdateView, driven off attackElapsed) would
                // drift out of sync with when the Leaper actually lands (stateTimer reaching 0).
                state.attackElapsed += delta * LeapSpeedRatio;
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
        if (state.leapsRemaining <= 0) state.leapsRemaining = state.scaledLeapsPerAttack;
        state.leapsRemaining--;

        // leapStart doubles as the tracking origin for the whole Windup (this Leaper is kinematic/stationary
        // throughout Windup — see the isWindupOrLeap kinematic block below — so it never needs re-reading
        // live), moved here from EnterLeap (2026-08-26) since live-tracking now needs it from the very start
        // of Windup, not just at the Windup->Leaping handoff.
        state.leapStart = transform.position;

        // Seeds the tracked direction/distance to match _windupTrackedTarget's CURRENT position relative to
        // leapStart — same seeding MortarHazardPD.Launch() does for its own identical tracking fields, so
        // the first tracked Windup tick's rate-capped RotateTowards/MoveTowards starts from the real
        // current bearing/range instead of snapping in from a stale (or zeroed) previous life's value.
        Vector3 targetPos = _windupTrackedTarget.transform.position;
        targetPos.y = 0f;
        Vector3 toTarget = targetPos - state.leapStart;
        toTarget.y = 0f;
        state.trackedDistance = toTarget.magnitude;
        state.leapTarget = targetPos;
        _leapTargetSnapshot = state.leapTarget;

        // CharacterStatsPD overload (not the ref-state one) — faces _windupTrackedTarget specifically, not
        // whatever live _target currently is, same reasoning as the rest of this method. Its own returned dir
        // (not a separately-computed Atan2) seeds state.leapDirection — matches BlitzGloomPD.EnterWindup
        // exactly (2026-09-14), only written on a non-zero return so a degenerate/guard-failed snap holds
        // whatever leapDirection a previous life left rather than stomping it with zero.
        Vector3 snapDir = SnapFaceTarget(_windupTrackedTarget);
        if (snapDir != Vector3.zero) state.leapDirection = snapDir;
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

    // See SpearGloomPD._stabOverlapResults's own doc comment for the buffer-size reasoning.
    private static readonly Collider[] _landOverlapResults = new Collider[8];

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

        // One-shot AoE resolve on landing via Physics.OverlapSphereNonAlloc against
        // CombatLayerMasks.FriendlyHurtboxMask (2026-08-30, was a manual FriendlyTargetRegistryPD distance
        // check) — see SpearGloomPD.EnterStab's own doc comment for why enemy AoE attacks now match every
        // player weapon's own collider-query convention.
        // * size — matches SpearGloomPD/DaggerGloomPD's own size-scaled hit-check precedent.
        float   dmg    = B.scaledDamage * landDamageMultiplier;
        Vector3 origin = transform.position;

        int n = Physics.OverlapSphereNonAlloc(origin, landRadius * size, _landOverlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _landOverlapResults[i].GetComponent<IFriendlyPD>();
            // No IsInvincible pre-check (2026-08-24 fix) — TakeDamage() already no-ops safely against
            // invincibility on its own, AND is the only place Dodge credit gets awarded (see
            // CharacterStatsPD.NotifyDodge's own doc comment); skipping the call here silently ate every
            // Dodge this attack should have granted while the player was dash-invincible.
            if (stats == null || stats.IsDead) continue;
            stats.TakeDamage(dmg, false, ObjectId);
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
            state.attackCooldownTimer = state.scaledAttackCooldown;
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
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        EnterMoving(ref currentState);

        // Predicted-instant hide, not left for UpdateView's own stateId-driven redraw to eventually catch
        // up — Interrupt() mutates currentState out-of-band (from a different identity's own Simulate(),
        // not this enemy's own HandleActiveState flow this tick), which was leaving the windup indicator
        // visibly stuck for a stray tick before UpdateView's viewState caught up to the interrupt.
        // _lastVisualState deliberately NOT written here — see SpearGloomPD.OnInterrupted's own comment for
        // why (UpdateView's own edge-detection bookkeeping, not ours to set).
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null) _windupFill.Hide();
        // Explicit reparent, not left for UpdateView's own edge-detected transition to eventually catch up —
        // an interrupt during Windup leaves _windupIndicator unparented (see UpdateView's own transition
        // comment), and relying on the next natural stateId change to fix it assumes UpdateView keeps
        // running normally afterward, which isn't guaranteed (e.g. this instance getting pooled/deactivated
        // before another view tick happens) — same "leftover circle on the ground" class of bug this
        // codebase has hit before. Fixed immediately here instead.
        if (_windupIndicator != null && _windupIndicator.transform.parent != _windupIndicatorParent)
            ReparentWindupIndicatorHome();
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

    // Piecewise cosmetic arc-height curve — see leapArcEdgeFraction/leapArcEdgeHeightFraction's own doc
    // comments for the full reasoning. t is the normalized 0-1 progress through the Leaping state
    // (UpdateView's own responsibility to compute — this method is a pure function of t alone, no State
    // reads, so it's safe to call from view code with no simulation-safety concerns).
    private float ComputeLeapArcHeight(float t)
    {
        float edge = Mathf.Clamp(leapArcEdgeFraction, 0f, 0.49f);
        if (edge <= 0f) return Mathf.Sin(t * Mathf.PI) * leapArcHeight; // degenerate — no edge phase, plain sine

        float edgeHeight = leapArcEdgeHeightFraction * leapArcHeight;

        if (t < edge)
        {
            // Liftoff — linear, constant speed: edgeHeight / (edge * leapDuration) units/sec.
            return edgeHeight * (t / edge);
        }
        if (t > 1f - edge)
        {
            // Landing — mirror of liftoff.
            return edgeHeight * ((1f - t) / edge);
        }

        // Hang — gentle sine arc covering the REMAINING height (edgeHeight up to leapArcHeight and back down
        // to edgeHeight), spread across the large middle time window. hangSpan > 0 guaranteed since edge is
        // clamped to at most 0.49 above.
        float hangSpan = 1f - 2f * edge;
        float localHang = (t - edge) / hangSpan;
        return edgeHeight + (leapArcHeight - edgeHeight) * Mathf.Sin(localHang * Mathf.PI);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        // Death is handled by base.UpdateView() above (OnDeathStop() + hierarchy.Delete()) — but that
        // doesn't stop execution from continuing into the rest of THIS method afterward. If death happens
        // while stateId still reads Windup/Leaping (e.g. killed by AoE/DoT mid-leap, never transitioning
        // back to Moving first), the SetActive(...) call further down would immediately re-enable the very
        // indicator OnDeathStop() just hid. Bailing here preserves whatever OnDeathStop() already did.
        if (viewState.b.isDead) return;

        // state.leapDirection/trackedDistance (see HandleActiveState's own Windup case) describe the tracked
        // target as a direction+distance from state.leapStart. HandleActiveState's Windup case now applies
        // leapDirection directly to this Leaper's own REAL body (_rigidbody.MoveRotation, 2026-09-14) — the
        // physics root's rotation IS the tracked direction now, a single source of truth, not a separate
        // parallel value the indicator had to fight to reproduce. _windupIndicator (a child of Visuals, which already continuously interpolates
        // toward that same tick-accurate rotation for rendering, same as this enemy's own body mesh) needs NO
        // explicit position/rotation write of its own anymore — plain passive inheritance is already correct,
        // same reasoning every other purely-cosmetic child of Visuals in this codebase already relies on. This
        // replaces an earlier version that wrote an explicit WORLD rotation directly onto _windupIndicator
        // every frame — technically correct in isolation, but a world write on an object still parented under
        // a continuously-smoothed ancestor is exactly the class of read-timing mismatch the fill-mesh jitter
        // fix already had to solve for position; the actual fix was making the SOURCE (the body's own real
        // rotation) correct, not fighting the smoother with a second, competing write on the child.
        // trackedDistance is exactly "how far forward along the tracked bearing the target sits," safe to
        // pass straight into ShowCircle's centerForwardOffset (pure local space, see its own doc comment) —
        // WindupIndicator's inherited facing now genuinely matches it by construction. * size (2026-08-26
        // fix) — matches the actual landing hit-check's own landRadius * size below; the indicator wasn't
        // scaling with Elite size at all (unlike DaggerGloomPD's spinRadius * size), so an Elite Leaper's true
        // damage radius silently reached past its own drawn circle.
        var ss = (LeapState)viewState.stateId;
        if (_windupFill != null && ss == LeapState.Windup)
            _windupFill.ShowCircle(landRadius * size, viewState.trackedDistance);

        // Purely cosmetic arc bob — safe off viewState (a transient misprediction here is an imperceptible
        // visual wobble, not a discrete value that needs to be exactly right).
        if (_yAxisMovers != null)
        {
            if (ss == LeapState.Leaping)
            {
                float t = 1f - Mathf.Clamp01(viewState.stateTimer / leapDuration);
                float arcY = ComputeLeapArcHeight(t);
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
                // LeapSpeedRatio, not EffectiveAttackSpeed (2026-09-23 fix) — matches the arc lerp's own
                // real traversal rate now, see LeapSpeedRatio's own doc comment.
                _animator.SetFloat(LeapSpeedHash, _leapClipRate * LeapSpeedRatio);
        }

        if (ss == _lastVisualState) return;
        LeapState previousState = _lastVisualState;
        bool wasWindupOrLeap = previousState == LeapState.Windup || previousState == LeapState.Leaping;
        bool isWindupOrLeap  = ss == LeapState.Windup || ss == LeapState.Leaping;
        _lastVisualState = ss;

        if (_windupIndicator != null)
        {
            if (ss == LeapState.Leaping && previousState != LeapState.Leaping)
            {
                // Entering Leaping specifically (not the whole Windup->Leaping window — Windup no longer
                // needs unparenting at all now that the Leaper's own body actually tracks leapDirection, see
                // this method's own top-of-function comment). The target is now FROZEN
                // (_leapTargetSnapshot/trackedDistance/leapDirection only ever update during the Windup
                // case, see HandleActiveState's own comment), but the Leaper's own body is about to move fast
                // via its own kinematic arc — unparenting removes _windupIndicator from Visuals'
                // continuously-interpolating hierarchy entirely, same fix the old system originally used.
                // Uses _leapTargetSnapshot (the RAW simulated value), deliberately NOT viewState — this MUST
                // stay exactly equal to state.leapTarget, the value EnterLeap/the actual jump and hit
                // detection use, or the indicator would stop being a truthful preview of where the attack
                // really lands (see _leapTargetSnapshot's own doc comment — a prior version of this fix tried
                // reading viewState instead to smooth out a snap, and that was reverted specifically because
                // it broke this guarantee). _leapTargetSnapshot itself is a plain field (no interpolation
                // blend, see its own doc comment), so this write can't be stale either. centerForwardOffset
                // stays at its 0 default — this object's own (now frozen) position IS the center; no
                // per-frame ShowCircle call needed for the rest of Leaping since nothing here changes again
                // until the leap ends.
                _windupIndicator.transform.SetParent(null, true);
                _windupIndicator.transform.position = new Vector3(_leapTargetSnapshot.x, 0.05f, _leapTargetSnapshot.z);
                if (_windupFill != null)
                {
                    _windupFill.ShowCircle(landRadius * size);
                    // Forces an immediate redraw using the NEW position/offset set just above — the "live,
                    // every frame" SetProgress call further up this same method already ran THIS frame,
                    // before this transition block, using the OLD (still-trackedDistance) values, since
                    // ShowCircle only ever updates the fields SetProgress reads, not the rendered result
                    // itself. Without this second call, fillMesh/the outline would render one frame with the
                    // brand-new WindupIndicator position but the stale offset from last frame — a visible
                    // one-frame flash to the wrong spot before the NEXT frame's SetProgress call caught up
                    // and self-corrected.
                    _windupFill.SetProgress(viewState.attackElapsed / (windupDuration + leapDuration));
                }
            }
            else if (previousState == LeapState.Leaping && ss != LeapState.Leaping)
            {
                // Leaving Leaping — reparent back to wherever this indicator originally lived (captured once
                // in LateAwake), same as every other pooled-reuse-safe field in this codebase. See
                // ReparentWindupIndicatorHome's own doc comment for why this restores the exact original local
                // pose explicitly rather than trusting SetParent's own worldPositionStays.
                ReparentWindupIndicatorHome();
            }
            _windupIndicator.SetActive(isWindupOrLeap);
        }
        // Reset once the whole Windup+Leaping span is over (Leaping->Landing) — the live SetProgress block
        // above already covers driving it forward throughout both states, this just clears it back to 0 on
        // the way out (Landing/PostLeap/Moving all have nothing to show).
        if (_windupFill != null && !isWindupOrLeap) _windupFill.Hide();

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

    // Restores the windup indicator on a misprediction-revert — OnDeathStop() below is a one-way latch (see
    // EnemyBasePD.OnHiddenStateChanged's own doc comment) that hides+unparents the indicator the instant
    // isDead is predicted true, but nothing ever undoes that if the death turns out to be wrong. Unlike the
    // render-hide/hurtbox/physicsCollider, which EnemyBasePD's own revert block already restores, nothing
    // re-shows the indicator on its own here: the Leaper's own stateId never actually left Windup/Leaping
    // (only the separate isDead flag mispredicted), so UpdateView's _lastVisualState edge-detect (line 796)
    // never sees a state change and never re-enters the block that would normally SetActive(true) it.
    // Confirmed live (2026-10-15): indicator stuck permanently missing after a mid-leap death misprediction
    // that correctly reverted everything else. currentState, not a viewState param — this hook has none (see
    // EnemyBasePD.OnHiddenStateChanged's own doc comment for why it can't, being declared on the shared
    // non-generic base both EnemyBasePD and EnemyBasePD<TState> subclasses use) — same "read currentState
    // directly" convention OnInterrupted()/CanBeInterrupted already use elsewhere in this exact file.
    protected override void OnHiddenStateChanged(bool hidden)
    {
        base.OnHiddenStateChanged(hidden);
        if (hidden || _windupIndicator == null) return;

        var ss = GetLeapState(ref currentState);
        if (ss != LeapState.Windup && ss != LeapState.Leaping) return;

        if (ss == LeapState.Leaping)
        {
            _windupIndicator.transform.SetParent(null, true);
            _windupIndicator.transform.position = new Vector3(_leapTargetSnapshot.x, 0.05f, _leapTargetSnapshot.z);
        }
        _windupIndicator.SetActive(true);
        if (_windupFill != null) _windupFill.ShowCircle(landRadius * size);
    }

    protected override void OnDeathStop()
    {
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.Hide();
        // Explicit reparent, fixed immediately rather than left for OnRespawnReset's own defensive check to
        // catch on the NEXT pooled reuse (which could be a long time away) — same "leftover circle on the
        // ground" class of bug OnInterrupted's own identical fix addresses. Dying mid-Windup/Leaping leaves
        // _windupIndicator unparented (see UpdateView's own transition comment).
        if (_windupIndicator != null && _windupIndicator.transform.parent != _windupIndicatorParent)
            ReparentWindupIndicatorHome();
        // Snaps out of whatever attack pose this enemy died mid-animation in — see SpearGloomPD's own
        // OnDeathStop comment for why (stateId is frozen from here on, so nothing else will do this).
        // Guarded on activeInHierarchy — see SpearGloomPD's own comment on the same guard.
        if (_animator != null && gameObject.activeInHierarchy) _animator.Play(IdleHash, 0, 0f);
    }

    protected override void OnRespawnReset()
    {
        ApplyAnimatorClipSpeeds();
        currentState.stateId             = 0;
        currentState.leapsRemaining      = currentState.scaledLeapsPerAttack;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        currentState.stateTimer          = 0f;
        currentState.attackElapsed       = 0f;
        if (_yAxisMovers != null) _yAxisMovers.localPosition = _yAxisMoversRestPosition;
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.Hide();
        // In case death happened mid-Leaping, while the indicator was unparented — a fresh reuse should
        // never inherit a previous life's detached indicator.
        if (_windupIndicator != null && _windupIndicator.transform.parent != _windupIndicatorParent)
            ReparentWindupIndicatorHome();
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
