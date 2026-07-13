using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class SpearGloomPD : EnemyBasePD<SpearGloomPD.State>
{
    public struct State : IPredictedData<State>
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
    [SerializeField] private Collider _spearHitbox;
    public float stabDamageMultiplier = 1f;
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

        if (_animator != null)
        {
            foreach (var clip in _animator.runtimeAnimatorController.animationClips)
            {
                if (clip.name == "Spear Gloom Windup")
                    _animator.SetFloat(WindupSpeedHash, clip.length / windupDuration);
                else if (clip.name == "Spear Gloom Stab")
                    _animator.SetFloat(StabSpeedHash, clip.length / stabDuration);
                else if (clip.name == "Spear Gloom PostStab")
                {
                    _postStabClipLength = clip.length;
                    _animator.SetFloat(PostStabSpeedHash, clip.length / postStabPause);
                }
            }
        }

        if (_spearHitbox != null && _spearHitbox.TryGetComponent(out PredictedPhysicsCallbacks cb))
            cb.onTriggerEnter += OnSpearHit;

        currentState.spearAttackCooldownTimer = attackCooldown;
        currentState.spearAttacksRemaining    = attacksPerAttack;
    }

    private void OnSpearHit(GameObject other)
    {
        if (B.isDead) return;
        var stats = other.GetComponentInParent<CharacterStatsPD>();
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

        if (_physicsCollider != null)
            _physicsCollider.enabled = ss == SpearState.Moving || ss == SpearState.PostStab;
    }

    // ── State transitions ──────────────────────────────────────────────────
    private void EnterWindup(ref State state)
    {
        state.spearStateTimer = windupDuration;
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
        SetSpearState(ref state, SpearState.DashingToTarget);
        StartDashToTarget(ref state, baseMoveSpeed * dashSpeedMultiplier, preferredRange / 2f);
    }

    private void EnterPostStab(ref State state)
    {
        state.spearStateTimer          = postStabPause;
        state.spearAttackCooldownTimer = attackCooldown;
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        SetSpearState(ref state, SpearState.PostStab);
    }

    private void EnterMoving(ref State state)
    {
        state.spearAttacksRemaining = attacksPerAttack;
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

    protected override void OnHideOnDeath()
    {
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null) _windupFill.ResetFill();
    }

    protected override void OnRespawnReset()
    {
        currentState.spearStateId             = 0;
        currentState.spearAttacksRemaining    = attacksPerAttack;
        currentState.spearAttackCooldownTimer = attackCooldown;
        currentState.spearStateTimer          = 0f;
        if (_spearHitbox != null)    _spearHitbox.enabled    = false;
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.ResetFill();
        _lastVisualState = (SpearState)(-1);
    }
}
