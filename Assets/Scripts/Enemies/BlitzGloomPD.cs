using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Enemies/BlitzGloom.cs — a straight-line dash attacker. Old system's
// windup-indicator unparenting hack (SetParent(null) + manually driving world position/rotation every
// frame) isn't needed here — the indicator just stays nested under Visuals like SpearGloomPD's own windup
// indicator, inheriting facing naturally once the body itself rotates. Damage comes entirely from the
// generic ContactDamageCollider + CanContactDamage (matching old's CanContactDamage => Blitzing) — not a
// dedicated hitbox, since Blitz's "attack" is just its own body moving fast into whatever it touches,
// unlike SpearGloomPD's separate stab hitbox.
//
// Live-tracks its committed target for the whole Windup, then LOCKS the instant Blitzing begins (2026-08-21,
// same "follow the player during the telegraph, then commit" feel KingOrbiterPD's own Shoot-phase indicators
// and MortarGloomPD's own hazard already have) — much simpler here than either of those, since there's no
// separately-spawned object with its own independent clock to keep synced: the body itself just keeps
// re-facing _windupTrackedTarget every Windup tick (TrackWindupTarget, called from HandleActiveState's own
// Windup case, which already runs at this enemy's own attack-speed-scaled pace by construction — nothing
// extra needed for that), and the indicator inherits the new facing for free via ordinary parent-child
// transform inheritance, exactly as it always did for the old one-time SnapFaceAnchor. Locking is equally
// free: nothing updates blitzDirection once EnterBlitz's own SetBlitzState flips this out of Windup, so it
// naturally stays exactly wherever TrackWindupTarget last left it — no separate flag/Lock() call needed.
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
    [SerializeField] private float windupDuration       = 0.5f;
    [SerializeField] private float postBlitzPause       = 0.5f;
    [SerializeField] private float blitzDistance        = 12f;
    [SerializeField] private float blitzSpeedMultiplier = 3.5f;
    // Tier1-authored baseline, scaled per tier by blitzesPerAttackTierGrowth below (applied in
    // OnConfigureSpawn()). See _authoredBlitzesPerAttack's own doc comment for why this needs the
    // snapshot-and-reset treatment instead of just incrementing this field directly.
    [SerializeField] private int   blitzesPerAttack     = 1;
    // Captured once in OnAwake(), reset back onto blitzesPerAttack every pooled reuse in LateAwake() —
    // otherwise OnConfigureSpawn's += would keep compounding across reuses (e.g. tier3 life leaves this at
    // 3, then a LATER tier1 reuse of the same pooled slot would add 0 to that stale 3 instead of starting
    // fresh from 1) — same "_authoredX snapshot" pattern EnemyBasePD's own maxHealth/damage/moveSpeed/
    // xpValue/enemyName already use.
    private int _authoredBlitzesPerAttack;

    [Header("Attack Cooldown")]
    [SerializeField] private float attackCooldown = 5f;
    // Captured once in OnAwake(), reset back onto attackCooldown every pooled reuse in LateAwake(), scaled
    // by attackCooldownMultiplier (Threat Level 5) in OnConfigureSpawn() — same "_authoredX snapshot"
    // pattern as _authoredBlitzesPerAttack above, needed for the same reason (avoid compounding across
    // pooled reuses).
    private float _authoredAttackCooldown;

    [Header("Tier Scaling")]
    // Added onto blitzesPerAttack per tier above 1 — see OnConfigureSpawn().
    [SerializeField] private int blitzesPerAttackTierGrowth = 1;

    [Header("Indicators")]
    [SerializeField] private GameObject   windupIndicator;
    [SerializeField] private WindupFillPD windupFill;

    // The SPECIFIC player this Windup committed to, captured once at EnterWindup — see class doc comment.
    // Plain field (not State), same "commit once, keep reading that same reference" contract as
    // KingOrbiterPD's own _shotTargetSnapshot — immune to _targetStats getting reassigned to someone else
    // mid-Windup (taunt/invisible-retarget). Only ever read while actually in Windup.
    private CharacterStatsPD _windupTrackedTarget;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnAwake()
    {
        base.OnAwake();
        _authoredBlitzesPerAttack = blitzesPerAttack;
        _authoredAttackCooldown = attackCooldown;
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        // Reset to the tier1-authored baseline before OnConfigureSpawn (which always runs AFTER LateAwake(),
        // once hierarchy.Create() returns) scales it fresh for this life — see _authoredBlitzesPerAttack's
        // own doc comment.
        blitzesPerAttack = _authoredBlitzesPerAttack;
        attackCooldown = _authoredAttackCooldown;
        // Randomized initial cooldown (1x-2x attackCooldown), matching old BlitzGloom.OnSpawned — spreads
        // out multiple simultaneously-spawned Blitzers so they don't all windup in lockstep. GameRandom,
        // not UnityEngine.Random, for determinism (old system's UnityEngine.Random.Range wasn't
        // replay-safe, but ran on a NetworkBehaviour without PD's resimulation concerns).
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        // Set again in OnConfigureSpawn() once blitzesPerAttack is actually tier-scaled — this LateAwake()
        // assignment runs BEFORE that scaling lands, so it's only ever a placeholder for the brief window
        // in between.
        currentState.blitzesRemaining    = blitzesPerAttack;
    }

    // Tier1-authored blitzesPerAttack, scaled by blitzesPerAttackTierGrowth per tier above 1 (see that
    // field's own doc comment). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call).
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        blitzesPerAttack += (spawnTier - 1) * blitzesPerAttackTierGrowth;
        currentState.blitzesRemaining = blitzesPerAttack;

        // Threat Level 5 — halves attackCooldown (attackCooldownMultiplier is 0.5f once difficulty >= 5,
        // else 1f — see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set the placeholder
        // attackCooldownTimer LateAwake() already assigned above, using the now-scaled value, same
        // "set again once the real scaling lands" reasoning as blitzesRemaining above.
        attackCooldown = _authoredAttackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded)
        {
            // Decrement moved to EnterWindup (attack START, not this completion point) — see its own
            // comment for why. blitzesRemaining already reflects the blitz that just ended by the time we
            // get here.
            // _target == null guard matters here — without it, a chain that lost its target mid-way would
            // charge again in whatever the PREVIOUS charge's stale direction was (SnapFaceAnchor no-ops
            // on a zero direction, leaving blitzDirection untouched) instead of ending the chain.
            // IsTargetStillValid — a target that went invisible-and-non-taunting since the last charge
            // shouldn't get chained into another one against them; see that property's own doc comment.
            // !IsCrowdControlled — a true Stun() landing mid-sequence never interrupts the dash that just
            // ended above, but stops the chain from continuing into another one, falling through to
            // PostBlitz instead (a Slow does NOT stop the chain — see IsCrowdControlled's own doc comment,
            // hard CC only).
            if (state.blitzesRemaining > 0 && _target != null && IsTargetStillValid && !IsCrowdControlled)
            {
                CaptureAttackAnchor(ref state);
                EnterWindup(ref state);
            }
            else EnterPostBlitz(ref state);
        }

        var ss = GetBlitzState(ref state);

        // attackCooldownTimer: only ticks while actually in Moving (2026-08-19 — uniform across every
        // multi-phase enemy's own attack-cadence cooldown now, see OrbiterGloomPD's own fix). stateTimer:
        // scaled live by EffectiveAttackSpeed every tick — see SpearGloomPD's own comment on the identical
        // pattern. Note the actual Blitz dash itself needs no separate handling here at all: CanContactDamage
        // is gated to the whole Blitzing state, and HandleDash already applies EffectiveAttackSpeed live to
        // the dash's own velocity (see EnemyBasePD.HandleDash), so "attack speed = dash speed" already holds
        // for free.
        if (ss == BlitzState.Moving)
            state.attackCooldownTimer -= delta;
        state.stateTimer -= delta * EffectiveAttackSpeed;

        switch (ss)
        {
            case BlitzState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    // blitzesRemaining itself is handled by EnterWindup now, not here — see its own comment.
                    // Ongoing attack starts here — see CaptureAttackAnchor's own doc comment.
                    CaptureAttackAnchor(ref state);
                    EnterWindup(ref state);
                }
                break;

            case BlitzState.Windup:
                TrackWindupTarget(ref state);
                if (state.stateTimer <= 0f) EnterBlitz(ref state);
                break;

            case BlitzState.PostBlitz:
                if (state.stateTimer <= postBlitzPause * 0.5f) FaceTargetSmooth(delta);
                if (state.stateTimer <= 0f) EnterMoving(ref state);
                break;
        }

        ss = GetBlitzState(ref state);

        // Reworked (2026-08-10) — previously blocked stun/knockback for the ENTIRE non-Moving sequence
        // (including PostBlitz) as a deliberate per-enemy choice; now matches every other multi-phase
        // enemy's own convention instead (SpearGloomPD/DaggerGloomPD/ShooterGloomPD/MortarGloomPD): only the
        // actual committed windup+attack blocks it, PostBlitz recovers to stunnable/vulnerable like any
        // other Post/Between beat.
        bool isWindupOrBlitzing = ss == BlitzState.Windup || ss == BlitzState.Blitzing;
        isStunnable = !isWindupOrBlitzing;

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
        // Decrement lives HERE — the actual START of one blitz attempt — not at the dash's own completion
        // (dashJustEnded, above, where it used to live). A blitz cancelled mid-windup or mid-dash via
        // Interrupt() now still counts against the budget, since blitzesRemaining already dropped the
        // instant this ran. Refill-if-exhausted (not an unconditional reset) — called from both a fresh
        // Moving-triggered attack (blitzesRemaining sitting at 0 from a previous fully-completed sequence,
        // or a pooled spawn) AND the dashJustEnded continuation branch above (blitzesRemaining already > 0,
        // so this is a no-op there). This is also why Moving's own attack-trigger and EnterMoving/
        // OnInterrupted no longer stomp blitzesRemaining themselves — this single self-contained check is
        // the only reset point now, so an interrupted sequence's leftover count survives into whatever
        // attack comes next instead of being thrown away. See KingSpikePD.EnterChargeWindup for the same
        // pattern.
        if (state.blitzesRemaining <= 0) state.blitzesRemaining = blitzesPerAttack;
        state.blitzesRemaining--;

        // Captured here — the commit moment for this strike — not read live off _targetStats later; see
        // class doc comment and _windupTrackedTarget's own field comment.
        _windupTrackedTarget = _targetStats;
        state.stateTimer = windupDuration;
        SetBlitzState(ref state, BlitzState.Windup);
        // Immediate first-tick orientation, same reasoning HandleActiveState's own Windup case calls this
        // every subsequent tick for — without this, the body would keep whatever rotation Moving left it at
        // for a full tick before tracking visibly started.
        TrackWindupTarget(ref state);
    }

    // Re-faces the body toward _windupTrackedTarget's LIVE position every Windup tick (instant snap, not
    // smoothed — matches the old one-time SnapFaceAnchor's own instant nature, just now called continuously)
    // and keeps state.blitzDirection updated to match, so whatever EnterBlitz reads the instant Windup ends
    // is always the freshest possible direction — see class doc comment for why that alone is enough to make
    // this "lock" automatically. No-op (holds last direction) if the target's died/gone invisible-without-
    // taunting mid-Windup, or if the computed direction is degenerate (target exactly on top of this enemy)
    // — same "freeze on invalid, don't stomp with zero" convention the old SnapFaceAnchor call already had.
    private void TrackWindupTarget(ref State state)
    {
        if (_windupTrackedTarget == null || _windupTrackedTarget.IsDead ||
            (_windupTrackedTarget.IsInvisible && !_windupTrackedTarget.IsTaunting)) return;

        Vector3 dir = _windupTrackedTarget.transform.position - transform.position;
        dir.y = 0f;
        if (dir == Vector3.zero) return;
        dir.Normalize();
        _rigidbody.MoveRotation(Quaternion.LookRotation(dir));
        state.blitzDirection = dir;
    }

    private void EnterBlitz(ref State state)
    {
        Vector3 endPoint = transform.position + state.blitzDirection * blitzDistance;
        SetBlitzState(ref state, BlitzState.Blitzing);
        // EffectiveMoveSpeed, not baseMoveSpeed (2026-08-13) — so this scales with Boss enrage stacking and
        // stays proportionate to whatever EnemySpawnerPD's own tier scaling did to moveSpeed, same as every
        // other dashing enemy's own dash-speed formula (see EnemyBasePD.StartDashToTarget's own doc comment).
        StartDashToPoint(ref state, EffectiveMoveSpeed * blitzSpeedMultiplier, endPoint);
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
        // blitzesRemaining deliberately left untouched here — EnterWindup owns resetting it for the natural
        // target-lost/crowd-controlled early-exit path (refill-if-exhausted, not unconditional).
        // OnInterrupted() now zeroes it explicitly itself, BEFORE calling this, for the Interrupt()-driven
        // path — see its own doc comment.
        // Attack routine's over — reprioritize onto whoever's closest now, rather than sitting on whoever
        // this routine happened to start against. See EnemyBasePD.FindClosestTarget's own doc comment.
        FindClosestTarget();
        SetBlitzState(ref state, BlitzState.Moving);
    }

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above, fixed
    // 2026-08-10 to match the rest of the roster) and meant to be cancelable — CanBeInterrupted now defaults
    // to false (see EnemyBasePD's own doc comment). PostBlitz is stunnable/vulnerable, so is correctly
    // excluded here too — no longer "everything except Moving."
    protected override bool CanBeInterrupted
    {
        get
        {
            var ss = GetBlitzState(ref currentState);
            return ss == BlitzState.Windup || ss == BlitzState.Blitzing;
        }
    }

    // Fully aborts the attack chain (2026-08-12, reworked from "leave blitzesRemaining untouched") — an
    // interrupted attack no longer retries with whatever reps it had left; it zeroes the counter AND puts
    // the whole sequence on a full fresh cooldown (flat, not randomized — matches EnterPostBlitz's own
    // explicit "only the initial spawn-time cooldown gets randomized" choice), so the very next Moving-state
    // check can't immediately restart. Moving itself is stunnable (see isStunnable's own per-tick assignment
    // above), so a Stun() paired with the same hit as this Interrupt() (always called BEFORE a paired
    // Stun(), see IEnemyPD.Interrupt()'s own doc comment) freezes this enemy here via EnemyBasePD.Simulate()'s
    // own isStunnable-gated freeze for the stun's whole duration before it's ever able to attack again — no
    // extra handling needed for that here.
    protected override void OnInterrupted()
    {
        currentState.blitzesRemaining    = 0;
        currentState.attackCooldownTimer = attackCooldown;
        EnterMoving(ref currentState);

        // Predicted-instant hide, not left for UpdateView's own stateId-driven redraw to eventually catch
        // up — Interrupt() mutates currentState out-of-band (from a different identity's own Simulate(),
        // not this enemy's own HandleActiveState flow this tick), which could otherwise leave the windup
        // indicator visibly stuck for a stray tick before UpdateView's viewState catches up to the
        // interrupt. Same fix applied to every other enemy with a windup indicator (Spear/Dagger/Leaper/
        // KingSpike) — see KingSpikePD.OnInterrupted's own comment.
        // _lastVisualState deliberately NOT written here — see SpearGloomPD.OnInterrupted's own comment for
        // why (UpdateView's own edge-detection bookkeeping, not ours to set).
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null) windupFill.ResetFill();
    }

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
        _windupTrackedTarget = null;
    }
}
