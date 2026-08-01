using PurrNet.Packing;
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
        public void Dispose()
        {
            b.contactCooldowns.Dispose();
            b.killLaunchHitIds.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    private static readonly int IdleHash          = Animator.StringToHash("Idle");
    private static readonly int SpearWindupHash   = Animator.StringToHash("SpearWindup");
    private static readonly int SpearStabHash     = Animator.StringToHash("SpearStab");
    private static readonly int SpearPostStabHash = Animator.StringToHash("SpearPostStab");
    private static readonly int WindupSpeedHash   = Animator.StringToHash("WindupSpeed");
    private static readonly int StabSpeedHash     = Animator.StringToHash("StabSpeed");
    private static readonly int PostStabSpeedHash = Animator.StringToHash("PostStabSpeed");

    private float _postStabClipLength;
    // Baseline "clip length / baseline duration" ratios, cached instead of SetFloat'd directly — Spear's
    // stab now resolves as a one-shot check (see EnterStab's own comment), same as Dagger's spin/Shooter's
    // shot, so there's no more lingering-hitbox reason to keep StabSpeedHash pinned fixed — every one of
    // these needs to be re-derived live every frame (ratio * EffectiveAttackSpeed, see UpdateView) instead
    // of set once.
    private float _windupClipRate;
    private float _stabClipRate;

    [Header("Spear Settings")]
    public float stabDamageMultiplier = 1f;
    // Separate from preferredRange (which governs the approach/dash/stab behavioral thresholds) — the
    // single source of truth for reach; both the visual indicator and the actual stab hit-check derive from
    // it (see UpdateView() for the indicator, EnterStab() for the hit-check), so tuning this one field keeps
    // both in sync automatically.
    [SerializeField] private float stabRange          = 3f;
    // Full width of the forward-facing rectangular stab zone (EnterStab() checks stabWidth/2 either side of
    // this enemy's own facing) — replaces what used to be the _spearHitbox BoxCollider's own authored
    // size.x. Not derived from anything at runtime; hand-tune to match however wide the stab visually reads.
    [SerializeField] private float stabWidth          = 2f;
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

        currentState.spearAttackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.spearAttacksRemaining    = attacksPerAttack;
    }

    // Caches _windupClipRate/_stabClipRate/_postStabClipLength (re-derived live in UpdateView, scaled by
    // whatever EffectiveAttackSpeed currently is) so each clip's actual playback duration matches
    // windupDuration/stabDuration/betweenStabPause/postStabPause exactly. Deliberately NOT in LateAwake() —
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
                _windupClipRate = clip.length / windupDuration;
            else if (clip.name == "Spear Gloom Stab")
                _stabClipRate = clip.length / stabDuration;
            else if (clip.name == "Spear Gloom PostStab")
                _postStabClipLength = clip.length;
        }
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded) EnterWindup(ref state);

        // spearAttackCooldownTimer: untouched — pacing between whole attack sequences, not attack
        // execution speed. spearStateTimer: scaled live by EffectiveAttackSpeed every tick (not
        // pre-divided once at state entry) — this is what makes windup/stabbing-state/betweenStabs/
        // postStab all reactively speed up or slow down immediately if attack speed changes mid-state,
        // rather than being locked to whatever it was when the state started.
        state.spearAttackCooldownTimer -= delta;
        state.spearStateTimer          -= delta * EffectiveAttackSpeed;

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
                        // Ongoing attack starts here — snapshot the target's position now so the whole
                        // dash+windup+stab sequence resolves against it even if _target gets reassigned
                        // mid-attack (taunt/invisible). See CaptureAttackAnchor's own doc comment.
                        CaptureAttackAnchor(ref state);
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
                    // !IsCrowdControlled — a true Stun() or Slow() landing mid-sequence never interrupts the
                    // stab that just finished above, but stops the chain from continuing into another one,
                    // falling through to PostStab instead (see IsCrowdControlled's own doc comment).
                    if (state.spearAttacksRemaining > 0 && !IsCrowdControlled) EnterBetweenStabs(ref state);
                    else                                                      EnterPostStab(ref state);
                }
                break;

            case SpearState.BetweenStabs:
                // Outside the ongoing window (ongoing ends the moment Stabbing finishes) — safe to read
                // live _target here, since this is deciding what the NEXT attack (if any) should use.
                if (state.spearStateTimer <= betweenStabPause * 0.5f) FaceTargetSmooth(delta);
                if (state.spearStateTimer <= 0f)
                {
                    if (_target == null)
                    {
                        // No valid target left for a subsequent stab in this chain — end the chain here
                        // instead of winding up at nobody, matching every other multi-stage enemy.
                        EnterPostStab(ref state);
                    }
                    else
                    {
                        CaptureAttackAnchor(ref state);
                        float dist = Vector3.Distance(transform.position, _target.position);
                        if (dist > preferredRange / 2f) EnterDashToTarget(ref state);
                        else                             EnterWindup(ref state);
                    }
                }
                break;

            case SpearState.PostStab:
                if (state.spearStateTimer <= postStabPause * 0.5f) FaceTargetSmooth(delta);
                if (state.spearStateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ss = GetSpearState(ref state);

        // Two different "immovable" mechanisms depending on why: DashingToTarget is fast movement, so its
        // collider is disabled (Case 1 — non-interacting, so a fast dash doesn't fling other enemies/
        // players around). Windup and Stabbing are stationary telegraphed-attack windows where the stab's
        // indicator needs to stay trustworthy, so they go kinematic instead (Case 2 — immovable but still
        // physically present, can still push other things, just can't itself be knocked off-position by
        // an incoming hit mid-telegraph). Everything else (Moving, BetweenStabs, PostStab) is normal
        // physics, collider enabled, not kinematic.
        bool isWindupOrStab = ss == SpearState.Windup || ss == SpearState.Stabbing;
        // Dash is part of the committed attack sequence too, not just Windup/Stabbing — every other dashing
        // enemy (Dagger/Blitz/Shooter/KingSpike) already folds its own dash state out of isStunnable; Spear
        // was the one exception, which let a hit landed mid-dash apply knockback and visibly interrupt/
        // redirect the charge.
        isStunnable = !isWindupOrStab && ss != SpearState.DashingToTarget;
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
        SnapFaceAnchor(ref state);
        SetSpearState(ref state, SpearState.Windup);
    }

    private void EnterStab(ref State state)
    {
        state.spearStateTimer = stabDuration;
        state.spearAttacksRemaining--;
        SetSpearState(ref state, SpearState.Stabbing);

        // One-shot forward-rectangle AoE resolve, matching KingSpikePD.FireSpikeBurst/DaggerGloomPD.
        // EnterSpin/LeaperGloomPD.EnterLanding — a plain check against CharacterStatsPD.All rather than a
        // physics trigger collider. Spear used to be the one enemy in the roster still using a real
        // Collider+OnTriggerEnter for its attack; converted to match everyone else. Direction is derived
        // from the captured attackAnchorPosition, not transform.forward directly — same reasoning as
        // ShooterGloomPD.FireShots (deterministic off simulation-owned state, not whatever the body's
        // current rotation happens to be), even though in practice they resolve to the same thing here
        // since SnapFaceAnchor() already faced the body this way back at Windup entry.
        Vector3 origin  = transform.position;
        Vector3 forward = state.b.attackAnchorPosition - origin;
        forward.y = 0f;
        if (forward == Vector3.zero) return;
        forward.Normalize();

        float dmg       = damage * stabDamageMultiplier;
        // Scaled by size (EnemyBasePD's generic per-enemy scalar) — stabWidth is tuned against the base
        // model's own footprint, and elite/bigger variants scale their model up via size, so the stab zone
        // needs to widen right along with it or it'd visually clip through a bigger model's own sides.
        float halfWidth = stabWidth * 0.5f * size;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead || stats.IsInvincible) continue;

            Vector3 offset = stats.transform.position - origin;
            offset.y = 0f;
            float forwardDist = Vector3.Dot(offset, forward);
            if (forwardDist < 0f || forwardDist > stabRange) continue;

            float lateralDist = (offset - forward * forwardDist).magnitude;
            if (lateralDist > halfWidth) continue;

            stats.TakeDamage(dmg);
        }
    }

    private void EnterBetweenStabs(ref State state)
    {
        state.spearStateTimer = betweenStabPause;
        SetSpearState(ref state, SpearState.BetweenStabs);
    }

    private void EnterDashToTarget(ref State state)
    {
        SetSpearState(ref state, SpearState.DashingToTarget);
        // baseMoveSpeed * dashSpeedMultiplier is the unscaled base — HandleDash itself applies
        // EffectiveAttackSpeed live, every tick, so this dash speeds up/slows down in real time right
        // alongside every other attack-speed-scaled part of this attack, not just at dash start.
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
        // Attack routine's over — reprioritize onto whoever's closest now, rather than sitting on whoever
        // this routine happened to start against. See EnemyBasePD.FindClosestTarget's own doc comment.
        FindClosestTarget();
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        SetSpearState(ref state, SpearState.Moving);
    }

    // Every Spear phase is interruptible (no override of CanBeInterrupted needed) — this just resets the
    // state machine back to Moving, same terminal transition the attack chain already uses when it runs out
    // of spearAttacksRemaining naturally. See EnemyBasePD.Interrupt()'s own doc comment.
    protected override void OnInterrupted() => EnterMoving(ref currentState);

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
        // reach, so the visual indicator can never silently drift out of sync with it.
        if (_windupIndicator != null) IndicatorMath.ApplyLineTransform(_windupIndicator.transform, stabRange);

        var ss = (SpearState)viewState.spearStateId;

        // Live, every frame regardless of the _lastVisualState-gated block below — reacts immediately to
        // EffectiveAttackSpeed changing mid-state (a slow landing or expiring mid-windup/recovery), not
        // just whatever it was the one time the state was entered.
        if (_windupFill != null && ss == SpearState.Windup)
            _windupFill.SetProgress(1f - Mathf.Clamp01(viewState.spearStateTimer / windupDuration));
        if (_animator != null)
        {
            if (ss == SpearState.Windup)
                _animator.SetFloat(WindupSpeedHash, _windupClipRate * EffectiveAttackSpeed);
            else if (ss == SpearState.Stabbing)
                _animator.SetFloat(StabSpeedHash, _stabClipRate * EffectiveAttackSpeed);
            else if (ss == SpearState.BetweenStabs)
                _animator.SetFloat(PostStabSpeedHash, (_postStabClipLength / betweenStabPause) * EffectiveAttackSpeed);
            else if (ss == SpearState.PostStab)
                _animator.SetFloat(PostStabSpeedHash, (_postStabClipLength / postStabPause) * EffectiveAttackSpeed);
        }

        if (ss == _lastVisualState) return;
        _lastVisualState = ss;

        if (_windupIndicator != null) _windupIndicator.SetActive(ss == SpearState.Windup);
        if (_windupFill != null && ss != SpearState.Windup) _windupFill.ResetFill();

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
                _animator.Play(SpearPostStabHash, 0, 0f);
                break;
            case SpearState.PostStab:
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
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.ResetFill();
        // In case death happened mid-Windup/Stabbing, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (spearStateId resets to Moving above), but resetting
        // explicitly here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (SpearState)(-1);
    }
}
