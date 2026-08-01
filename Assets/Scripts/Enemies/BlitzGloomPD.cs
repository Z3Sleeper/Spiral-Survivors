using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Enemies/BlitzGloom.cs — a straight-line dash attacker. Old system's
// windup-indicator unparenting hack (SetParent(null) + manually driving world position/rotation every
// frame) isn't needed here — the indicator just stays nested under Visuals like SpearGloomPD's own windup
// indicator, inheriting facing naturally once SnapFaceTarget rotates the body at windup entry. Damage
// comes entirely from the generic ContactDamageCollider + CanContactDamage (matching old's
// CanContactDamage => Blitzing) — not a dedicated hitbox, since Blitz's "attack" is just its own body
// moving fast into whatever it touches, unlike SpearGloomPD's separate stab hitbox.
public class BlitzGloomPD : EnemyBasePD<BlitzGloomPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int     stateId;              // cast to/from BlitzState, same convention as SpearGloomPD
        public float   stateTimer;
        public float   attackCooldownTimer;
        public Vector3 blitzDirection;        // locked in at Windup entry, reused through Blitzing
        public int     blitzesRemaining;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    public enum BlitzState { Moving, Windup, Blitzing, PostBlitz }

    private BlitzState GetBlitzState(ref State s) => (BlitzState)s.stateId;
    private void SetBlitzState(ref State s, BlitzState st) => s.stateId = (int)st;

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    // Only the dash itself deals contact damage — matches old's identical CanContactDamage override.
    public override bool CanContactDamage => (BlitzState)currentState.stateId == BlitzState.Blitzing;

    [Header("Blitz Settings")]
    [SerializeField] private float windupDuration       = 0.7f;
    [SerializeField] private float postBlitzPause       = 0.5f;
    [SerializeField] private float attackCooldown       = 5f;
    [SerializeField] private float blitzDistance        = 15f;
    [SerializeField] private float blitzSpeedMultiplier = 25f;
    [SerializeField] private int   blitzesPerAttack     = 1;
    [SerializeField] private GameObject   windupIndicator;
    [SerializeField] private WindupFillPD windupFill;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void LateAwake()
    {
        base.LateAwake();
        // Randomized initial cooldown (1x-2x attackCooldown), matching old BlitzGloom.OnSpawned — spreads
        // out multiple simultaneously-spawned Blitzers so they don't all windup in lockstep. GameRandom,
        // not UnityEngine.Random, for determinism (old system's UnityEngine.Random.Range wasn't
        // replay-safe, but ran on a NetworkBehaviour without PD's resimulation concerns).
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.blitzesRemaining    = blitzesPerAttack;
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded)
        {
            state.blitzesRemaining--;
            // _target == null guard matters here — without it, a chain that lost its target mid-way would
            // charge again in whatever the PREVIOUS charge's stale direction was (SnapFaceAnchor no-ops
            // on a zero direction, leaving blitzDirection untouched) instead of ending the chain.
            // IsTargetStillValid — a target that went invisible-and-non-taunting since the last charge
            // shouldn't get chained into another one against them; see that property's own doc comment.
            // !IsCrowdControlled — a true Stun() or Slow() landing mid-sequence never interrupts the dash
            // that just ended above, but stops the chain from continuing into another one, falling through
            // to PostBlitz instead (see IsCrowdControlled's own doc comment).
            if (state.blitzesRemaining > 0 && _target != null && IsTargetStillValid && !IsCrowdControlled)
            {
                CaptureAttackAnchor(ref state);
                EnterWindup(ref state);
            }
            else EnterPostBlitz(ref state);
        }

        // attackCooldownTimer: untouched — pacing between attack sequences, not execution speed.
        // stateTimer: scaled live by EffectiveAttackSpeed every tick — see SpearGloomPD's own comment on
        // the identical pattern. Note the actual Blitz dash itself needs no separate handling here at all:
        // CanContactDamage is gated to the whole Blitzing state, and HandleDash already applies
        // EffectiveAttackSpeed live to the dash's own velocity (see EnemyBasePD.HandleDash), so "attack
        // speed = dash speed" already holds for free.
        state.attackCooldownTimer -= delta;
        state.stateTimer          -= delta * EffectiveAttackSpeed;

        var ss = GetBlitzState(ref state);

        switch (ss)
        {
            case BlitzState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    state.blitzesRemaining = blitzesPerAttack;
                    // Ongoing attack starts here — see CaptureAttackAnchor's own doc comment.
                    CaptureAttackAnchor(ref state);
                    EnterWindup(ref state);
                }
                break;

            case BlitzState.Windup:
                if (state.stateTimer <= 0f) EnterBlitz(ref state);
                break;

            case BlitzState.PostBlitz:
                if (state.stateTimer <= postBlitzPause * 0.5f) FaceTargetSmooth(delta);
                if (state.stateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ss = GetBlitzState(ref state);
        // Blocks stun/knockback for the entire attack sequence (not just Windup) — matches old
        // BlitzGloom.SetState's identical isStunnable assignment, a deliberate per-enemy choice rather
        // than a universal rule (compare SpearGloomPD, which only blocks during its two kinematic states).
        isStunnable = ss == BlitzState.Moving ? !stunResistant : false;

        // Windup is a stationary telegraphed-attack window — kinematic (Case 2, immovable but still
        // physically present), matching SpearGloomPD/KingSpikePD's convention. Blitzing is fast movement
        // handled entirely by the base dash primitives (collider-disabled instead, Case 1) — HandleDash
        // already disables/re-enables _physicsCollider around the dash itself, but re-deriving it here
        // every tick (rather than relying solely on that) matches SpearGloomPD's own explicit convention.
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = ss == BlitzState.Windup;
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }

        if (_physicsCollider != null)
            _physicsCollider.enabled = ss != BlitzState.Blitzing;
    }

    // ── State transitions ────────────────────────────────────────────────────

    private void EnterWindup(ref State state)
    {
        var snapDir = SnapFaceAnchor(ref state);
        if (snapDir != Vector3.zero) state.blitzDirection = snapDir;
        state.stateTimer = windupDuration;
        SetBlitzState(ref state, BlitzState.Windup);
    }

    private void EnterBlitz(ref State state)
    {
        Vector3 endPoint = transform.position + state.blitzDirection * blitzDistance;
        SetBlitzState(ref state, BlitzState.Blitzing);
        StartDashToPoint(ref state, baseMoveSpeed * blitzSpeedMultiplier, endPoint);
    }

    private void EnterPostBlitz(ref State state)
    {
        state.stateTimer = postBlitzPause;
        // Flat, not randomized — only the initial spawn-time cooldown gets randomized (to desync multiple
        // simultaneously-spawned Blitzers), not every recurring attack cooldown afterward.
        state.attackCooldownTimer = attackCooldown;
        SetBlitzState(ref state, BlitzState.PostBlitz);
    }

    private void EnterMoving(ref State state)
    {
        state.blitzesRemaining = blitzesPerAttack;
        // Attack routine's over — reprioritize onto whoever's closest now, rather than sitting on whoever
        // this routine happened to start against. See EnemyBasePD.FindClosestTarget's own doc comment.
        FindClosestTarget();
        SetBlitzState(ref state, BlitzState.Moving);
    }

    // Every Blitz phase is interruptible (no override of CanBeInterrupted needed) — this just resets the
    // state machine back to Moving, same terminal transition the attack chain already uses when it runs out
    // of blitzesRemaining naturally. See EnemyBasePD.Interrupt()'s own doc comment.
    protected override void OnInterrupted() => EnterMoving(ref currentState);

    // ── View ──────────────────────────────────────────────────────────────────
    private BlitzState _lastVisualState = (BlitzState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        // Same reasoning as SpearGloomPD's guard — this can run while a pooled instance is transiently
        // inactive during replay; bail before recording _lastVisualState so the transition retries once
        // actually active, instead of leaving stale indicator state from a previous pooled life.
        if (!gameObject.activeInHierarchy) return;

        // Re-applied every frame, not just once at spawn — see SpearGloomPD/KingSpikePD's identical
        // reasoning: if blitzDistance is ever changed at runtime, the indicator's visual footprint should
        // never silently drift out of sync with it.
        if (windupIndicator != null) IndicatorMath.ApplyLineTransform(windupIndicator.transform, blitzDistance);

        var ss = (BlitzState)viewState.stateId;

        // Live, every frame regardless of the _lastVisualState-gated block below — see SpearGloomPD's own
        // comment on the identical live-fill pattern.
        if (windupFill != null && ss == BlitzState.Windup)
            windupFill.SetProgress(1f - Mathf.Clamp01(viewState.stateTimer / windupDuration));

        if (ss == _lastVisualState) return;
        _lastVisualState = ss;

        if (windupIndicator != null) windupIndicator.SetActive(ss == BlitzState.Windup);
        if (windupFill != null && ss != BlitzState.Windup) windupFill.ResetFill();
    }

    protected override void OnDeathStop()
    {
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null)      windupFill.ResetFill();
    }

    protected override void OnRespawnReset()
    {
        currentState.stateId             = 0;
        currentState.stateTimer          = 0f;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.blitzesRemaining    = blitzesPerAttack;
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null)      windupFill.ResetFill();
        // In case death happened mid-Windup, while kinematic was on — HandleActiveState would correct this
        // on the next tick anyway (stateId resets to Moving above), but resetting explicitly here avoids
        // even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (BlitzState)(-1);
    }
}
