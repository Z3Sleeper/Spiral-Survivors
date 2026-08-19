using PurrNet.Prediction;
using UnityEngine;

// A standalone ground hazard spawned by MortarGloomPD at Windup entry — not IEnemyPD, not a weapon, same
// category as RemoteSpikeBurstPD ("deliberately not X"). Lives independently of the Mortar once spawned: it
// keeps ticking and will still detonate even if the Mortar that spawned it has since died, EXCEPT while still
// mid-Windup, where MortarGloomPD.OnInterrupted() can actively Cancel() it (see that class's own doc comment
// for why cancellation is phase-gated rather than blanket-never).
//
// Two explicit, separately-paced phases (2026-08-21, replacing a single combined chargeTimer) — matches the
// user's own "attached" vs. "fired" framing:
// 1. "Attached" (windupTimer, !locked): live-tracks the committed target's position, same "follow the player
//    during the telegraph, then commit" feel KingOrbiterPD's own Shoot-phase indicators already have (which is
//    what this was modeled after) — scaled by windupAttackSpeed, a ONE-TIME snapshot of the firer's own
//    EffectiveAttackSpeed taken at Launch(). This is what keeps this phase's real-world duration matching
//    MortarGloomPD's own attack-speed-scaled Windup phase (state.mortarStateTimer -= delta * EffectiveAttackSpeed)
//    instead of drifting out of sync with it — a plain unscaled timer here would either still be tracking a
//    beat after Mortar's own Fire had already begun (fast Mortar) or lock early and sit frozen for the rest of
//    an artificially-stretched Windup (slow Mortar). Snapshotted rather than read live from a stored
//    MortarGloomPD reference on purpose: per this class's own "lives independently, can outlive its firer"
//    design above, the firer could die/get pooled-and-reused for an unrelated future life while this hazard is
//    still counting down, and a live reference would then silently read that UNRELATED life's attack speed.
// 2. "Fired" (fireTimer, locked): always raw, unscaled delta regardless of attack speed, per explicit design —
//    matches the cosmetic shell's own (also unscaled) flight time. Position is frozen the instant this phase
//    begins; nothing in this phase ever touches transform again.
//
// Reads _trackedTarget's live position from ITS OWN LateSimulate() (same established "cross-identity
// Simulate()-context read is safe/deterministic" pattern SpikeShieldPD's own _owner.Target read already uses)
// and writes only its OWN transform — same "own-identity self-write, own LateSimulate()" shape
// SpikeShieldPD.WriteTransform already uses, not a cross-identity write from the caller's side.
public class MortarHazardPD : PredictedIdentity<MortarHazardPD.State>
{
    public struct State : IPredictedData<State>
    {
        // True once the "attached" (windup/tracking) phase has fully elapsed — from here on only fireTimer
        // ticks, and position is permanently frozen. See class doc comment for the two-phase split.
        public bool  locked;
        public float windupTimer;
        public float fireTimer;
        public bool  expired;
        // Set only via Cancel() (MortarGloomPD.OnInterrupted, Windup-phase only) — distinguishes "shut down
        // early, no damage" from a normal timer-reaches-0 expiry, both of which route through the same
        // `expired` flag for UpdateView()'s existing delete-on-expiry path.
        public bool  cancelled;
        public void Dispose() { }
    }

    [SerializeField] private WindupFillPD _windupFill;

    private static readonly Collider[] _hitResults = new Collider[8]; // hard cap of 4 players, generous margin

    private float _halfWidth;
    private float _halfDepth;
    private float _damage;
    // This life's own total phase durations, captured once at Launch() — the denominators for
    // UpdateView's own live SetProgress() ratio (see its own doc comment for why this replaced a self-timed
    // StartFill()) and for LateSimulate's own elapsed-time-into-each-phase math.
    private float _windupDuration;
    private float _fireDuration;
    // One-time snapshot of the firer's own EffectiveAttackSpeed at Launch() — see class doc comment for why
    // this is scaled-once, not read live.
    private float _windupAttackSpeed = 1f;

    // Live-tracking inputs (2026-08-21) — see class doc comment. _trackedTarget is the SPECIFIC player this
    // strike committed to (captured by the caller at spawn time, immune to MortarGloomPD's own _targetStats
    // possibly being reassigned to someone else mid-Windup — same "commit once, keep reading that same
    // reference" contract EnemyBasePD.CaptureAttackAnchor already guarantees for the plain-Vector3 case).
    // _firerPosition is a one-time snapshot of the Mortar's own position at spawn (it's kinematic and doesn't
    // move again until well after this hazard has locked, so a live re-read is never actually needed).
    private CharacterStatsPD _trackedTarget;
    private Vector3 _firerPosition;

