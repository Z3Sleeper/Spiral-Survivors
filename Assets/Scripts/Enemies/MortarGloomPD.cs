using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Same range-holding/dash-away AI as ShooterGloomPD, but the actual damage isn't a flying projectile — it's
// an instant hit in a line-shaped hazard that appears at the target's location, oriented perpendicular to
// the mortar's own firing direction (see MortarHazardPD). The flying shell the player sees (see
// MortarProjectileVFXPD) is purely cosmetic and has zero gameplay effect; Fire/PostFire's own durations are
// deliberately uncorrelated with how long that shell takes to fly — the ONLY link between the hazard/shell
// and the Mortar's own state machine is projectileTravelDuration (see its own field comment). The shell is no
// longer spawned by THIS class's own Fire-state transition (2026-08-26) — MortarHazardPD spawns it itself,
// off its own lock transition, so it stays correctly linked to the hazard even if this Mortar dies mid-Windup
// (see MortarHazardPD's own class doc comment for the bug that fix addresses); projectileVfxPrefab and its
// sibling fields below are still authored here, just forwarded into hazard.Launch() at SpawnHazard()-time.
//
// The DashingAway/Windup/Fire sequence for one strike (or a strikesPerAttack-repeated burst) is an "ongoing
// attack" (see EnemyBasePD.CaptureAttackAnchor) — it always resolves against whichever target it committed
// to when it started, even if _target gets reassigned mid-sequence. BetweenStrikes re-evaluates _target
// fresh for whether/where the NEXT strike in the chain aims, same convention ShooterGloomPD's own
// BetweenShots already uses. No multishot — "that's a projectile thing and this isn't a projectile" (direct
// user quote) — strikesPerAttack is the only repeat knob.
public class MortarGloomPD : EnemyBasePD<MortarGloomPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   mortarStateId;
        public float mortarStateTimer;
        public float attackCooldownTimer;
        public int   strikesRemaining;
        // The CURRENT strike's hazard, valid/cancelable only while mortarStateId == Windup for that same
        // strike (see OnInterrupted's own doc comment) — not a list, since earlier strikes' hazards already
        // passed Fire and are permanently uncancelable, so only ever one entry matters at a time.
        public PredictedObjectID pendingHazardId;
        // Runtime-scaled — see EnemyBaseState.scaledMaxHealth's own doc comment for why these live in State
        // rather than as a plain field mutated by OnConfigureSpawn (2026-09-22 follow-up pass).
        public int   scaledStrikesPerAttack;
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

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    private static readonly int IdleHash     = Animator.StringToHash("Idle");
    private static readonly int WindupHash   = Animator.StringToHash("MortarWindup");
    private static readonly int FireHash     = Animator.StringToHash("MortarFire");
    private static readonly int PostFireHash = Animator.StringToHash("MortarPostFire");
    private static readonly int WindupSpeedHash   = Animator.StringToHash("WindupSpeed");
    private static readonly int FireSpeedHash     = Animator.StringToHash("FireSpeed");
    private static readonly int PostFireSpeedHash = Animator.StringToHash("PostFireSpeed");

    public enum MortarState { Moving, DashingAway, Windup, Fire, BetweenStrikes, PostFire }

    [Header("Mortar Settings")]
    // Same "too close" trigger threshold reasoning as ShooterGloomPD.minAttackRange.
    [SerializeField] private float minAttackRange       = 8f;
    [SerializeField] private float windupDuration       = 0.5f;
    // Pure Mortar-side animation beats — deliberately uncorrelated with projectileTravelDuration below (see
    // that field's own comment and the class's own doc comment).
    [SerializeField] private float fireDuration         = 0.1f;
    [SerializeField] private float betweenStrikePause   = 0.25f;
    [SerializeField] private float postFireDuration     = 0.5f;
    // Tier1-authored, pristine read-only baseline forever (2026-09-22) — OnConfigureSpawn scales it into
    // State.scaledStrikesPerAttack instead of mutating this in place, so no _authoredX snapshot/reset is
    // needed anymore (see EnemyBaseState.scaledMaxHealth's own doc comment for the full reasoning).
    [SerializeField] private int   strikesPerAttack     = 1;
    [SerializeField] private float strikeDamageMultiplier = 1f;
    [SerializeField] private float dashSpeedMultiplier  = 4f;
    [SerializeField] private float dashDistanceBuffer   = 0.5f;

    [Header("Hazard (danger indicator + damage, independent once spawned)")]
    [SerializeField] private GameObject hazardPrefab; // carries MortarHazardPD
    [SerializeField] private float hazardWidth = 8f; // perpendicular to firing direction
    [SerializeField] private float hazardDepth = 2f; // thickness along firing direction
    // Shared by BOTH the hazard's own fill duration (windupDuration + this) AND the cosmetic shell's flight
    // time — this is the ONLY place travel time and the hazard are linked. Deliberately NOT linked to
    // fireDuration/postFireDuration, which are pure animation beats with no bearing on when the hazard
    // actually resolves (see class doc comment — explicit user correction).
    [SerializeField] private float projectileTravelDuration = 0.5f;
    // Max bearing turn rate for the hazard's own live-tracking during Windup (2026-08-21) — copied from
    // ShielderGloomPD's own rotationRateDegPerSecond/rotationRateDegPerSecondTierGrowth (45 base, 0.2 tier
    // growth). Tier-scaled here (the hazard itself has no tier concept of its own) and passed into
    // MortarHazardPD.Launch() as a one-time snapshot, same as windupAttackSpeed — see MortarHazardPD's own
    // doc comment for why. Same pristine-baseline treatment as strikesPerAttack above — scales into
    // State.scaledRotationRateDegPerSecond instead.
    [SerializeField] private float rotationRateDegPerSecond = 45f;
    // Max rate the hazard's own tracked DISTANCE (from the firer) can close/open during Windup (2026-08-23)
    // — same rate-cap idea as rotationRateDegPerSecond above, just for the radial component instead of the
    // angular one ("Z tracking" — distance snapped instantly before this, only the bearing swing was
    // capped). Started at 10 (player base move speed), lowered to 5 (2026-08-28) after live testing — same
    // change applied to LeaperGloomPD's identical field. Tier-scaled by rotationRateDegPerSecondTierGrowth
    // (2026-08-23) — deliberately reusing the SAME growth rate as rotationRateDegPerSecond rather than a
    // separate tunable, pending confirmation the two should actually tune identically; split into its own
    // *TierGrowth field later if not. Scales into State.scaledDistanceTrackingRateUnitsPerSecond.
    [SerializeField] private float distanceTrackingRateUnitsPerSecond = 5f;

    [Header("Projectile VFX (cosmetic only — zero gameplay effect)")]
    [SerializeField] private GameObject projectileVfxPrefab; // carries MortarProjectileVFXPD
    [SerializeField] private float projectileArcHeight = 3f;
    // "Slight yz firing offset" — spawns slightly up and forward from the mortar's own root, never sideways.
    // Both scale with `size` (Elite scaling) — see MortarHazardPD.SpawnProjectileVfx, which actually applies
    // them now (forwarded into hazard.Launch() below, at SpawnHazard()-time).
    [SerializeField] private float projectileSpawnHeightOffset  = 1f;
    [SerializeField] private float projectileSpawnForwardOffset = 0f;

    [Header("Attack Cooldown")]
    // Same pristine-baseline treatment as strikesPerAttack above — scales into State.scaledAttackCooldown.
    [SerializeField] private float attackCooldown = 5f;

    [Header("Tier Scaling")]
    // Added onto strikesPerAttack per tier above 1 — see OnConfigureSpawn(). Authored to 0 (2026-09-30) —
    // per-attack scaling was producing too much visual clutter/forced dash usage relative to other enemies;
    // attackCooldownTierGrowth below takes over instead. Left in place (not removed) in case it's ever
    // reinstated.
    [SerializeField] private int strikesPerAttackTierGrowth = 1;
    // Same linear-per-tier shape as every other *TierGrowth field — see ShielderGloomPD's own identical
    // field for the precedent this was copied from.
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;
    // TRUE compounding multiplier (2026-09-30, explicit exception to the project's usual "never
    // compounding/exponential" *TierGrowth convention) — replaces strikesPerAttackTierGrowth as this enemy's
    // own tier-scaling lever. scaledAttackCooldown = attackCooldown * attackCooldownTierGrowth^(spawnTier-1),
    // e.g. 0.4 default: T1 = authored, T2 = authored*0.4, T3 = authored*0.4*0.4, T4 = authored*0.4^3 — each
    // tier multiplies the PREVIOUS tier's already-reduced cooldown again, not a flat per-tier percentage off
    // the base. Combined multiplicatively with attackCooldownMultiplier (Threat Level 5's own halving) too.
    [SerializeField] private float attackCooldownTierGrowth = 0.4f;

    // Baseline "clip length / baseline duration" ratios — same reasoning as ShooterGloomPD's own
    // _windupClipRate/_shootClipRate/_postShotClipRate.
    private float _windupClipRate;
    private float _fireClipRate;
    private float _postFireClipRate;

    private MortarState GetMortarState(ref State s) => (MortarState)s.mortarStateId;
    private void SetMortarState(ref State s, MortarState st) => s.mortarStateId = (int)st;

    protected override void LateAwake()
    {
        base.LateAwake();
        // Placeholder only — strikesPerAttack/attackCooldown are the pristine tier1 baseline until
        // OnConfigureSpawn() (always runs AFTER LateAwake(), once hierarchy.Create() returns) writes the
        // real scaled values into State. Re-set again in OnRespawnReset() once those are actually
        // tier-scaled (runs AFTER OnConfigureSpawn, on this life's first Simulate() tick).
        currentState.attackCooldownTimer = attackCooldown;
        currentState.strikesRemaining    = strikesPerAttack;
    }

    // Tier1-authored strikesPerAttack, scaled by strikesPerAttackTierGrowth per tier above 1 (see that
    // field's own doc comment). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call).
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        currentState.scaledStrikesPerAttack = strikesPerAttack + (spawnTier - 1) * strikesPerAttackTierGrowth;
        currentState.scaledRotationRateDegPerSecond = rotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);
        // Reuses rotationRateDegPerSecondTierGrowth itself (not a separate *TierGrowth field) — see
        // distanceTrackingRateUnitsPerSecond's own doc comment.
        currentState.scaledDistanceTrackingRateUnitsPerSecond = distanceTrackingRateUnitsPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment), combined
        // multiplicatively with attackCooldownTierGrowth's own TRUE compounding per-tier reduction (see that
        // field's own doc comment — Mathf.Pow, not a linear "1 - (tier-1)*growth" term). Re-set the
        // placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        currentState.scaledAttackCooldown = attackCooldown * attackCooldownMultiplier * Mathf.Pow(attackCooldownTierGrowth, spawnTier - 1);
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
    }

    // See ShooterGloomPD.ApplyAnimatorClipSpeeds's own doc comment for why this lives here (not LateAwake())
    // and re-runs from OnRespawnReset() on every reuse.
    private void ApplyAnimatorClipSpeeds()
    {
        if (_animator == null) return;
        foreach (var clip in _animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == "Mortar Gloom Windup")
                _windupClipRate = clip.length / windupDuration;
            else if (clip.name == "Mortar Gloom Fire")
                _fireClipRate = clip.length / fireDuration;
            else if (clip.name == "Mortar Gloom PostFire")
                _postFireClipRate = clip.length / postFireDuration;
        }
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded) EnterWindup(ref state);

        var ms = GetMortarState(ref state);

        // attackCooldownTimer: only ticks while actually in Moving (2026-08-19 — uniform across every
        // multi-phase enemy's own attack-cadence cooldown now, see OrbiterGloomPD's own fix). mortarStateTimer:
        // scaled live by EffectiveAttackSpeed every tick — see SpearGloomPD's own comment on the identical
        // pattern.
        if (ms == MortarState.Moving)
            state.attackCooldownTimer -= delta;
        state.mortarStateTimer -= delta * EffectiveAttackSpeed;

        switch (ms)
        {
            case MortarState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    // Decrement/refill lives here (the actual start of the attempt), same reasoning as
                    // ShooterGloomPD's own Moving-case comment — an attempt cancelled via Interrupt()
                    // mid-dash or mid-windup still counts against the budget.
                    if (state.strikesRemaining <= 0) state.strikesRemaining = state.scaledStrikesPerAttack;
                    state.strikesRemaining--;
                    CaptureAttackAnchor(ref state);
                    float dist = Vector3.Distance(transform.position, _target.position);
                    if (dist < minAttackRange) EnterDashAway(ref state, dist);
                    else                        EnterWindup(ref state);
                }
                break;

            case MortarState.DashingAway:
                // Movement is driven by the base HandleDash() call in Simulate(); nothing to do here beyond
                // waiting for dashJustEnded (handled above) to advance to Windup.
                break;

            case MortarState.Windup:
                if (state.mortarStateTimer <= 0f) EnterFire(ref state);
                break;

            case MortarState.Fire:
                if (state.mortarStateTimer <= 0f)
                {
                    if (state.strikesRemaining > 0 && IsTargetStillValid && !IsCrowdControlled) EnterBetweenStrikes(ref state);
                    else                                                                         EnterPostFire(ref state);
                }
                break;

            case MortarState.BetweenStrikes:
                if (state.mortarStateTimer <= betweenStrikePause * 0.5f) FaceTargetSmooth(delta);
                if (state.mortarStateTimer <= 0f)
                {
                    if (_target == null)
                    {
                        EnterPostFire(ref state);
                    }
                    else
                    {
                        if (state.strikesRemaining <= 0) state.strikesRemaining = state.scaledStrikesPerAttack;
                        state.strikesRemaining--;
                        CaptureAttackAnchor(ref state);
                        EnterWindup(ref state);
                    }
                }
                break;

            case MortarState.PostFire:
                if (state.mortarStateTimer <= postFireDuration * 0.5f) FaceTargetSmooth(delta);
                if (state.mortarStateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ms = GetMortarState(ref state);

        // Windup+Fire are the committed/telegraphed beats — protected from stun-freeze, same convention as
        // ShooterGloomPD's own Windup/Shooting. Moving/BetweenStrikes/PostFire are freezable.
        isStunnable = ms == MortarState.Moving || ms == MortarState.BetweenStrikes || ms == MortarState.PostFire;
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = ms == MortarState.Windup || ms == MortarState.Fire;
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }

        // Belt-and-suspenders with StartDashToPoint/HandleDash's own internal disable/re-enable — matches
        // every other dashing enemy's explicit per-tick assignment.
        if (_physicsCollider != null)
            _physicsCollider.enabled = ms != MortarState.DashingAway;
    }

    // ── State transitions ────────────────────────────────────────────────────

    private void EnterDashAway(ref State state, float currentDistance)
    {
        SetMortarState(ref state, MortarState.DashingAway);

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
        state.mortarStateTimer = windupDuration;
        SnapFaceAnchor(ref state);
        SetMortarState(ref state, MortarState.Windup);
        SpawnHazard(ref state); // independent from here on — see OnInterrupted's own doc comment
    }

    private void EnterFire(ref State state)
    {
        state.mortarStateTimer = fireDuration;
        SetMortarState(ref state, MortarState.Fire);
        // No longer spawns the projectile here (2026-08-26) — see MortarHazardPD's own class doc comment for
        // why that moved onto the hazard's own lock transition instead.
    }

    private void EnterBetweenStrikes(ref State state)
    {
        state.mortarStateTimer = betweenStrikePause;
        SetMortarState(ref state, MortarState.BetweenStrikes);
    }

    private void EnterPostFire(ref State state)
    {
        state.mortarStateTimer   = postFireDuration;
        state.attackCooldownTimer = state.scaledAttackCooldown;
        SetMortarState(ref state, MortarState.PostFire);
    }

    private void EnterMoving(ref State state)
    {
        // Attack routine's over — reprioritize onto whoever's closest now, same reasoning as
        // ShooterGloomPD.EnterMoving.
        FindClosestTarget();
        SetMortarState(ref state, MortarState.Moving);
    }

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above) and
    // meant to be cancelable — CanBeInterrupted now defaults to false (see EnemyBasePD's own doc comment).
    // Fire stays excluded regardless (per design — "already launched," see class doc comment); BetweenStrikes/
    // Moving/PostFire are isStunnable, so were never committed in the first place.
    protected override bool CanBeInterrupted
    {
        get
        {
            var ms = GetMortarState(ref currentState);
            return ms == MortarState.Windup || ms == MortarState.DashingAway;
        }
    }

    // Interrupting mid-Windup actively cancels that strike's already-spawned hazard (erases the indicator,
    // stops its pending damage) — the danger indicator hasn't "committed" yet at that point. CanBeInterrupted
    // above already guarantees this can never fire during Fire/PostFire, where the hazard is permanent (see
    // MortarHazardPD's own class doc comment). No-op during BetweenStrikes/Moving/DashingAway — nothing
    // currently pending to cancel there (the previous strike's hazard, if any, already passed Fire).
    //
    // strikesRemaining/attackCooldownTimer explicitly reset here (2026-08-19 fix, same bug class as
    // Spear/Dagger/Blitz/Shooter/Leaper/Orbiter's own OnInterrupted()) — an interrupted strike fully aborts
    // the whole multi-strike chain (zero the remaining count) and goes on a full, freshly-randomized
    // cooldown (matching EnterPostFire's own formula, this enemy's natural chain-end), rather than leaving
    // both untouched and letting the very next tick immediately re-windup with whatever strikes were left.
    protected override void OnInterrupted()
    {
        CancelPendingHazardIfWindingUp();
        currentState.strikesRemaining    = 0;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        EnterMoving(ref currentState);
    }

    // Shared by OnInterrupted() above and Die() below (2026-08-29) — the Mortar itself dying mid-Windup
    // needs to cancel that strike's pending hazard for the exact same reason an interrupt does: without
    // this, the hazard (which deliberately "lives independently... will still detonate even if the Mortar
    // that spawned it has since died," per MortarHazardPD's own class doc comment) kept charging and firing
    // a projectile from the dead Mortar's last position — live-tested, this was happening far too often to
    // treat as an acceptable edge case. Windup-only gated, same as OnInterrupted's own check — during
    // Fire/PostFire the hazard is already committed/permanent, so a death landing near-simultaneously with
    // Fire is explicitly still fine to let ride (already addressed separately, per explicit design).
    // Both call sites are Simulate()-context-safe for this predicted-state mutation (hazard.Cancel() writes
    // currentState.cancelled): Interrupt() itself documents "mutates currentState out-of-band, from a
    // different identity's own Simulate() call," and Die() is called synchronously from within TakeDamage(),
    // also Simulate()-context — NOT from OnDeathStop()/UpdateView(), which would be unsafe.
    private void CancelPendingHazardIfWindingUp()
    {
        if (GetMortarState(ref currentState) != MortarState.Windup) return;
        var go = hierarchy.GetGameObject(currentState.pendingHazardId);
        if (go != null && go.TryGetComponent(out MortarHazardPD hazard)) hazard.Cancel();
    }

    protected override void Die()
    {
        base.Die();
        CancelPendingHazardIfWindingUp();
    }

    // Same reasoning as Die()'s own call above, for the difficulty-pacing wipe path instead of a real death
    // (2026-10-16 fix) — SilentDespawn() previously had no hook at all, so a Mortar wiped mid-Windup left its
    // independently-living hazard indicator filling and firing a projectile from empty space. See
    // EnemyBasePD.OnSilentDespawn's own doc comment.
    protected override void OnSilentDespawn()
    {
        base.OnSilentDespawn();
        CancelPendingHazardIfWindingUp();
    }

    // ── Hazard + VFX spawn ──────────────────────────────────────────────────

    // Initial placement only, at the captured attackAnchorPosition (see CaptureAttackAnchor's own doc
    // comment) — the hazard then LIVE-TRACKS _targetStats (read here, at spawn, which IS the commit moment
    // for this strike — see MortarHazardPD's own class doc comment for why passing the reference itself,
    // rather than re-deriving it later, is what keeps this immune to _targetStats getting reassigned to
    // someone else mid-Windup) for the rest of its own "attached" phase, then locks automatically once its
    // own "fired" phase begins. EffectiveAttackSpeed passed in as a one-time snapshot (not a live reference —
    // see MortarHazardPD's own class doc comment for why) so the hazard's tracking phase paces itself in
    // lockstep with THIS strike's own attack-speed-scaled Windup duration.
    private void SpawnHazard(ref State state)
    {
        if (hazardPrefab == null) return;

        Vector3 targetPos = state.b.attackAnchorPosition;
        targetPos.y = 0f; // lands at y=0, per design
        Vector3 fireDir = targetPos - transform.position;
        fireDir.y = 0f;
        if (fireDir.sqrMagnitude < 0.0001f) fireDir = transform.forward;
        fireDir.Normalize();
        Quaternion rot = Quaternion.LookRotation(fireDir); // indicator's local X = perpendicular width, Z = depth

        var id = hierarchy.Create(hazardPrefab, targetPos, rot);
        if (!id.HasValue) return;
        state.pendingHazardId = id.Value; // tracked so OnInterrupted() can Cancel() it if Windup gets interrupted

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out MortarHazardPD hazard))
            hazard.Launch(windupDuration, projectileTravelDuration, hazardWidth * size, hazardDepth * size,
                B.scaledDamage * strikeDamageMultiplier, _targetStats, transform.position, EffectiveAttackSpeed,
                state.scaledRotationRateDegPerSecond, state.scaledDistanceTrackingRateUnitsPerSecond,
                projectileVfxPrefab, projectileArcHeight, projectileSpawnHeightOffset,
                projectileSpawnForwardOffset, size, TierAccentMaterial);
    }

    // ── View ──────────────────────────────────────────────────────────────────
    private MortarState _lastVisualState = (MortarState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        var ms = (MortarState)viewState.mortarStateId;

        // Live, every frame regardless of the _lastVisualState-gated block below — same reasoning as
        // ShooterGloomPD's own live-animator-speed pattern.
        if (_animator != null)
        {
            if (ms == MortarState.Windup)
                _animator.SetFloat(WindupSpeedHash, _windupClipRate * EffectiveAttackSpeed);
            else if (ms == MortarState.Fire)
                _animator.SetFloat(FireSpeedHash, _fireClipRate * EffectiveAttackSpeed);
            else if (ms == MortarState.PostFire)
                _animator.SetFloat(PostFireSpeedHash, _postFireClipRate * EffectiveAttackSpeed);
        }

        if (ms == _lastVisualState) return;
        _lastVisualState = ms;

        if (_animator == null) return;
        switch (ms)
        {
            case MortarState.Moving:
                _animator.Play(IdleHash, 0, 0f);
                break;
            case MortarState.Windup:
                // Only the FIRST windup of an attack sequence plays the actual raise-into-firing-position
                // clip — same reasoning as ShooterGloomPD's own shotsRemaining check, translated to strikes.
                if (viewState.strikesRemaining == viewState.scaledStrikesPerAttack - 1)
                    _animator.Play(WindupHash, 0, 0f);
                break;
            case MortarState.Fire:
                _animator.Play(FireHash, 0, 0f);
                break;
            case MortarState.PostFire:
                _animator.Play(PostFireHash, 0, 0f);
                break;
        }
    }

    // No windup indicator on the Mortar itself to hide (the danger indicator lives on the independently-
    // spawned MortarHazardPD) — only needed here to snap out of whatever attack pose this enemy died
    // mid-animation in, same reasoning as ShooterGloomPD's own OnDeathStop.
    protected override void OnDeathStop()
    {
        if (_animator != null && gameObject.activeInHierarchy) _animator.Play(IdleHash, 0, 0f);
    }

    protected override void OnRespawnReset()
    {
        ApplyAnimatorClipSpeeds();
        currentState.mortarStateId       = 0;
        currentState.mortarStateTimer    = 0f;
        currentState.strikesRemaining    = currentState.scaledStrikesPerAttack;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        // In case death happened mid-Windup/Fire, while kinematic was on — HandleActiveState would correct
        // this on the next tick anyway (mortarStateId resets to Moving above), but resetting explicitly here
        // avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (MortarState)(-1);
    }
}
