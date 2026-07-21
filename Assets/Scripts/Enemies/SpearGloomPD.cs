using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class SpearGloomPD : EnemyBasePD<SpearGloomPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int spearStateId;
        public float spearStateTimer;
        public float spearAttackCooldownTimer;
        public int spearAttacksRemaining;
        public DisposableList<PlayerID> hitTargetsThisStab;
        public void Dispose()
        {
            hitTargetsThisStab.Dispose();
            b.contactCooldowns.Dispose();
            b.killLaunchHitIds.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.hitTargetsThisStab = hitTargetsThisStab.Duplicate();
            return copy;
        }
    }

    protected override State GetInitialState()
    {
        var s = base.GetInitialState();
        s.hitTargetsThisStab = DisposableList<PlayerID>.Create(4);
        return s;
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;
    protected override bool IsReadyToAttack
    {
        get
        {
            var ss = (SpearState)currentState.spearStateId;
            return ss != SpearState.Moving && ss != SpearState.PostStab;
        }
    }

    private static readonly int IdleHash          = Animator.StringToHash("Idle");
    private static readonly int SpearWindupHash   = Animator.StringToHash("SpearWindup");
    private static readonly int SpearStabHash     = Animator.StringToHash("SpearStab");
    private static readonly int SpearPostStabHash = Animator.StringToHash("SpearPostStab");
    private static readonly int WindupSpeedHash   = Animator.StringToHash("WindupSpeed");
    private static readonly int StabSpeedHash     = Animator.StringToHash("StabSpeed");
    private static readonly int PostStabSpeedHash = Animator.StringToHash("PostStabSpeed");

    private float _postStabClipLength;

    [Header("Spear Settings")]
    [SerializeField] private BoxCollider _spearHitbox;
    public float stabDamageMultiplier = 1f;
    // Separate from preferredRange (which governs the approach/dash/stab behavioral thresholds) — this is
    // the single source of truth for reach; both the visual indicator and the actual hitbox derive from
    // it every frame (see UpdateView()), so tuning this one field keeps everything in sync automatically.
    [SerializeField] private float stabRange          = 3f;
    [SerializeField] private float windupDuration     = 0.5f;
    [SerializeField] private float stabDuration       = 0.1f;
    [SerializeField] private float betweenStabPause   = 0.3f;
    [SerializeField] private float postStabPause      = 0.6f;
    [SerializeField] private float attackCooldown     = 3f;
    [SerializeField] private int   attacksPerAttack   = 1;
    [SerializeField] private float dashSpeedMultiplier = 1.5f;
    [SerializeField] private GameObject _windupIndicator;
    [SerializeField] private WindupFillPD _windupFill;

    public enum SpearState { Moving, Windup, Stabbing, BetweenStabs, PostStab, DashingToTarget }

    private SpearState GetSpearState(ref State s) => (SpearState)s.spearStateId;
    private void SetSpearState(ref State s, SpearState st) => s.spearStateId = (int)st;

    protected override void LateAwake()
    {
        base.LateAwake();

        if (_spearHitbox != null && _spearHitbox.TryGetComponent(out PredictedPhysicsCallbacks cb))
            cb.onTriggerEnter += OnSpearHit;

        currentState.spearAttackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.spearAttacksRemaining    = attacksPerAttack;
    }

    // Sets WindupSpeed/StabSpeed (and caches _postStabClipLength, re-derived fresh from PostStabPause/
    // BetweenStabPause at each use in UpdateView so it doesn't need re-setting here) so each clip's actual
    // playback duration matches windupDuration/stabDuration exactly. Deliberately NOT in LateAwake() —
    // EnemyBasePD.Simulate()'s first-tick init block calls _animator.Rebind() (to force a clean pose after
    // pooled reuse), which resets ALL Animator Controller parameters back to their defaults, wiping out
    // whatever LateAwake() computed. OnRespawnReset() runs right after that Rebind() call (on every reuse,
    // including the very first spawn), so re-applying here is what actually sticks — WindupSpeed/StabSpeed
    // were silently broken (playing at each clip's raw 1x speed) until now.
    private void ApplyAnimatorClipSpeeds()
    {
        if (_animator == null) return;
        foreach (var clip in _animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == "Spear Gloom Windup")
                _animator.SetFloat(WindupSpeedHash, clip.length / windupDuration);
            else if (clip.name == "Spear Gloom Stab")
                _animator.SetFloat(StabSpeedHash, clip.length / stabDuration);
            else if (clip.name == "Spear Gloom PostStab")
                _postStabClipLength = clip.length;
        }
    }

    private void OnSpearHit(GameObject other)
    {
        if (B.isDead) return;
        var stats = other.GetComponent<CharacterStatsPD>();
        if (stats == null || !stats.owner.HasValue) return;

        // Trigger events dispatch twice under normal operation — once live, once again if this tick
        // later replays as a verified/reconciliation pass — so this needs its own dedup, scoped to
        // one stab, per target (not a single flag — a stab can still legitimately hit multiple
        // different players at once, just not the same player twice).
        var target = stats.owner.Value;
        if (currentState.hitTargetsThisStab.Contains(target)) return;

        currentState.hitTargetsThisStab.Add(target);
        stats.TakeDamage(damage * stabDamageMultiplier);
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded) EnterWindup(ref state);

        state.spearAttackCooldownTimer -= delta;
        state.spearStateTimer          -= delta;

        var ss = GetSpearState(ref state);

        switch (ss)
        {
            case SpearState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.spearAttackCooldownTimer <= 0f && _target != null)
                {
                    float dist = Vector3.Distance(transform.position, _target.position);
                    if (dist <= preferredRange * 2f)
                    {
                        if (dist > preferredRange / 2f) EnterDashToTarget(ref state);
                        else                             EnterWindup(ref state);
                    }
                }
                break;

            case SpearState.Windup:
                if (state.spearStateTimer <= 0f) EnterStab(ref state);
                break;

            case SpearState.Stabbing:
                if (state.spearStateTimer <= 0f)
                {
                    if (state.spearAttacksRemaining > 0) EnterBetweenStabs(ref state);
                    else                                  EnterPostStab(ref state);
                }
                break;

            case SpearState.BetweenStabs:
                if (state.spearStateTimer <= betweenStabPause * 0.5f) FaceTargetSmooth(delta);
                if (state.spearStateTimer <= 0f)
                {
                    float dist = _target != null ? Vector3.Distance(transform.position, _target.position) : 0f;
                    if (_target != null && dist > preferredRange / 2f) EnterDashToTarget(ref state);
                    else                                                EnterWindup(ref state);
                }
                break;

            case SpearState.PostStab:
                if (state.spearStateTimer <= postStabPause * 0.5f) FaceTargetSmooth(delta);
                if (state.spearStateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ss = GetSpearState(ref state);

        if (_spearHitbox != null)
            _spearHitbox.enabled = ss == SpearState.Stabbing;

        // Two different "immovable" mechanisms depending on why: DashingToTarget is fast movement, so its
        // collider is disabled (Case 1 — non-interacting, so a fast dash doesn't fling other enemies/
        // players around). Windup and Stabbing are stationary telegraphed-attack windows where the stab's
        // indicator needs to stay trustworthy, so they go kinematic instead (Case 2 — immovable but still
        // physically present, can still push other things, just can't itself be knocked off-position by
        // an incoming hit mid-telegraph). Everything else (Moving, BetweenStabs, PostStab) is normal
        // physics, collider enabled, not kinematic.
        bool isWindupOrStab = ss == SpearState.Windup || ss == SpearState.Stabbing;
        isStunnable = !isWindupOrStab;
        if (_rigidbody != null)
        {
            // Zero velocity on the entering edge only (not every tick while already kinematic) — setting
            // velocity on an already-kinematic PredictedRigidbody throws ("not supported"). Whatever
            // residual velocity this rigidbody was carrying the instant it goes kinematic needs clearing
            // so it doesn't sit there stale (harmless in practice since kinematic ignores it, but leaves
            // a nonzero value another system could read/act on).
            if (isWindupOrStab && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = isWindupOrStab;
        }

        if (_physicsCollider != null)
            _physicsCollider.enabled = ss != SpearState.DashingToTarget;
    }

    // ── State transitions ──────────────────────────────────────────────────
    private void EnterWindup(ref State state)
    {
        state.spearStateTimer = windupDuration;
        // Pinned for the whole attack sequence — see EnterMoving for the matching clear. Entering Windup
        // directly from Moving (rather than via EnterDashToTarget) is one of the two commit points, so the
        // lock has to be set here too, not just in EnterDashToTarget.
        LockTarget(ref state);
        SnapFaceTarget(ref state);
        SetSpearState(ref state, SpearState.Windup);
    }

    private void EnterStab(ref State state)
    {
        state.spearStateTimer = stabDuration;
        state.spearAttacksRemaining--;
        state.hitTargetsThisStab.Clear();
        SetSpearState(ref state, SpearState.Stabbing);
    }

    private void EnterBetweenStabs(ref State state)
    {
        state.spearStateTimer = betweenStabPause;
        SetSpearState(ref state, SpearState.BetweenStabs);
    }

    private void EnterDashToTarget(ref State state)
    {
        // Pinned for the whole attack sequence (dash -> windup -> stab(s) -> pause) — see EnterMoving for
        // the matching clear. Locking here (the moment the dash direction is committed) rather than only in
        // EnterWindup is what keeps the dash itself, and everything after it, aimed at the same player.
        LockTarget(ref state);
        SetSpearState(ref state, SpearState.DashingToTarget);
        StartDashToTarget(ref state, baseMoveSpeed * dashSpeedMultiplier, preferredRange / 2f);
    }

    private void EnterPostStab(ref State state)
    {
        state.spearStateTimer          = postStabPause;
        state.spearAttackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        SetSpearState(ref state, SpearState.PostStab);
    }

    private void EnterMoving(ref State state)
    {
        state.spearAttacksRemaining = attacksPerAttack;
        ClearTargetLock(ref state);
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        SetSpearState(ref state, SpearState.Moving);
    }

    // ── View ───────────────────────────────────────────────────────────────
    private SpearState _lastVisualState = (SpearState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        // Same reasoning as EnemyBasePD's initialized guard: on the client this can run while the
        // pooled object is transiently inactive during replay. Bail before recording _lastVisualState
        // so this same transition gets retried once actually active, instead of being silently
        // skipped forever (which left stale animator state from the object's previous pooled life).
        if (!gameObject.activeInHierarchy) return;

        // Re-applied every frame, not just once at spawn — stabRange is the single source of truth for
        // reach; both the visual indicator and the actual hitbox derive from it here, so neither can
        // silently drift out of sync with it (or with each other).
        if (_windupIndicator != null) IndicatorMath.ApplyLineTransform(_windupIndicator.transform, stabRange);
        IndicatorMath.ApplyLineCollider(_spearHitbox, stabRange);

        var ss = (SpearState)viewState.spearStateId;
        if (ss == _lastVisualState) return;
        _lastVisualState = ss;

        if (_windupIndicator != null) _windupIndicator.SetActive(ss == SpearState.Windup);
        if (_windupFill != null)
        {
            if (ss == SpearState.Windup) _windupFill.StartFill(windupDuration);
            else                         _windupFill.ResetFill();
        }

        if (_animator == null) return;
        switch (ss)
        {
            case SpearState.Moving:
                _animator.Play(IdleHash, 0, 0f);
                break;
            case SpearState.Windup:
                _animator.Play(SpearWindupHash, 0, 0f);
                break;
            case SpearState.Stabbing:
                _animator.Play(SpearStabHash, 0, 0f);
                break;
            case SpearState.BetweenStabs:
                _animator.SetFloat(PostStabSpeedHash, _postStabClipLength / betweenStabPause);
                _animator.Play(SpearPostStabHash, 0, 0f);
                break;
            case SpearState.PostStab:
                _animator.SetFloat(PostStabSpeedHash, _postStabClipLength / postStabPause);
                _animator.Play(SpearPostStabHash, 0, 0f);
                break;
        }
    }

    protected override void OnDeathStop()
    {
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null) _windupFill.ResetFill();
        // Snaps out of whatever attack pose (Windup/Stabbing/etc.) this enemy died mid-animation in — ss
        // is frozen from here on (Simulate() stops updating spearStateId once dead), so without this the
        // corpse would otherwise hold/loop that pose for the entire kill-launch instead of reading as dead.
        // Guarded on activeInHierarchy (unlike the indicator-hide above, which must always run — see
        // EnemyBasePD.UpdateView's own comment) since Animator.Play() logs a warning when called on an
        // inactive GameObject; on the client this can be transiently true if this tick is being replayed
        // before a pooled instance has fully reactivated. Harmless to skip here — a hidden/dead corpse's
        // pose no longer matters, and a genuinely fresh reuse gets its own animator reset via
        // OnRespawnReset()'s Rebind() regardless.
        if (_animator != null && gameObject.activeInHierarchy) _animator.Play(IdleHash, 0, 0f);
    }

    protected override void OnRespawnReset()
    {
        ApplyAnimatorClipSpeeds();
        currentState.spearStateId             = 0;
        currentState.spearAttacksRemaining    = attacksPerAttack;
        currentState.spearAttackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.spearStateTimer          = 0f;
        if (_spearHitbox != null)    _spearHitbox.enabled    = false;
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.ResetFill();
        // In case death happened mid-Windup/Stabbing, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (spearStateId resets to Moving above), but resetting
        // explicitly here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (SpearState)(-1);
    }
}
