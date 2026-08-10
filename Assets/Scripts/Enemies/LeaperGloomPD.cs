using PurrNet.Packing;
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
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int     stateId;              // cast to/from LeapState, same convention as SpearGloomPD
        public float   stateTimer;
        // Counts up from 0 at EnterWindup() through the end of Leaping — unlike stateTimer (which counts
        // DOWN and gets reset to a new baseline at the Windup->Leaping boundary, so it means "time left in
        // THIS sub-phase" rather than one consistent thing), this represents one single quantity ("time
        // since this windup began") for the whole combined span. See UpdateView's fill computation for why
        // that distinction matters for interpolated rendering specifically.
        public float   attackElapsed;
        public float   attackCooldownTimer;
        public Vector3 leapStart;
        public Vector3 leapTarget;            // locked in at Windup entry, reused through Leaping/Landing
        public int     leapsRemaining;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
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

    // Baseline "clip length / baseline duration" ratios, cached instead of SetFloat'd directly — both need
    // to be re-derived live every frame (ratio * EffectiveAttackSpeed, see UpdateView) instead of set once
    // at spawn, same reasoning as every other attack-speed-scaled enemy in this pass. The leap's own actual
    // traversal speed needs no separate handling here at all — it's driven directly by
    // state.stateTimer / leapDuration (see HandleActiveState's Leaping case), and stateTimer's decrement
    // rate is already scaled by EffectiveAttackSpeed below, so the arc lerp itself speeds up/slows down for
    // free, same effect as scaling dash speed for every other enemy's own attack-speed-scaled dash.
    private float _windupClipRate;
    private float _leapClipRate;

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
    // Bobbed vertically (cosmetic only) during Leaping — deliberately a dedicated wrapper (YAxisMovers),
    // not the raw model mesh transform directly: PurrDiction's PredictedTransform tracks a hurtbox/root
    // structure of its own, and driving a transform it also touches fights it (same "YAxisMovers, not
    // Visuals" fix RogueSpinAbilityPD's leap needed for the player). Everything that should visually rise with
    // the leap (model, shell, indicators, etc.) lives under this one wrapper instead.
    [SerializeField] private Transform _yAxisMovers;
    [SerializeField] private GameObject   _windupIndicator;
    [SerializeField] private WindupFillPD _windupFill;

    private Vector3 _yAxisMoversRestPosition;
    private bool    _yAxisMoversRestPositionCaptured;

    // Plain (not predicted State) shadow of state.leapTarget, written in lockstep inside EnterWindup() —
    // the same deterministic Simulate() call that decides leapTarget itself, so this is available
    // instantly on every machine with no network round-trip, unlike reading it back off `verified`.
    // Reading it off `viewState` instead wouldn't work either: PurrDiction linearly interpolates every
    // State field (including plain Vector3s) between simulated ticks for smooth rendering, and leapTarget
    // is a discrete "decided once" value — viewState would show it sliding in from whatever it was
    // previously rather than snapping instantly. A plain field isn't part of State, so it isn't subject to
    // that interpolation at all.
    private Vector3 _leapTargetSnapshot;

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

        // Guarded to a single real capture per instance, same reasoning as _windupIndicatorParent above —
        // UpdateView() bails out immediately on death (before it ever reaches the branch that resets
        // _yAxisMovers back to rest), so a Leaper that dies mid-Leaping (arc-bobbed up) gets pooled with
        // its visuals still elevated. An unconditional recapture here would then bake that elevated position
        // in as the new "rest" position for the rest of this instance's pooled life — OnRespawnReset()
        // would keep dutifully "resetting" it back to a wrong, floating baseline. This is what caused the
        // "leaper stuck floating" bug.
        if (!_yAxisMoversRestPositionCaptured && _yAxisMovers != null)
        {
            _yAxisMoversRestPosition = _yAxisMovers.localPosition;
            _yAxisMoversRestPositionCaptured = true;
        }

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
                _windupClipRate = clip.length / windupDuration;
            else if (clip.name == "Leaper Gloom Leap")
                _leapClipRate = clip.length / leapDuration;
        }
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        // attackCooldownTimer: untouched — pacing between attack sequences, not execution speed.
        // stateTimer: scaled live by EffectiveAttackSpeed every tick — see SpearGloomPD's own comment on
        // the identical pattern. This is also what drives the Leaping arc lerp below, so the leap's own
        // traversal speed scales for free too.
        state.attackCooldownTimer -= delta;
        state.stateTimer          -= delta * EffectiveAttackSpeed;

        var ss = GetLeapState(ref state);

        switch (ss)
        {
            case LeapState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TargetInRange())
                {
                    // leapsRemaining itself is handled by EnterWindup now, not here — see its own comment.
                    EnterWindup(ref state);
                }
                break;

            case LeapState.Windup:
                state.attackElapsed += delta * EffectiveAttackSpeed;
                if (state.stateTimer <= 0f) EnterLeap(ref state);
                break;

            case LeapState.Leaping:
                state.attackElapsed += delta * EffectiveAttackSpeed;
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
            bool kinematicChanging = _rigidbody.isKinematic != shouldBeKinematic;

            // Zero velocity on EITHER transition direction, not just becoming-kinematic. A kinematic
            // rigidbody driven by MovePosition (the Leaping arc lerp above) still reports an implicit
            // velocity derived from its position delta each tick — Unity computes this so the kinematic
            // body can push dynamic bodies it collides with. That leftover velocity was still sitting on
            // the rigidbody the instant Leaping->Landing flipped isKinematic back to false, so it got
            // applied as real physics velocity and shot the leaper off in whatever direction the final
            // leap frame was moving — the "sliding after leap" bug. Matches old LeaperGloom.Land()'s
            // explicit _rb.linearVelocity = Vector3.zero right after un-kinematic-ing.
            //
            // The zero-out has to happen on whichever side of the isKinematic assignment the body is
            // actually non-kinematic — Rigidbody.velocity's setter throws "not supported on a kinematic
            // body" otherwise. Entering kinematic: it's still non-kinematic right up until the assignment
            // below, so zero first. Leaving kinematic: it's still kinematic until that same assignment
            // runs, so the zero has to come after.
            if (kinematicChanging && shouldBeKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
            if (kinematicChanging && !shouldBeKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
        }

        if (_physicsCollider != null) _physicsCollider.enabled = ss != LeapState.Leaping;
        // SetHurtboxDisabled (not a raw _hurtbox.enabled write) — this ALSO grants invulnerability + shows
        // the shell for the same window, per explicit design ("Hurtbox Disabled = Invincible" always, the
        // reverse not necessarily true — see IEnemyPD.SetHurtboxDisabled's own doc comment). Previously this
        // enemy was unhittable mid-air but NOT formally invulnerable, meaning a hit that somehow still
        // landed (e.g. a lingering AoE already resolved against a cached reference) would have dealt real
        // damage nobody could see land — this closes that gap too, not just the visual/messaging one.
        SetHurtboxDisabled(ss == LeapState.Leaping);
    }

    // ── State transitions ──────────────────────────────────────────────────

    private void EnterWindup(ref State state)
    {
        if (_target == null) return;

        // Decrement lives HERE — the actual START of one leap attempt — not at landing (EnterPostLand,
        // where it used to live). A leap cancelled mid-windup or mid-air via Interrupt() now still counts
        // against the budget, since leapsRemaining already dropped the instant this ran. Refill-if-exhausted
        // (not an unconditional reset) — called from both a fresh Moving-triggered attack (leapsRemaining
        // sitting at 0 from a previous fully-completed chain, or a pooled spawn) AND EnterPostLand's own
        // mid-chain continuation (leapsRemaining already > 0, so this is a no-op there). This is also why
        // Moving's own attack-trigger and EnterMoving/OnInterrupted no longer stomp leapsRemaining
        // themselves — this single self-contained check is the only reset point now, so an interrupted
        // chain's leftover count survives into whatever attack comes next instead of being thrown away.
        if (state.leapsRemaining <= 0) state.leapsRemaining = leapsPerAttack;
        state.leapsRemaining--;

        // Fixed-radius, random-angle offset — matches KingSpikePD's remote-burst offset (a uniform random
        // angle at a FIXED distance, not old's Random.insideUnitCircle.normalized equivalent, ported to
        // GameRandom for determinism).
        float   angle  = GameRandom.instance.NextFloat(0f, 360f) * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * leapOffsetRadius;
        state.leapTarget = _target.position + offset;
        _leapTargetSnapshot = state.leapTarget;

        SnapFaceTarget(ref state);
        state.stateTimer    = windupDuration;
        state.attackElapsed = 0f;
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
        // back to non-kinematic until the shared post-switch block runs later this same tick). That block
        // is what actually clears the leftover MovePosition-derived velocity on the kinematic->false edge —
        // see its comment for why that leftover velocity exists at all.

        state.stateTimer = landingDuration;
        SetLeapState(ref state, LeapState.Landing);

        // One-shot AoE resolve on landing, matching KingSpikePD.FireSpikeBurst/DaggerGloomPD.EnterSpin — a
        // plain distance check against CharacterStatsPD.All rather than a physics trigger/collider.
        // * size — matches SpearGloomPD/DaggerGloomPD's own size-scaled hit-check precedent.
        float   dmg    = damage * landDamageMultiplier;
        Vector3 origin = transform.position;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead || stats.IsInvincible) continue;
            if (Vector3.Distance(stats.transform.position, origin) <= landRadius * size)
                stats.TakeDamage(dmg);
        }
    }

    private void EnterPostLand(ref State state)
    {
        // Decrement moved to EnterWindup (attack START, not this completion point) — see its own comment.
        // _target != null guard matters here — EnterWindup() silently no-ops on a null target (it needs one
        // to compute leapTarget), so without this check a target lost mid-chain left the enemy stuck forever
        // in Landing (leapsRemaining spiraling negative every tick, never actually transitioning out).
        // IsTargetStillValid — a target that went invisible-and-non-taunting since the LAST leap in this
        // chain landed shouldn't get chained into another one against them; see that property's own doc
        // comment. The leap that already resolved above is untouched either way.
        // !IsCrowdControlled — a true Stun() or Slow() landing mid-sequence never interrupts the leap
        // that's already resolved above, but stops the chain from continuing into another one, falling
        // through to PostLeap instead (see IsCrowdControlled's own doc comment).
        if (state.leapsRemaining > 0 && _target != null && IsTargetStillValid && !IsCrowdControlled)
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
        // leapsRemaining deliberately left untouched here — EnterWindup now owns resetting it
        // (refill-if-exhausted, not unconditional), so an attempt cancelled via Interrupt() (which funnels
        // through here) keeps whatever count it already had instead of getting silently topped back up.
        // Attack routine's over — reprioritize onto whoever's closest now, rather than sitting on whoever
        // this routine happened to start against. See EnemyBasePD.FindClosestTarget's own doc comment.
        FindClosestTarget();
        SetLeapState(ref state, LeapState.Moving);
    }

    // Resets the state machine back to Moving, same terminal transition the attack chain already uses when
    // it runs out of leapsRemaining naturally. See EnemyBasePD.Interrupt()'s own doc comment.
    protected override void OnInterrupted()
    {
        EnterMoving(ref currentState);

        // Predicted-instant hide, not left for UpdateView's own stateId-driven redraw to eventually catch
        // up — Interrupt() mutates currentState out-of-band (from a different identity's own Simulate(),
        // not this enemy's own HandleActiveState flow this tick), which was leaving the windup indicator
        // visibly stuck for a stray tick before UpdateView's viewState caught up to the interrupt. Also
        // re-parents back immediately (Windup unparents the indicator to world root — see UpdateView's own
        // reparenting block) since an interrupt only ever fires during Windup (Leaping isn't interruptible
        // per CanBeInterrupted below), so it's always unparented at this exact point.
        // _lastVisualState deliberately NOT written here — see SpearGloomPD.OnInterrupted's own comment for
        // why (UpdateView's own edge-detection bookkeeping, not ours to set).
        if (_windupIndicator != null)
        {
            _windupIndicator.SetActive(false);
            _windupIndicator.transform.SetParent(_windupIndicatorParent, true);
        }
        if (_windupFill != null) _windupFill.ResetFill();
    }

    // Mid-Leaping specifically is NOT interruptible (per design) — the leaper is already mid-air on a fixed
    // kinematic arc lerp with both its collider AND hurtbox disabled (see HandleActiveState's own comment),
    // so there's no sensible "cancel and snap back to idle" here even if something did reach in. Windup
    // (stationary, hittable) and every other phase are still fair game.
    protected override bool CanBeInterrupted => GetLeapState(ref currentState) != LeapState.Leaping;


    // ── View ───────────────────────────────────────────────────────────────
    private LeapState _lastVisualState = (LeapState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (!gameObject.activeInHierarchy) return;

        // Death is handled by base.UpdateView() above (OnDeathStop() + hierarchy.Delete()) — but that
        // doesn't stop execution from continuing into the rest of THIS method afterward. If death happens
        // while stateId still reads Windup/Leaping (e.g. killed by AoE/DoT mid-leap, never transitioning
        // back to Moving first), the SetActive(...) call further down would immediately re-enable the very
        // indicator OnDeathStop() just hid. Since the indicator is unparented to world root during that
        // window, it'd then sit there forever, detached and visible, with nothing left to ever hide it
        // again once this enemy itself is deleted/pooled — this was the "leftover circle on the ground"
        // bug. Bailing here preserves whatever OnDeathStop() already did.
        if (viewState.b.isDead) return;

        // Sourced from _leapTargetSnapshot (a plain field, not predicted State) — instantly available on
        // every machine the moment EnterWindup() decides it, with no network round-trip and no
        // interpolation blend (see the field's own doc comment). Re-applied every frame the state is
        // Windup/Leaping so the indicator always tracks whatever the current leapTarget is, rather than
        // snapshotting once on entry.
        var ss = (LeapState)viewState.stateId;
        if (_windupIndicator != null && (ss == LeapState.Windup || ss == LeapState.Leaping))
            _windupIndicator.transform.position = new Vector3(_leapTargetSnapshot.x, 0.05f, _leapTargetSnapshot.z);
        if (_windupIndicator != null) IndicatorMath.ApplyRadialTransform(_windupIndicator.transform, landRadius);

        // Purely cosmetic arc bob — safe off viewState (a transient misprediction here is an imperceptible
        // visual wobble, not a discrete value that needs to be exactly right).
        if (_yAxisMovers != null)
        {
            if (ss == LeapState.Leaping)
            {
                float t = 1f - Mathf.Clamp01(viewState.stateTimer / leapDuration);
                float arcY = Mathf.Sin(t * Mathf.PI) * leapArcHeight;
                _yAxisMovers.localPosition = _yAxisMoversRestPosition + new Vector3(0f, arcY, 0f);
            }
            else
            {
                _yAxisMovers.localPosition = _yAxisMoversRestPosition;
            }
        }

        // Live, every frame regardless of the _lastVisualState-gated block below — reacts immediately to
        // EffectiveAttackSpeed changing mid-state, same reasoning as every other attack-speed-scaled enemy
        // in this pass. The fill spans both Windup and Leaping as one combined span, driven off
        // attackElapsed (counts up continuously across the whole span, reset once at EnterWindup) rather
        // than stateTimer. stateTimer counts DOWN and gets given a brand new baseline the instant Windup
        // hands off to Leaping, so it means two different things on either side of that handoff — fine for
        // verified/simulated reads, but PurrDiction's viewState is interpolated between ticks for smooth
        // rendering, and blending "windup time remaining" against "leap time remaining" across that one
        // tick boundary produced a real, visible glitch (the fill briefly reading a wrong value) for
        // whichever single render frame straddled the transition — worst-case-visible exactly when
        // windupDuration == leapDuration, i.e. right at the midpoint. attackElapsed has one consistent
        // meaning for the WHOLE span, so interpolating it across that same boundary is safe.
        if (_windupFill != null)
        {
            float combined = windupDuration + leapDuration;
            if (ss == LeapState.Windup || ss == LeapState.Leaping)
                _windupFill.SetProgress(viewState.attackElapsed / combined);
        }
        if (_animator != null)
        {
            if (ss == LeapState.Windup)
                _animator.SetFloat(WindupSpeedHash, _windupClipRate * EffectiveAttackSpeed);
            else if (ss == LeapState.Leaping)
                _animator.SetFloat(LeapSpeedHash, _leapClipRate * EffectiveAttackSpeed);
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
        // Reset once the whole Windup+Leaping span is over (Leaping->Landing) — the live SetProgress block
        // above already covers driving it forward throughout both states, this just clears it back to 0 on
        // the way out (Landing/PostLeap/Moving all have nothing to show).
        if (_windupFill != null && !isWindupOrLeap) _windupFill.ResetFill();

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

    protected override void OnDeathStop()
    {
        if (_windupIndicator != null) _windupIndicator.SetActive(false);
        if (_windupFill != null)      _windupFill.ResetFill();
        // Snaps out of whatever attack pose this enemy died mid-animation in — see SpearGloomPD's own
        // OnDeathStop comment for why (stateId is frozen from here on, so nothing else will do this).
        // Guarded on activeInHierarchy — see SpearGloomPD's own comment on the same guard.
        if (_animator != null && gameObject.activeInHierarchy) _animator.Play(IdleHash, 0, 0f);
    }

    protected override void OnRespawnReset()
    {
        ApplyAnimatorClipSpeeds();
        currentState.stateId             = 0;
        currentState.leapsRemaining      = leapsPerAttack;
        currentState.attackCooldownTimer = attackCooldown + GameRandom.instance.NextFloat(0f, attackCooldown);
        currentState.stateTimer          = 0f;
        currentState.attackElapsed       = 0f;
        if (_yAxisMovers != null) _yAxisMovers.localPosition = _yAxisMoversRestPosition;
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
