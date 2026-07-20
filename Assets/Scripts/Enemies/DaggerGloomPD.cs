using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Enemies/DaggerGloom.cs — basically SpearGloomPD's shape, but the dash
// target-repositions BEHIND the target instead of stopping short, and the attack is a radial AoE spin
// instead of a directional stab. Old system's spinHitbox (a trigger Collider, enabled only during
// Spinning) is replaced with a one-shot distance check against CharacterStatsPD.All, matching
// KingSpikePD's Spike Burst — cheaper than physics here since it's checking against a small, bounded,
// already-in-memory set (players) rather than an unbounded set of enemies. Cooldown randomization matches
// SpearGloomPD's current behavior (full 1x-2x range, both at spawn and after every attack) rather than old
// Dagger's own smaller 20% post-attack reroll, which was the exact pattern already rejected for BlitzGloomPD.
public class DaggerGloomPD : EnemyBasePD<DaggerGloomPD.State>
{
    public struct State : IPredictedData<State>
    {
        public EnemyBaseState b;
        public int   stateId;              // cast to/from DaggerState, same convention as SpearGloomPD
        public float stateTimer;
        public float attackCooldownTimer;
        public int   attacksRemaining;
        public void Dispose() { b.contactCooldowns.Dispose(); }
    }

    public enum DaggerState { Moving, Windup, Spinning, BetweenSpins, PostSpin, DashingToTarget }

    private DaggerState GetDaggerState(ref State s) => (DaggerState)s.stateId;
    private void SetDaggerState(ref State s, DaggerState st) => s.stateId = (int)st;

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    private static readonly int IdleHash           = Animator.StringToHash("Idle");
    private static readonly int DaggerWindupHash   = Animator.StringToHash("DaggerWindup");
    private static readonly int DaggerSpinHash     = Animator.StringToHash("DaggerSpin");
    private static readonly int DaggerPostSpinHash = Animator.StringToHash("DaggerPostSpin");
    private static readonly int WindupSpeedHash    = Animator.StringToHash("WindupSpeed");
    private static readonly int SpinSpeedHash      = Animator.StringToHash("SpinSpeed");
    private static readonly int PostSpinSpeedHash  = Animator.StringToHash("PostSpinSpeed");

    private float _postSpinClipLength;

    [Header("Dagger Settings")]
    public float spinDamageMultiplier = 1f;
    [SerializeField] private float spinRadius         = 3f;
    [SerializeField] private float windupDuration     = 0.5f;
    [SerializeField] private float spinDuration       = 0.1f;
    [SerializeField] private float betweenSpinPause   = 0.3f;
    [SerializeField] private float postSpinPause      = 0.6f;
    [SerializeField] private float attackCooldown     = 3f;
    [SerializeField] private int   attacksPerAttack   = 1;
    [SerializeField] private float dashSpeedMultiplier = 15f;
    [SerializeField] private GameObject   _windupIndicator;
    [SerializeField] private WindupFillPD _windupFill;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void LateAwake()
    {
        base.LateAwake();

        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.attacksRemaining    = attacksPerAttack;
    }

