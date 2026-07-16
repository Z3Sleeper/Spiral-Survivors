using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Enemies/LeaperGloom.cs — leaps to a point near the target (random angle,
// fixed distance, matching KingSpikePD's remote-burst offset convention) and deals AoE damage on landing.
// Old system's aoeHitbox (a trigger Collider, briefly enabled on landing) is replaced with a one-shot
// distance check against CharacterStatsPD.All, matching KingSpikePD's Spike Burst / DaggerGloomPD's Spin —
// cheaper than physics here since it's checking against a small, bounded, already-in-memory set (players).
// The leap itself is a kinematic Vector3.Lerp (with a purely cosmetic sine-arc model bob), not the shared
// dash primitives — old system disables the hurtbox entirely mid-air (can't be hit while airborne) but
// deliberately leaves contact damage untouched, matched here via no CanContactDamage override.
public class LeaperGloomPD : EnemyBasePD<LeaperGloomPD.State>
{
    public struct State : IPredictedData<State>
    {
        public EnemyBaseState b;
        public int     stateId;              // cast to/from LeapState, same convention as SpearGloomPD
        public float   stateTimer;
        public float   attackCooldownTimer;
        public Vector3 leapStart;
        public Vector3 leapTarget;            // locked in at Windup entry, reused through Leaping/Landing
        public int     leapsRemaining;
        public void Dispose() { b.contactCooldowns.Dispose(); }
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

    [Header("Leap Settings")]
    public float landDamageMultiplier = 1f;
    [SerializeField] private float landRadius       = 3f;
    [SerializeField] private float windupDuration   = 0.8f;
    [SerializeField] private float leapDuration     = 0.5f;
    [SerializeField] private float leapArcHeight    = 3f;
    [SerializeField] private float postLeapPause    = 0.5f;
    [SerializeField] private float attackCooldown   = 4f;
    [SerializeField] private float landingDuration  = 0.1f;
    [SerializeField] private int   leapsPerAttack   = 1;
    [SerializeField] private float leapOffsetRadius = 1f;
    [SerializeField] private Transform _model;        // bobbed vertically (cosmetic only) during Leaping
    [SerializeField] private GameObject   _windupIndicator;
    [SerializeField] private WindupFillPD _windupFill;

    private Vector3 _modelRestPosition;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private Transform _windupIndicatorParent;

    protected override void LateAwake()
    {
        base.LateAwake();

        // Null-coalesced, not unconditional — LateAwake() re-fires on every pooled reuse, but by that
        // point the indicator could already be unparented from a previous life interrupted mid-leap (e.g.
        // death during Leaping). Caching only when still unset means this reliably captures the true
        // original parent exactly once (the first real spawn), without needing a second, separate
        // Awake() method — EnemyBasePD already declares its own private Awake() (which sets up
        // _renderers/_originalColors, _hurtbox, etc.), and adding another same-named private Awake() here
        // risked one silently not running (this is what caused Leaper-only NREs/stuck-red hit-flash: this
        // enemy's _originalColors was never actually populated).
        if (_windupIndicator != null) _windupIndicatorParent ??= _windupIndicator.transform.parent;

        if (_model != null) _modelRestPosition = _model.localPosition;

        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.leapsRemaining      = leapsPerAttack;
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
                _animator.SetFloat(WindupSpeedHash, clip.length / windupDuration);
            else if (clip.name == "Leaper Gloom Leap")
                _animator.SetFloat(LeapSpeedHash, clip.length / leapDuration);
        }
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        state.attackCooldownTimer -= delta;
        state.stateTimer          -= delta;

        var ss = GetLeapState(ref state);

        switch (ss)
        {
            case LeapState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    state.leapsRemaining = leapsPerAttack;
                    EnterWindup(ref state);
                }
                break;

            case LeapState.Windup:
                if (state.stateTimer <= 0f) EnterLeap(ref state);
                break;

            case LeapState.Leaping:
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
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }

        if (_physicsCollider != null) _physicsCollider.enabled = ss != LeapState.Leaping;
        if (_hurtbox != null)         _hurtbox.enabled         = ss != LeapState.Leaping;
    }

    // ── State transitions ──────────────────────────────────────────────────

    private void EnterWindup(ref State state)
    {
        if (_target == null) return;

        // Fixed-radius, random-angle offset — matches KingSpikePD's remote-burst offset (a uniform random
        // angle at a FIXED distance, not old's Random.insideUnitCircle.normalized equivalent, ported to
        // GameRandom for determinism).
        float   angle  = GameRandom.instance.NextFloat(0f, 360f) * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * leapOffsetRadius;
        state.leapTarget = _target.position + offset;

        SnapFaceTarget(ref state);
        state.stateTimer = windupDuration;
        SetLeapState(ref state, LeapState.Windup);
    }

    private void EnterLeap(ref State state)
    {
        state.leapStart  = transform.position;
        state.stateTimer = leapDuration;
        SetLeapState(ref state, LeapState.Leaping);
    }

    private void EnterLanding(ref State state)
    {
        // Hard snap, correcting any residual lerp imprecision — matches old Land()'s direct position
        // assignment rather than trusting the final lerp step to land exactly on target.
        transform.position = new Vector3(state.leapTarget.x, transform.position.y, state.leapTarget.z);
        // No velocity to zero here — the rigidbody is still kinematic at this exact point (it doesn't flip
        // back to non-kinematic until the shared post-switch block runs later this same tick, so setting
        // velocity now would throw the same "not supported on a kinematic body" error), and a
        // MovePosition-driven kinematic leap doesn't leave any residual velocity behind to clear anyway.

        state.stateTimer = landingDuration;
        SetLeapState(ref state, LeapState.Landing);

        // One-shot AoE resolve on landing, matching KingSpikePD.FireSpikeBurst/DaggerGloomPD.EnterSpin — a
        // plain distance check against CharacterStatsPD.All rather than a physics trigger/collider.
        float   dmg    = damage * landDamageMultiplier;
        Vector3 origin = transform.position;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead || stats.IsInvincible) continue;
            if (Vector3.Distance(stats.transform.position, origin) <= landRadius)
                stats.TakeDamage(dmg);
        }
    }

    private void EnterPostLand(ref State state)
    {
        state.leapsRemaining--;
        if (state.leapsRemaining > 0)
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
        state.leapsRemaining = leapsPerAttack;
        SetLeapState(ref state, LeapState.Moving);
    }

    // ── View ───────────────────────────────────────────────────────────────
    private LeapState _lastVisualState = (LeapState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        // Death is handled by base.UpdateView() above (OnHideOnDeath() + hierarchy.Delete()) — but that
        // doesn't stop execution from continuing into the rest of THIS method afterward. If death happens
        // while stateId still reads Windup/Leaping (e.g. killed by AoE/DoT mid-leap, never transitioning
        // back to Moving first), the SetActive(...) call further down would immediately re-enable the very
        // indicator OnHideOnDeath() just hid. Since the indicator is unparented to world root during that
        // window, it'd then sit there forever, detached and visible, with nothing left to ever hide it
        // again once this enemy itself is deleted/pooled — this was the "leftover circle on the ground"
        // bug. Bailing here preserves whatever OnHideOnDeath() already did.
        if (viewState.b.isDead) return;

        // Sourced from verified, not viewState — leapTarget is a discrete "decided once" value (locked in
        // at EnterWindup), same category as KingSpikePD.chargeDirection, so it needs the exact
        // non-interpolated snapshot rather than viewState's default per-field linear blend. Re-applied
        // every frame the state is Windup/Leaping so the indicator always tracks whatever the current
        // leapTarget is, rather than snapshotting once on entry.
        var ss = (LeapState)viewState.stateId;
        if (_windupIndicator != null && verified.HasValue)
        {
            var vs = verified.Value;
            var vss = (LeapState)vs.stateId;
            if (vss == LeapState.Windup || vss == LeapState.Leaping)
                _windupIndicator.transform.position = new Vector3(vs.leapTarget.x, 0.05f, vs.leapTarget.z);
        }
        if (_windupIndicator != null) IndicatorMath.ApplyRadialTransform(_windupIndicator.transform, landRadius);

        // Purely cosmetic arc bob — safe off viewState (a transient misprediction here is an imperceptible
        // visual wobble, not a discrete value that needs to be exactly right).
        if (_model != null)
        {
            if (ss == LeapState.Leaping)
            {
                float t = 1f - Mathf.Clamp01(viewState.stateTimer / leapDuration);
                float arcY = Mathf.Sin(t * Mathf.PI) * leapArcHeight;
                _model.localPosition = _modelRestPosition + new Vector3(0f, arcY, 0f);
            }
            else
            {
                _model.localPosition = _modelRestPosition;
            }
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
        if (_windupFill != null)
        {
            // Represents the FULL time until landing (windup + leap), not just the windup phase — matches
            // old Leaper's windupFill.StartFill(windupDuration + leapDuration).
            if (ss == LeapState.Windup) _windupFill.StartFill(windupDuration + leapDuration);
            else                         _windupFill.ResetFill();
        }

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

    protected override void OnHideOnDeath()
    {
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.ResetFill();
    }

    protected override void OnRespawnReset()
    {
        ApplyAnimatorClipSpeeds();
        currentState.stateId             = 0;
        currentState.leapsRemaining      = leapsPerAttack;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.stateTimer          = 0f;
        if (_model != null) _model.localPosition = _modelRestPosition;
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
    }
}
