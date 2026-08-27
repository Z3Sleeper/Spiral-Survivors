using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// A standalone ground hazard spawned by MortarGloomPD at Windup entry — not IEnemyPD, not a weapon, same
// category as RemoteSpikeBurstPD ("deliberately not X"). Lives independently of the Mortar once spawned: it
// keeps ticking and will still detonate even if the Mortar that spawned it has since died, EXCEPT while still
// mid-Windup, where MortarGloomPD.OnInterrupted() can actively Cancel() it (see that class's own doc comment
// for why cancellation is phase-gated rather than blanket-never).
//
// Also spawns the cosmetic MortarProjectileVFXPD shell itself now (2026-08-26, was MortarGloomPD.EnterFire())
// — right at the "Attached" -> "Fired" lock transition below, driven by this hazard's own independent timer.
// The old approach spawned the shell from the MORTAR's own state-machine transition, which only runs while
// the Mortar is alive and still ticking HandleActiveState() — if the Mortar died mid-Windup, this hazard (per
// the independence described above) would still lock and deal damage on schedule, but the Mortar's own
// EnterFire() would never fire, so the cosmetic shell silently never spawned even though the hit landed.
// Tying the spawn to this hazard's own lock instead gives it the exact same "keeps going regardless of the
// firer's fate" guarantee the damage already has — see SpawnProjectileVfx's own doc comment for the position
// math, which uses _firerPosition (a snapshot) instead of the Mortar's live transform for the same reason.
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
        // Current tracked bearing (degrees, Atan2(z,x) convention — 0 = +X, counter-clockwise), only ever
        // meaningful while !locked. CompressedFloat since this changes virtually every tick during the
        // "attached" phase — same bandwidth reasoning SpikeShieldPD.State.facingAngle already uses for its
        // own identical continuously-changing angle. Needs to live in predicted State (unlike this hazard's
        // OWN transform, which is safely NOT State-backed) specifically because the 2026-08-21 rate cap makes
        // this genuinely incremental — MoveTowardsAngle reads its own PREVIOUS value every tick, which a
        // rollback replay must restore correctly, unlike the old snap-straight-to-target write (a pure
        // function of only currently-live inputs, safe without State).
        public CompressedFloat facingAngleDeg;
        // Current tracked DISTANCE from the firer (2026-08-23) — same "changes virtually every tick during
        // the 'attached' phase" bandwidth reasoning facingAngleDeg above already has, and the same
        // State-backed requirement (MoveTowards reads its own previous value every tick, which a rollback
        // replay must restore correctly). See State.facingAngleDeg's own doc comment for the shared shape;
        // this is the radial component to that field's angular one — together they fully rate-cap this
        // hazard's tracked position instead of only its bearing.
        public CompressedFloat trackedDistance;
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
    // Max bearing turn rate while tracking (2026-08-21) — a one-time snapshot of MortarGloomPD's own already
    // tier-scaled rotationRateDegPerSecond (itself copied from ShielderGloomPD's identical field, 45 base /
    // 0.2 tier growth), passed in at Launch() same as windupAttackSpeed and for the identical "firer could
    // die/get pooled-and-reused mid-track" reason. Caps how fast the tracked bearing can swing so circling
    // the Mortar (or timing a dash to arrive late in the windup) is a real counterplay option — an unbounded
    // instant snap (the old behavior) meant no amount of player movement could ever throw off where the
    // shell eventually lands.
    private float _rotationRateDegPerSecond = 45f;
    // Max rate the tracked DISTANCE (from the firer) can close/open during the "attached" phase (2026-08-23)
    // — same one-time-snapshot-at-Launch() shape as _rotationRateDegPerSecond above, for the identical
    // "firer could die/get pooled-and-reused mid-track" reason. Distance used to snap instantly to the
    // target's live range every tick — only the angular swing was ever capped — see LateSimulate's own
    // comment.
    private float _distanceTrackingRateUnitsPerSecond = 10f;

    // Cosmetic shell spawn info — all one-time snapshots at Launch(), same "firer could die/get pooled-and-
    // reused mid-track" reasoning as the tracking-rate fields above. size/tierAccentMaterial in particular
    // used to be read live off the Mortar at Fire-time (MortarGloomPD.size/TierAccentMaterial); reading them
    // NOW, at spawn, instead means this hazard never needs the Mortar to still exist by the time it locks.
    private GameObject _projectileVfxPrefab;
    private float _projectileArcHeight;
    private float _projectileSpawnHeightOffset;
    private float _projectileSpawnForwardOffset;
    private float _size = 1f;
    private Material _tierAccentMaterial;

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
        CharacterStatsPD trackedTarget = null, Vector3 firerPosition = default, float windupAttackSpeed = 1f,
        float rotationRateDegPerSecond = 45f, float distanceTrackingRateUnitsPerSecond = 10f,
        GameObject projectileVfxPrefab = null, float projectileArcHeight = 0f,
        float projectileSpawnHeightOffset = 0f, float projectileSpawnForwardOffset = 0f,
        float size = 1f, Material tierAccentMaterial = null)
    {
        _halfWidth = width * 0.5f;
        _halfDepth = depth * 0.5f;
        _damage    = damage;
        _windupDuration = windupDuration;
        _fireDuration   = fireDuration;
        _windupAttackSpeed = Mathf.Max(0.0001f, windupAttackSpeed);
        _rotationRateDegPerSecond = rotationRateDegPerSecond;
        _distanceTrackingRateUnitsPerSecond = distanceTrackingRateUnitsPerSecond;
        _projectileVfxPrefab = projectileVfxPrefab;
        _projectileArcHeight = projectileArcHeight;
        _projectileSpawnHeightOffset  = projectileSpawnHeightOffset;
        _projectileSpawnForwardOffset = projectileSpawnForwardOffset;
        _size = size;
        _tierAccentMaterial = tierAccentMaterial;

        currentState.windupTimer = windupDuration;
        currentState.fireTimer   = fireDuration;
        currentState.locked      = false;

        _trackedTarget = trackedTarget;
        _firerPosition = firerPosition;

        // Seeds the tracked bearing AND distance to match wherever the caller already placed this hazard for
        // its first frame (SpawnHazard's own hierarchy.Create() call) — same Atan2(z,x) convention
        // LateSimulate's own tracking uses, so the first real tick's MoveTowardsAngle/MoveTowards starts from
        // the correct current bearing/range instead of a spurious jump from 0.
        if (trackedTarget != null)
        {
            Vector3 initialTargetPos = trackedTarget.transform.position;
            initialTargetPos.y = 0f;
            Vector3 toTarget = initialTargetPos - firerPosition;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.0001f)
                currentState.facingAngleDeg = Mathf.Atan2(toTarget.z, toTarget.x) * Mathf.Rad2Deg;
            currentState.trackedDistance = toTarget.magnitude;
        }

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
            // "Attached" phase — keep tracking the committed target's live BEARING (angle from the firer) AND
            // DISTANCE, both rate-capped (2026-08-21 for bearing; 2026-08-23 for distance — was an instant
            // snap to the target's live range every tick, only the angular swing was ever capped) at
            // _rotationRateDegPerSecond/_distanceTrackingRateUnitsPerSecond respectively — mirrors
            // BlitzGloomPD/KingSpikePD's own rate-capped facing, just reinterpreted for a firer-relative
            // ground hazard's full 2D position instead of just a body's own facing direction. Holds its last
            // position/bearing/distance (no teleport-to-stale-null) if the target's died/gone invisible-
            // without-taunting mid-Windup — same "freeze on invalid" convention EnemyBasePD.IsTargetStillValid's
            // own callers already use.
            if (_trackedTarget != null && !_trackedTarget.IsDead &&
                (!_trackedTarget.IsInvisible || _trackedTarget.IsTaunting))
            {
                Vector3 targetPos = _trackedTarget.transform.position;
                targetPos.y = 0f;
                Vector3 toTarget = targetPos - _firerPosition;
                toTarget.y = 0f;
                float targetDistance = toTarget.magnitude;
                if (targetDistance > 0.0001f)
                {
                    float targetAngle = Mathf.Atan2(toTarget.z, toTarget.x) * Mathf.Rad2Deg;
                    state.facingAngleDeg = Mathf.MoveTowardsAngle(state.facingAngleDeg, targetAngle, _rotationRateDegPerSecond * delta);
                    state.trackedDistance = Mathf.MoveTowards(state.trackedDistance, targetDistance, _distanceTrackingRateUnitsPerSecond * delta);

                    float rad = state.facingAngleDeg * Mathf.Deg2Rad;
                    Vector3 dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                    transform.SetPositionAndRotation(_firerPosition + dir * state.trackedDistance, Quaternion.LookRotation(dir));
                }
            }

            state.windupTimer -= delta * _windupAttackSpeed;
            if (state.windupTimer > 0f) return;
            state.locked = true; // permanently freezes tracking from here on — see class doc comment
            SpawnProjectileVfx(); // right at the lock transition — see class doc comment for why here
        }

        // "Fired" phase — always raw, unscaled delta, per explicit design (see class doc comment).
        state.fireTimer -= delta;
        if (state.fireTimer > 0f) return;

        Vector3 half = new Vector3(_halfWidth, 2f, _halfDepth); // tall enough to catch any player hurtbox
        int n = Physics.OverlapBoxNonAlloc(transform.position, half, _hitResults, transform.rotation, CombatLayerMasks.PlayerHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _hitResults[i].GetComponent<CharacterStatsPD>();
            // No IsInvincible pre-check (2026-08-24 fix, was skipping the call entirely while invincible) —
            // TakeDamage() already no-ops safely against invincibility on its own, AND is the only place
            // Dodge credit gets awarded (see CharacterStatsPD.NotifyDodge's own doc comment); short-circuiting
            // here before ever calling it silently ate every Dodge this attack should have granted while the
            // player was dash-invincible.
            if (stats != null && !stats.IsDead) stats.TakeDamage(_damage);
        }
        state.expired = true;
    }

    // Spawns the cosmetic MortarProjectileVFXPD shell — moved here from MortarGloomPD.EnterFire() (2026-08-26,
    // see class doc comment for why) so it fires off THIS hazard's own lock transition instead of the firing
    // Mortar's own (possibly-frozen-by-death) state machine. Position math ported as-is from the old
    // MortarGloomPD.SpawnProjectileVfx(), just reading _firerPosition (this hazard's own one-time snapshot of
    // where the Mortar stood at spawn) instead of the Mortar's live transform.position, and this hazard's own
    // (now-locked) transform.position as the landing point instead of a separate hazardGo lookup — this
    // hazard doesn't need to ask "where did my own hazard land", it just IS the hazard.
    private void SpawnProjectileVfx()
    {
        if (_projectileVfxPrefab == null) return;

        Vector3 targetPos = transform.position; // already locked at this exact point
        targetPos.y = 0f;
        Vector3 fireDir = targetPos - _firerPosition;
        fireDir.y = 0f;
        // Degenerate fallback (target position coincides with the firer's) — this hazard's own forward is
        // already meaningfully oriented toward its landing point (see Launch()'s own Quaternion.LookRotation),
        // same role transform.forward played on the old Mortar-side version.
        if (fireDir.sqrMagnitude < 0.0001f) fireDir = transform.forward;
        fireDir.Normalize();
        Vector3 spawnPos = _firerPosition + Vector3.up * (_projectileSpawnHeightOffset * _size) + fireDir * (_projectileSpawnForwardOffset * _size);

        var id = hierarchy.Create(_projectileVfxPrefab, spawnPos, Quaternion.LookRotation(fireDir));
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out MortarProjectileVFXPD vfx))
            vfx.Launch(spawnPos, targetPos, _fireDuration, _projectileArcHeight, _size, _tierAccentMaterial);
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