    // Sets WindupSpeed/SpinSpeed (and caches _postSpinClipLength, re-derived fresh from postSpinPause/
    // betweenSpinPause at each use in UpdateView so it doesn't need re-setting here) so each clip's actual
    // playback duration matches windupDuration/spinDuration exactly. Deliberately NOT in LateAwake() —
    // EnemyBasePD.Simulate()'s first-tick init block calls _animator.Rebind() (to force a clean pose after
    // pooled reuse), which resets ALL Animator Controller parameters back to their defaults, wiping out
    // whatever LateAwake() computed. OnRespawnReset() runs right after that Rebind() call (on every reuse,
    // including the very first spawn), so re-applying here is what actually sticks — WindupSpeed/SpinSpeed
    // were silently broken (playing at each clip's raw 1x speed) until now.
    private void ApplyAnimatorClipSpeeds()
    {
        if (_animator == null) return;
        foreach (var clip in _animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == "Dagger Gloom Windup")
                _animator.SetFloat(WindupSpeedHash, clip.length / windupDuration);
            else if (clip.name == "Dagger Gloom Spin")
                _animator.SetFloat(SpinSpeedHash, clip.length / spinDuration);
            else if (clip.name == "Dagger Gloom PostSpin")
                _postSpinClipLength = clip.length;
        }
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded) EnterWindup(ref state);

        state.attackCooldownTimer -= delta;
        state.stateTimer          -= delta;

        var ss = GetDaggerState(ref state);

        switch (ss)
        {
            case DaggerState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && _target != null)
                {
                    float dist = Vector3.Distance(transform.position, _target.position);
                    if (dist <= preferredRange * 2f)
                    {
                        if (dist > preferredRange / 2f) EnterDashToTarget(ref state);
                        else                             EnterWindup(ref state);
                    }
                }
                break;

            case DaggerState.Windup:
                if (state.stateTimer <= 0f) EnterSpin(ref state);
                break;

            case DaggerState.Spinning:
                if (state.stateTimer <= 0f)
                {
                    if (state.attacksRemaining > 0) EnterBetweenSpins(ref state);
                    else                             EnterPostSpin(ref state);
                }
                break;

            case DaggerState.BetweenSpins:
                if (state.stateTimer <= betweenSpinPause * 0.5f) FaceTargetSmooth(delta);
                if (state.stateTimer <= 0f)
                {
                    float dist = _target != null ? Vector3.Distance(transform.position, _target.position) : 0f;
                    if (_target != null && dist > preferredRange / 2f) EnterDashToTarget(ref state);
                    else                                                EnterWindup(ref state);
                }
                break;

            case DaggerState.PostSpin:
                if (state.stateTimer <= postSpinPause * 0.5f) FaceTargetSmooth(delta);
                if (state.stateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ss = GetDaggerState(ref state);

        // Same "block stun/knockback for the whole attack sequence" reasoning as SpearGloomPD — only
        // Moving is interruptible.
        isStunnable = ss == DaggerState.Moving ? !stunResistant : false;

        // Windup and Spinning are both stationary — Windup is the telegraph, Spinning is where the AoE
        // resolves instantly (see EnterSpin()) but the animation still needs to play out untouched, so
        // both go kinematic (Case 2). DashingToTarget is fast movement, handled by the base dash
        // primitives (collider-disabled instead, Case 1).
        bool isWindupOrSpin = ss == DaggerState.Windup || ss == DaggerState.Spinning;
        if (_rigidbody != null)
        {
            if (isWindupOrSpin && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = isWindupOrSpin;
        }

        if (_physicsCollider != null)
            _physicsCollider.enabled = ss != DaggerState.DashingToTarget;
    }

    // ── State transitions ──────────────────────────────────────────────────

    private void EnterWindup(ref State state)
    {
        state.stateTimer = windupDuration;
        // Pinned for the whole attack sequence — see EnterMoving for the matching clear. Entering Windup
        // directly from Moving (rather than via EnterDashToTarget) is one of the two commit points, so the
        // lock has to be set here too, not just in EnterDashToTarget.
        LockTarget(ref state);
        SnapFaceTarget(ref state);
        SetDaggerState(ref state, DaggerState.Windup);
    }

    private void EnterSpin(ref State state)
    {
        state.stateTimer = spinDuration;
        state.attacksRemaining--;
        SetDaggerState(ref state, DaggerState.Spinning);

        // One-shot AoE resolve, matching KingSpikePD.FireSpikeBurst — a plain distance check against
        // CharacterStatsPD.All rather than a physics trigger/collider, since this is checking against a
        // small, bounded, already-in-memory set (players), not enemies.
        float   dmg    = damage * spinDamageMultiplier;
        Vector3 origin = transform.position;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead || stats.IsInvincible) continue;
            if (Vector3.Distance(stats.transform.position, origin) <= spinRadius)
                stats.TakeDamage(dmg);
        }
    }

    private void EnterBetweenSpins(ref State state)
    {
        state.stateTimer = betweenSpinPause;
        SetDaggerState(ref state, DaggerState.BetweenSpins);
    }

    // Dashes to a point BEHIND the target (through/past them from the current approach angle), not
    // stopping short like SpearGloomPD's own dash — matches old DaggerGloom.DashBehindTarget, just
    // computed once at dash-start (like every other dash in this rebuild) rather than re-tracked every
    // frame against a possibly-still-moving target.
    private void EnterDashToTarget(ref State state)
    {
        if (_target == null) return;
        // Pinned for the whole attack sequence (dash -> windup -> spin(s) -> pause) — see EnterMoving for
        // the matching clear. Locking here (the moment the dash destination is committed) rather than only
        // in EnterWindup is what keeps the dash itself, and everything after it, aimed at the same player.
        LockTarget(ref state);
        Vector3 dir = _target.position - transform.position;
        dir.y = 0f;
        dir.Normalize();
        Vector3 behindPoint = _target.position + dir * (preferredRange / 4f);
        SetDaggerState(ref state, DaggerState.DashingToTarget);
        StartDashToPoint(ref state, baseMoveSpeed * dashSpeedMultiplier, behindPoint);
    }

    private void EnterPostSpin(ref State state)
    {
        state.stateTimer          = postSpinPause;
        state.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        SetDaggerState(ref state, DaggerState.PostSpin);
    }

    private void EnterMoving(ref State state)
    {
        state.attacksRemaining = attacksPerAttack;
        ClearTargetLock(ref state);
        SetDaggerState(ref state, DaggerState.Moving);
    }

    // ── View ───────────────────────────────────────────────────────────────
    private DaggerState _lastVisualState = (DaggerState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        // Re-applied every frame — same reasoning as every other indicator in this rebuild: if spinRadius
        // changes at runtime, the indicator should never silently drift out of sync with it. Radial
        // (cylinder), not line — this is an AoE, not a directional reach.
        if (_windupIndicator != null) IndicatorMath.ApplyRadialTransform(_windupIndicator.transform, spinRadius);

        var ss = (DaggerState)viewState.stateId;
        if (ss == _lastVisualState) return;
        _lastVisualState = ss;

        if (_windupIndicator != null) _windupIndicator.SetActive(ss == DaggerState.Windup);
        if (_windupFill != null)
        {
            if (ss == DaggerState.Windup) _windupFill.StartFill(windupDuration);
            else                          _windupFill.ResetFill();
        }

        if (_animator == null) return;
        switch (ss)
        {
            case DaggerState.Moving:
                _animator.Play(IdleHash, 0, 0f);
                break;
            case DaggerState.Windup:
                _animator.Play(DaggerWindupHash, 0, 0f);
                break;
            case DaggerState.Spinning:
                _animator.Play(DaggerSpinHash, 0, 0f);
                break;
            case DaggerState.BetweenSpins:
                _animator.SetFloat(PostSpinSpeedHash, _postSpinClipLength / betweenSpinPause);
                _animator.Play(DaggerPostSpinHash, 0, 0f);
                break;
            case DaggerState.PostSpin:
                _animator.SetFloat(PostSpinSpeedHash, _postSpinClipLength / postSpinPause);
                _animator.Play(DaggerPostSpinHash, 0, 0f);
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
        currentState.attacksRemaining    = attacksPerAttack;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.stateTimer          = 0f;
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.ResetFill();
        // In case death happened mid-Windup/Spinning, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (stateId resets to Moving above), but resetting explicitly
        // here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (DaggerState)(-1);
    }
}
