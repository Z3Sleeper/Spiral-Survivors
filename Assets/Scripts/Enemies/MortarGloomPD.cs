using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Same range-holding/dash-away AI as ShooterGloomPD, but the actual damage isn't a flying projectile — it's
// an instant hit in a line-shaped hazard that appears at the target's location, oriented perpendicular to
// the mortar's own firing direction (see MortarHazardPD). The flying shell the player sees (see
// MortarProjectileVFXPD) is purely cosmetic and has zero gameplay effect; Fire/PostFire's own durations are
// deliberately uncorrelated with how long that shell takes to fly — the ONLY link between the hazard/shell
// and the Mortar's own state machine is projectileTravelDuration (see its own field comment).
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
    [SerializeField] private float minAttackRange       = 3f;
    [SerializeField] private float attackCooldown       = 5f;
    [SerializeField] private float windupDuration       = 0.5f;
    // Pure Mortar-side animation beats — deliberately uncorrelated with projectileTravelDuration below (see
    // that field's own comment and the class's own doc comment).
    [SerializeField] private float fireDuration         = 0.1f;
    [SerializeField] private float betweenStrikePause   = 0.3f;
    [SerializeField] private float postFireDuration     = 0.3f;
    [SerializeField] private int   strikesPerAttack     = 1;
    [SerializeField] private float strikeDamageMultiplier = 1f;
    [SerializeField] private float dashSpeedMultiplier  = 3f;
    [SerializeField] private float dashDistanceBuffer   = 0.5f;

    [Header("Hazard (danger indicator + damage, independent once spawned)")]
    [SerializeField] private GameObject hazardPrefab; // carries MortarHazardPD
    [SerializeField] private float hazardWidth = 4f; // perpendicular to firing direction
    [SerializeField] private float hazardDepth = 1f; // thickness along firing direction
    // Shared by BOTH the hazard's own fill duration (windupDuration + this) AND the cosmetic shell's flight
    // time — this is the ONLY place travel time and the hazard are linked. Deliberately NOT linked to
    // fireDuration/postFireDuration, which are pure animation beats with no bearing on when the hazard
    // actually resolves (see class doc comment — explicit user correction).
    [SerializeField] private float projectileTravelDuration = 0.6f;

    [Header("Projectile VFX (cosmetic only — zero gameplay effect)")]
    [SerializeField] private GameObject projectileVfxPrefab; // carries MortarProjectileVFXPD
    [SerializeField] private float projectileArcHeight = 3f;
    // "Slight yz firing offset" — spawns slightly up and forward from the mortar's own root, never sideways.
    // Both scale with `size` (Elite scaling) — see SpawnProjectileVfx.
    [SerializeField] private float projectileSpawnHeightOffset  = 0.5f;
    [SerializeField] private float projectileSpawnForwardOffset = 0.5f;
    [SerializeField] private Material projectileMaterial;

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
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.strikesRemaining    = strikesPerAttack;
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

        state.attackCooldownTimer -= delta;
        state.mortarStateTimer    -= delta * EffectiveAttackSpeed;

        var ms = GetMortarState(ref state);

        switch (ms)
        {
            case MortarState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    // Decrement/refill lives here (the actual start of the attempt), same reasoning as
                    // ShooterGloomPD's own Moving-case comment — an attempt cancelled via Interrupt()
                    // mid-dash or mid-windup still counts against the budget.
                    if (state.strikesRemaining <= 0) state.strikesRemaining = strikesPerAttack;
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
                        if (state.strikesRemaining <= 0) state.strikesRemaining = strikesPerAttack;
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

        StartDashToPoint(ref state, baseMoveSpeed * dashSpeedMultiplier, destination);
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
        SpawnProjectileVfx(ref state);
    }

    private void EnterBetweenStrikes(ref State state)
    {
        state.mortarStateTimer = betweenStrikePause;
        SetMortarState(ref state, MortarState.BetweenStrikes);
    }

    private void EnterPostFire(ref State state)
    {
        state.mortarStateTimer   = postFireDuration;
        state.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
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
    protected override void OnInterrupted()
    {
        if (GetMortarState(ref currentState) == MortarState.Windup)
        {
            var go = hierarchy.GetGameObject(currentState.pendingHazardId);
            if (go != null && go.TryGetComponent(out MortarHazardPD hazard)) hazard.Cancel();
        }
        EnterMoving(ref currentState);
    }

    // ── Hazard + VFX spawn ──────────────────────────────────────────────────

    // Aims at the captured attackAnchorPosition (see CaptureAttackAnchor's own doc comment), not live
    // _target — same reasoning as ShooterGloomPD.FireShots.
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
            hazard.Launch(windupDuration + projectileTravelDuration, hazardWidth * size, hazardDepth * size,
                damage * strikeDamageMultiplier);
    }

    private void SpawnProjectileVfx(ref State state)
    {
        if (projectileVfxPrefab == null) return;

        Vector3 targetPos = state.b.attackAnchorPosition;
        targetPos.y = 0f;
        Vector3 fireDir = targetPos - transform.position;
        fireDir.y = 0f;
        if (fireDir.sqrMagnitude < 0.0001f) fireDir = transform.forward;
        fireDir.Normalize();
        Vector3 spawnPos = transform.position + Vector3.up * (projectileSpawnHeightOffset * size) + fireDir * (projectileSpawnForwardOffset * size);

        var id = hierarchy.Create(projectileVfxPrefab, spawnPos, Quaternion.LookRotation(fireDir));
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out MortarProjectileVFXPD vfx))
            vfx.Launch(spawnPos, targetPos, projectileTravelDuration, projectileArcHeight, size, projectileMaterial);
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
                if (viewState.strikesRemaining == strikesPerAttack - 1)
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
        currentState.strikesRemaining    = strikesPerAttack;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        // In case death happened mid-Windup/Fire, while kinematic was on — HandleActiveState would correct
        // this on the next tick anyway (mortarStateId resets to Moving above), but resetting explicitly here
        // avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (MortarState)(-1);
    }
}