    // Called immediately after hierarchy.Create() returns — same "mutate currentState right after Create()"
    // pattern as RemoteSpikeBurstPD.Launch(). Takes full width/depth (not half-extents) — friendlier to tune
    // from the Inspector-facing caller; halved here into the half-extents Physics.OverlapBoxNonAlloc actually
    // wants. trackedTarget/firerPosition/windupAttackSpeed drive the live-tracking-then-lock behavior — see
    // class doc comment; trackedTarget may be null (no live tracking, stays exactly where spawned).
    public void Launch(float windupDuration, float fireDuration, float width, float depth, float damage,
        CharacterStatsPD trackedTarget = null, Vector3 firerPosition = default, float windupAttackSpeed = 1f)
    {
        _halfWidth = width * 0.5f;
        _halfDepth = depth * 0.5f;
        _damage    = damage;
        _windupDuration = windupDuration;
        _fireDuration   = fireDuration;
        _windupAttackSpeed = Mathf.Max(0.0001f, windupAttackSpeed);

        currentState.windupTimer = windupDuration;
        currentState.fireTimer   = fireDuration;
        currentState.locked      = false;

        _trackedTarget = trackedTarget;
        _firerPosition = firerPosition;

        // Pooled-reuse safety — same reasoning as RemoteSpikeBurstPD.Launch()'s own explicit ResetFill() call.
        if (_windupFill != null) _windupFill.ResetFill();

        // Scales this hazard's own root — DangerIndicator and WindupFill both live as children underneath it
        // and inherit the non-uniform width/depth for free via ordinary transform-hierarchy propagation, same
        // convention RemoteSpikeBurstPD.UpdateView uses (IndicatorMath.ApplyRadialTransform(transform, ...)),
        // just non-uniform (width != depth) instead of a single radius. No direct DangerIndicator reference
        // needed — matches every other windup-hazard prefab in this codebase, which only ever slots in
        // WindupFill (and occasionally a WindupIndicator wrapper), never DangerIndicator itself.
        // *0.1f — same compensation IndicatorMath.ApplyLineTransform's own "Z scale is 10% of distance" uses:
        // Unity's built-in Plane primitive is 10x10 world units at scale 1 (unlike a Quad, which is 1x1), so
        // a DangerIndicator/WindupFill authored as a Plane needs this ratio to make localScale units actually
        // correspond to world units.
        var rootScale = transform.localScale;
        transform.localScale = new Vector3(width * 0.1f, rootScale.y, depth * 0.1f);
    }

    protected override State GetInitialState() => default;

    protected override void Simulate(ref State state, float delta) { }

    // Called from MortarGloomPD.OnInterrupted() (Windup-phase only) — cross-identity predicted-state
    // mutation from within the caller's own Simulate() context, same established-safe pattern
    // SetMovementDisabled/Stun/Slow/Interrupt already use elsewhere in this codebase.
    public void Cancel() => currentState.cancelled = true;

    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (state.cancelled) { state.expired = true; return; } // no damage, no timer countdown — just expire
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        if (!state.locked)
        {
            // "Attached" phase — keep tracking the committed target's live position. Holds its last position
            // (no teleport-to-stale-null) if the target's died/gone invisible-without-taunting mid-Windup —
            // same "freeze on invalid" convention EnemyBasePD.IsTargetStillValid's own callers already use.
            if (_trackedTarget != null && !_trackedTarget.IsDead &&
                (!_trackedTarget.IsInvisible || _trackedTarget.IsTaunting))
            {
                Vector3 targetPos = _trackedTarget.transform.position;
                targetPos.y = 0f;
                Vector3 fireDir = targetPos - _firerPosition;
                fireDir.y = 0f;
                if (fireDir.sqrMagnitude > 0.0001f)
                    transform.SetPositionAndRotation(targetPos, Quaternion.LookRotation(fireDir.normalized));
            }

            state.windupTimer -= delta * _windupAttackSpeed;
            if (state.windupTimer > 0f) return;
            state.locked = true; // permanently freezes tracking from here on — see class doc comment
        }

        // "Fired" phase — always raw, unscaled delta, per explicit design (see class doc comment).
        state.fireTimer -= delta;
        if (state.fireTimer > 0f) return;

        Vector3 half = new Vector3(_halfWidth, 2f, _halfDepth); // tall enough to catch any player hurtbox
        int n = Physics.OverlapBoxNonAlloc(transform.position, half, _hitResults, transform.rotation, CombatLayerMasks.PlayerHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _hitResults[i].GetComponent<CharacterStatsPD>();
            if (stats != null && !stats.IsDead && !stats.IsInvincible) stats.TakeDamage(_damage);
        }
        state.expired = true;
    }

    // Combined 0-1 progress across BOTH phases (elapsed time in each, not clock time — a scaled-down/up
    // windupAttackSpeed changes how much REAL time the "attached" phase takes but never changes what fraction
    // of the whole sequence it represents at the moment it completes).
    private float ComputeProgress(State s)
    {
        float total = _windupDuration + _fireDuration;
        if (total <= 0f) return 1f;
        float elapsedWindup = _windupDuration - Mathf.Max(0f, s.windupTimer);
        float elapsedFire   = s.locked ? _fireDuration - Mathf.Max(0f, s.fireTimer) : 0f;
        return Mathf.Clamp01((elapsedWindup + elapsedFire) / total);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Live, every frame — same feedback_windupfill_self_timed_drift fix as RemoteSpikeBurstPD.UpdateView.
        // Gate reads verified (avoids a stale pooled-reuse timer value reading as "almost done" right after a
        // fresh Launch()); the actual ratio reads viewState once that gate is open, for smoother
        // every-render-frame updates than verified alone would give — see RemoteSpikeBurstPD's own doc
        // comment for the full reasoning on why that's safe.
        if (_windupFill != null && verified.HasValue && (verified.Value.windupTimer > 0f || verified.Value.fireTimer > 0f))
            _windupFill.SetProgress(ComputeProgress(viewState));

        // State-critical — only act on server-confirmed expiry, same reasoning as every other spawned
        // one-shot effect's deletion. Covers both a real timeout and an early Cancel() the same way.
        if (verified.HasValue && verified.Value.expired)
            hierarchy.Delete(this);
    }
}
