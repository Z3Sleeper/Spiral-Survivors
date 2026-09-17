using PurrNet.Packing;
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
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   stateId;              // cast to/from DaggerState, same convention as SpearGloomPD
        public float stateTimer;
        public float attackCooldownTimer;
        public int   attacksRemaining;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
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
    // Baseline "clip length / baseline duration" ratios, cached instead of SetFloat'd directly — every one
    // of Dagger's own attack beats scales with attack speed (unlike SpearGloomPD's Stab, which stays fixed
    // because it gates a lingering hitbox; Dagger's Spin damage is a one-shot check that already resolved
    // the instant EnterSpin() ran, so there's no window to protect — see SpearGloomPD's own doc comment for
    // the full reasoning), so both need to be re-derived live every frame (ratio * EffectiveAttackSpeed,
    // see UpdateView) instead of set once at spawn.
    private float _windupClipRate;
    private float _spinClipRate;

    [Header("Dagger Settings")]
    public float spinDamageMultiplier = 1f;
    [SerializeField] private float spinRadius         = 3f;
    [SerializeField] private float windupDuration     = 0.75f;
    [SerializeField] private float spinDuration       = 0.1f;
    [SerializeField] private float betweenSpinPause   = 0.25f;
    [SerializeField] private float postSpinPause      = 0.5f;
    // Tier1-authored baseline, scaled per tier by attacksPerAttackTierGrowth below (applied in
    // OnConfigureSpawn()). See _authoredAttacksPerAttack's own doc comment for why this needs the
    // snapshot-and-reset treatment instead of just incrementing this field directly.
    [SerializeField] private int   attacksPerAttack   = 1;
    [SerializeField] private float dashSpeedMultiplier = 4f;
    // Flat + size-scaled (2026-09-03, was preferredRange/4f) — decoupled from preferredRange, matching
    // KingSpikePD.postChargeDashOffsetDistance's own flat-variable convention instead of deriving from the
    // shared approach/retreat/attack-range field. See EnterDashToTarget's own call site.
    [SerializeField] private float dashOffsetDistance = 2f;
    // Captured once in OnAwake(), reset back onto attacksPerAttack every pooled reuse in LateAwake() —
    // otherwise OnConfigureSpawn's += would keep compounding across reuses, same "_authoredX snapshot"
    // pattern EnemyBasePD's own maxHealth/damage/moveSpeed/xpValue/enemyName already use (and BlitzGloomPD's
    // own _authoredBlitzesPerAttack already established for this exact identical case).
    private int _authoredAttacksPerAttack;

    [Header("Attack Cooldown")]
    [SerializeField] private float attackCooldown = 5f;
    // Captured once in OnAwake(), reset back onto attackCooldown every pooled reuse in LateAwake(), scaled
    // by attackCooldownMultiplier (Threat Level 5) in OnConfigureSpawn() — same "_authoredX snapshot"
    // pattern as _authoredAttacksPerAttack above.
    private float _authoredAttackCooldown;

    [Header("Tier Scaling")]
    // Added onto attacksPerAttack per tier above 1 — see OnConfigureSpawn().
    [SerializeField] private int attacksPerAttackTierGrowth = 1;

    [Header("Indicators")]
    [SerializeField] private GameObject   _windupIndicator;
    [SerializeField] private WindupFillPD _windupFill;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnAwake()
    {
        base.OnAwake();
        _authoredAttacksPerAttack = attacksPerAttack;
        _authoredAttackCooldown = attackCooldown;
    }

    protected override void LateAwake()
    {
        base.LateAwake();

        // Reset to the tier1-authored baseline before OnConfigureSpawn (which always runs AFTER LateAwake(),
        // once hierarchy.Create() returns) scales it fresh for this life — see _authoredAttacksPerAttack's
        // own doc comment.
        attacksPerAttack = _authoredAttacksPerAttack;
        attackCooldown = _authoredAttackCooldown;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        // Re-set again in OnRespawnReset() once attacksPerAttack is actually tier-scaled (that always runs
        // AFTER OnConfigureSpawn, on this life's first Simulate() tick — see its own comment for the
        // BlitzGloomPD precedent) — this LateAwake() assignment is only ever a placeholder until then.
        currentState.attacksRemaining    = attacksPerAttack;
    }

    // Tier1-authored attacksPerAttack, scaled by attacksPerAttackTierGrowth per tier above 1 (see that
    // field's own doc comment). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call).
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        attacksPerAttack += (spawnTier - 1) * attacksPerAttackTierGrowth;

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        attackCooldown = _authoredAttackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
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
                _windupClipRate = clip.length / windupDuration;
            else if (clip.name == "Dagger Gloom Spin")
                _spinClipRate = clip.length / spinDuration;
            else if (clip.name == "Dagger Gloom PostSpin")
                _postSpinClipLength = clip.length;
        }
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded)
        {
            // Re-captures the anchor to wherever the dash's own tracked position actually ended up, right at
            // the moment the dash ends — same fix as SpearGloomPD's own identical bug (2026-08-27, upgraded
            // 2026-08-31 to CaptureAttackAnchorFromOffsetChase — see its own doc comment): the dash itself
            // (HandleOffsetChase) tracks the target live the whole time it's running (freezing on death/
            // invisibility), but attackAnchorPosition was still whatever it was snapshotted to back at the
            // ORIGINAL CaptureAttackAnchor call (attack-start, before the dash even began), so Windup's own
            // SnapFaceAnchor was facing a stale pre-dash position. Unlike Spear this doesn't affect
            // EnterSpin's own damage (a self-centered radial AoE, not direction-dependent), which is why this
            // was never visibly noticeable here — but the facing itself was still wrong, so fixed for
            // consistency. Same target IDENTITY stays committed either way — this only refreshes the
            // POSITION.
            CaptureAttackAnchorFromOffsetChase(ref state);
            EnterWindup(ref state);
        }

        var ss = GetDaggerState(ref state);

        // attackCooldownTimer: only ticks while actually in Moving (2026-08-19 — uniform across every
        // multi-phase enemy's own attack-cadence cooldown now, see OrbiterGloomPD's own fix). stateTimer:
        // scaled live by EffectiveAttackSpeed every tick (not pre-divided once at state entry) — reacts
        // immediately if attack speed changes mid-state, same as SpearGloomPD's own stateTimer.
        if (ss == DaggerState.Moving)
            state.attackCooldownTimer -= delta;
        state.stateTimer -= delta * EffectiveAttackSpeed;

        switch (ss)
        {
            case DaggerState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && _target != null)
                {
                    float dist = Vector3.Distance(transform.position, _target.position);
                    if (dist <= preferredRange * 2f)
                    {
                        // Ongoing attack starts here — see CaptureAttackAnchor's own doc comment.
                        // Decrement/refill lives here (the actual start of the attempt — dashtotarget OR
                        // windup, whichever runs first — not EnterSpin's own strike-resolve point) so an
                        // attempt cancelled via Interrupt() mid-dash or mid-windup still counts against the
                        // budget. See EnterMoving's own comment for why the reset moved here too.
                        if (state.attacksRemaining <= 0) state.attacksRemaining = attacksPerAttack;
                        state.attacksRemaining--;
                        CaptureAttackAnchor(ref state);
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
                    // IsTargetStillValid — a target that went invisible-and-non-taunting since the last spin
                    // shouldn't get chained into another one against them; see that property's own doc comment.
                    // !IsCrowdControlled — a true Stun() landing mid-sequence never interrupts the spin that
                    // just finished above, but stops the chain from continuing into another one, falling
                    // through to PostSpin instead (a Slow does NOT stop the chain — see IsCrowdControlled's
                    // own doc comment, hard CC only).
                    if (state.attacksRemaining > 0 && IsTargetStillValid && !IsCrowdControlled) EnterBetweenSpins(ref state);
                    else                                                  EnterPostSpin(ref state);
                }
                break;

            case DaggerState.BetweenSpins:
                // Outside the ongoing window (ongoing ends the moment Spinning finishes) — safe to read
                // live _target here, since this is deciding what the NEXT attack (if any) should use.
                if (state.stateTimer <= betweenSpinPause * 0.5f) FaceTargetSmooth(delta);
                if (state.stateTimer <= 0f)
                {
                    if (_target == null)
                    {
                        EnterPostSpin(ref state);
                    }
                    else
                    {
                        // Same refill-if-exhausted + decrement as the Moving case above — this is the other
                        // funnel into a fresh attempt (continuing an existing chain rather than starting a
                        // brand new one), same reasoning applies.
                        if (state.attacksRemaining <= 0) state.attacksRemaining = attacksPerAttack;
                        state.attacksRemaining--;
                        CaptureAttackAnchor(ref state);
                        float dist = Vector3.Distance(transform.position, _target.position);
                        if (dist > preferredRange / 2f) EnterDashToTarget(ref state);
                        else                             EnterWindup(ref state);
                    }
                }
                break;

            case DaggerState.PostSpin:
                if (state.stateTimer <= postSpinPause * 0.5f) FaceTargetSmooth(delta);
                if (state.stateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ss = GetDaggerState(ref state);

        // Windup and Spinning are both stationary — Windup is the telegraph, Spinning is where the AoE
        // resolves instantly (see EnterSpin()) but the animation still needs to play out untouched, so
        // both go kinematic (Case 2). DashingToTarget is fast movement, handled by the base dash
        // primitives (collider-disabled instead, Case 1).
        bool isWindupOrSpin = ss == DaggerState.Windup || ss == DaggerState.Spinning;

        // Fixed (2026-08-10) — this previously claimed Spear-parity in its own comment without actually
        // delivering it (treated EVERY non-Moving state as committed, including BetweenSpins/PostSpin).
        // Now genuinely matches SpearGloomPD's own shape: only Windup/Spinning/DashingToTarget block
        // stun/knockback; BetweenSpins/PostSpin are stunnable/vulnerable like any other recovery beat.
        isStunnable = !isWindupOrSpin && ss != DaggerState.DashingToTarget;

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
        SnapFaceAnchor(ref state);
        SetDaggerState(ref state, DaggerState.Windup);
    }

    // See SpearGloomPD._stabOverlapResults's own doc comment for the buffer-size reasoning.
    private static readonly Collider[] _spinOverlapResults = new Collider[8];

    private void EnterSpin(ref State state)
    {
        state.stateTimer = spinDuration;
        // Decrement moved to the attack-start sites (Moving/BetweenSpins, above) — see their own comment.
        SetDaggerState(ref state, DaggerState.Spinning);

        // One-shot AoE resolve via Physics.OverlapSphereNonAlloc against CombatLayerMasks.FriendlyHurtboxMask
        // (2026-08-30, was a manual FriendlyTargetRegistryPD distance check) — see SpearGloomPD.EnterStab's
        // own doc comment for why enemy AoE attacks now match every player weapon's own collider-query
        // convention.
        // * size — matches SpearGloomPD's own stabRange * size precedent; EnemyBasePD.size has no automatic
        // transform.localScale wiring, so elite/bigger variants need this explicit to actually hit wider.
        float   dmg    = damage * spinDamageMultiplier;
        Vector3 origin = transform.position;

        int n = Physics.OverlapSphereNonAlloc(origin, spinRadius * size, _spinOverlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _spinOverlapResults[i].GetComponent<IFriendlyPD>();
            // No IsInvincible pre-check (2026-08-24 fix) — TakeDamage() already no-ops safely against
            // invincibility on its own, AND is the only place Dodge credit gets awarded (see
            // CharacterStatsPD.NotifyDodge's own doc comment); skipping the call here silently ate every
            // Dodge this attack should have granted while the player was dash-invincible.
            if (stats == null || stats.IsDead) continue;
            stats.TakeDamage(dmg, false, ObjectId);
        }
    }

    private void EnterBetweenSpins(ref State state)
    {
        state.stateTimer = betweenSpinPause;
        SetDaggerState(ref state, DaggerState.BetweenSpins);
    }

    // Chases a point BEHIND the target (through/past them, continuing along the live dasher→target
    // bearing), not stopping short like SpearGloomPD's own chase — matches old DaggerGloom.DashBehindTarget's
    // intent, but continuously re-tracked every tick now (2026-08-26, was a point snapshotted once at
    // dash-start via StartDashToPoint — a fast player could just outrun a snapshotted destination) instead
    // of computed once and left to go stale.
    private void EnterDashToTarget(ref State state)
    {
        SetDaggerState(ref state, DaggerState.DashingToTarget);
        // EffectiveMoveSpeed, not baseMoveSpeed (2026-08-13) — see BlitzGloomPD.EnterBlitz's own comment.
        // 0° — continues straight past the target's own live position instead of stopping at/before it,
        // ending up on its far side from wherever Dagger approached from. See
        // EnemyBasePD.StartOffsetChase's own doc comment for the full angular convention.
        StartOffsetChase(ref state, EffectiveMoveSpeed * dashSpeedMultiplier, dashOffsetDistance * size, 0f);
    }

    private void EnterPostSpin(ref State state)
    {
        state.stateTimer          = postSpinPause;
        state.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        SetDaggerState(ref state, DaggerState.PostSpin);
    }

    private void EnterMoving(ref State state)
    {
        // attacksRemaining deliberately left untouched here — the attack-start sites (Moving/BetweenSpins)
        // own resetting it for the natural target-lost/crowd-controlled early-exit path (refill-if-exhausted,
        // not unconditional). OnInterrupted() now zeroes it explicitly itself, BEFORE calling this, for the
        // Interrupt()-driven path — see its own doc comment.
        // Attack routine's over — reprioritize onto whoever's closest now, rather than sitting on whoever
        // this routine happened to start against. See EnemyBasePD.FindClosestTarget's own doc comment.
        FindClosestTarget();
        SetDaggerState(ref state, DaggerState.Moving);
    }

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above, fixed
    // 2026-08-10 to genuinely match SpearGloomPD's shape) and meant to be cancelable — CanBeInterrupted now
    // defaults to false (see EnemyBasePD's own doc comment). BetweenSpins/PostSpin are stunnable/vulnerable,
    // so are correctly excluded here too — no longer "everything except Moving."
    protected override bool CanBeInterrupted
    {
        get
        {
            var ss = GetDaggerState(ref currentState);
            return ss == DaggerState.Windup || ss == DaggerState.Spinning || ss == DaggerState.DashingToTarget;
        }
    }

    // Fully aborts the attack chain (2026-08-12, reworked from "leave attacksRemaining untouched" — this
    // also now applies to a DashingToTarget interrupt, previously the one case that effectively just cost a
    // single rep) — an interrupted attack no longer retries with whatever reps it had left; it zeroes the
    // counter AND puts the whole sequence on a full fresh cooldown (same formula EnterPostSpin's own natural
    // chain-end already uses), so the very next Moving-state check can't immediately refill-and-restart.
    // Moving itself is stunnable (see isStunnable's own per-tick assignment above), so a Stun() paired with
    // the same hit as this Interrupt() (always called BEFORE a paired Stun(), see IEnemyPD.Interrupt()'s own
    // doc comment) freezes this enemy here via EnemyBasePD.Simulate()'s own isStunnable-gated freeze for the
    // stun's whole duration before it's ever able to attack again — no extra handling needed for that here.
    protected override void OnInterrupted()
    {
        currentState.attacksRemaining    = 0;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        EnterMoving(ref currentState);

        // Predicted-instant hide, not left for UpdateView's own stateId-driven redraw to eventually catch
        // up — Interrupt() mutates currentState out-of-band (from a different identity's own Simulate(),
        // not this enemy's own HandleActiveState flow this tick), which was leaving the windup indicator
        // visibly stuck for a stray tick before UpdateView's viewState caught up to the interrupt.
        // _lastVisualState deliberately NOT written here — see SpearGloomPD.OnInterrupted's own comment for
        // why (UpdateView's own edge-detection bookkeeping, not ours to set — would silently skip its
        // _animator.Play(IdleHash, ...) call otherwise).
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null) _windupFill.Hide();
    }

    // ── View ───────────────────────────────────────────────────────────────
    private DaggerState _lastVisualState = (DaggerState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        // Re-applied every frame — same reasoning as every other indicator in this rebuild: if spinRadius
        // changes at runtime, the indicator should never silently drift out of sync with it. Radial
        // (cylinder), not line — this is an AoE, not a directional reach. * size matches EnterSpin's own
        // hit-check exactly (see that method's comment). centerForwardOffset left at its 0 default —
        // _windupIndicator's own current position (WindupFillPD's own transform) already IS the center.
        if (_windupIndicator != null && _windupFill != null)
            _windupFill.ShowCircle(spinRadius * size);

        var ss = (DaggerState)viewState.stateId;

        // Live, every frame regardless of the _lastVisualState-gated block below — reacts immediately to
        // EffectiveAttackSpeed changing mid-state, same reasoning as SpearGloomPD's own live block.
        if (_windupFill != null && ss == DaggerState.Windup)
            _windupFill.SetProgress(1f - Mathf.Clamp01(viewState.stateTimer / windupDuration));
        if (_animator != null)
        {
            if (ss == DaggerState.Windup)
                _animator.SetFloat(WindupSpeedHash, _windupClipRate * EffectiveAttackSpeed);
            else if (ss == DaggerState.Spinning)
                _animator.SetFloat(SpinSpeedHash, _spinClipRate * EffectiveAttackSpeed);
            else if (ss == DaggerState.BetweenSpins)
                _animator.SetFloat(PostSpinSpeedHash, (_postSpinClipLength / betweenSpinPause) * EffectiveAttackSpeed);
            else if (ss == DaggerState.PostSpin)
                _animator.SetFloat(PostSpinSpeedHash, (_postSpinClipLength / postSpinPause) * EffectiveAttackSpeed);
        }

        if (ss == _lastVisualState) return;
        _lastVisualState = ss;

        if (_windupIndicator != null) _windupIndicator.SetActive(ss == DaggerState.Windup);
        if (_windupFill != null && ss != DaggerState.Windup) _windupFill.Hide();

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
                _animator.Play(DaggerPostSpinHash, 0, 0f);
                break;
            case DaggerState.PostSpin:
                _animator.Play(DaggerPostSpinHash, 0, 0f);
                break;
        }
    }

    protected override void OnDeathStop()
    {
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.Hide();
        // Snaps out of whatever attack pose this enemy died mid-animation in — see SpearGloomPD's own
        // OnDeathStop comment for why (stateId is frozen from here on, so nothing else will do this).
        // Guarded on activeInHierarchy — see SpearGloomPD's own comment on the same guard.
        if (_animator != null && gameObject.activeInHierarchy) _animator.Play(IdleHash, 0, 0f);
    }

    protected override void OnRespawnReset()
    {
        ApplyAnimatorClipSpeeds();
        currentState.stateId             = 0;
        currentState.attacksRemaining    = attacksPerAttack;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.stateTimer          = 0f;
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.Hide();
        // In case death happened mid-Windup/Spinning, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (stateId resets to Moving above), but resetting explicitly
        // here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (DaggerState)(-1);
    }
}
