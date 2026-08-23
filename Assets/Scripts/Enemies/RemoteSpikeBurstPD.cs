using PurrNet.Prediction;
using UnityEngine;

// A standalone ground hazard spawned by KingSpikePD's remote spike burst sequence — not IEnemyPD, not a
// weapon, just a short-lived spawned effect with a charge-then-detonate timer, same category as
// WeaponRangedProjectilePD ("deliberately not X"). Lives independently of the boss once spawned: it keeps
// ticking and will still detonate even if the boss that spawned it has since died.
public class RemoteSpikeBurstPD : PredictedIdentity<RemoteSpikeBurstPD.State>
{
    public struct State : IPredictedData<State>
    {
        public float chargeTimer;
        // Set once the charge timer expires; the actual hierarchy.Delete() is deferred to UpdateView() off
        // verified state, same pattern as WeaponRangedProjectilePD — deleting directly from LateSimulate()
        // would be acting on a not-yet-confirmed tick.
        public bool  expired;
        public void Dispose() { }
    }

    [SerializeField] private WindupFillPD _windupFill;

    private float _radius;
    private float _damage;
    // This life's own total charge duration, captured once at Launch() — the denominator for UpdateView's
    // own live SetProgress() ratio (see its own doc comment for why this replaced a self-timed StartFill()).
    private float _chargeDuration;

    // Called by KingSpikePD.SpawnRemoteBurstWave() immediately after hierarchy.Create() returns —
    // synchronous and deterministic, same "mutate currentState right after Create()" pattern as
    // XPOrbPD.SetValue()/WeaponRangedProjectilePD.Launch().
    public void Launch(float chargeTime, float radius, float damage)
    {
        _radius = radius;
        _damage = damage;
        _chargeDuration = chargeTime;
        currentState.chargeTimer = chargeTime;

        // A pooled instance's PREVIOUS life ends with the fill fully complete (that's what a finished
        // windup looks like right before detonation) — WindupFillPD's own Awake() (where it resets to
        // empty) never re-fires on reuse, only on true first creation. UpdateView's own SetProgress() call
        // waits on verified.HasValue (see its own doc comment) rather than firing near-instantly off
        // viewState, so without this explicit reset a reused instance would visibly sit fully-filled for a
        // moment before that first verified snapshot arrives and progress actually starts moving.
        if (_windupFill != null) _windupFill.ResetFill();
    }

    protected override State GetInitialState() => default;

    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        state.chargeTimer -= delta;
        if (state.chargeTimer > 0f) return;

        foreach (var stats in CharacterStatsPD.All.Values)
        {
            // No IsInvincible pre-check (2026-08-24 fix) — TakeDamage() already no-ops safely against
            // invincibility on its own, AND is the only place Dodge credit gets awarded (see
            // CharacterStatsPD.NotifyDodge's own doc comment); skipping the call here silently ate every
            // Dodge this attack should have granted while the player was dash-invincible.
            if (stats.IsDead) continue;
            if (Vector3.Distance(stats.transform.position, transform.position) <= _radius)
                stats.TakeDamage(_damage);
        }

        state.expired = true;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Re-applied every frame, not just once at Launch() — if _radius is ever changed mid-life, the
        // visible footprint (DangerIndicator + WindupFill, both children of this root, propagated down
        // via ordinary transform-hierarchy scaling) should never silently drift out of sync with the
        // actual AoE damage radius.
        IndicatorMath.ApplyRadialTransform(transform, _radius);

        // Live, every frame — see feedback_windupfill_self_timed_drift: a self-timed StartFill() runs on its
        // own Time.deltaTime clock, independent of the tick-based chargeTimer that actually gates the real
        // detonation in LateSimulate(), and can drift out of sync with it. SetProgress() instead reads the
        // live ratio directly, so it can't drift — it's a direct function of the same timer, not a second
        // independent countdown aimed at the same starting number.
        //
        // Gate reads verified (not viewState) — same reason as KingSpikePD's chargeDirection fix: viewState
        // linearly interpolates every STATE field by default, including plain floats like chargeTimer. Right
        // after Launch() sets currentState.chargeTimer to the real duration, viewState can still be blending
        // in from whatever this pooled instance's chargeTimer happened to be at the end of its PREVIOUS life
        // (typically near 0, since that's how the previous life expired) — which would make this ratio read
        // as "almost done" for a tick or two right after a fresh Launch(). verified is the exact,
        // non-interpolated snapshot, so it doesn't have that blending problem.
        // Gated on chargeTimer > 0, not just verified.HasValue — verified can legitimately turn non-null
        // for a tick or two before Launch()'s synchronous currentState.chargeTimer write has actually
        // propagated through to a verified snapshot, reading as the struct's default 0 in the meantime;
        // without this gate that transient would read as "already 100% done" instead of "not started yet".
        //
        // The actual RATIO, once gated open, reads viewState instead of verified — verified only updates as
        // often as a new snapshot gets confirmed, which reads noticeably steppier than viewState's own
        // every-render-frame interpolation. The blending risk above only exists in the brief window BEFORE
        // this gate opens (which is exactly what the gate is for); by the time verified confirms a real
        // positive chargeTimer, viewState's own interpolation has virtually always already caught up too
        // (same order-of-magnitude window), and Mathf.Clamp01 below floors/ceilings the rare stray frame
        // where it hasn't rather than showing something wildly wrong.
        if (_windupFill != null && verified.HasValue && verified.Value.chargeTimer > 0f)
            _windupFill.SetProgress(1f - Mathf.Clamp01(viewState.chargeTimer / _chargeDuration));

        // State-critical — only act on server-confirmed expiry, same reasoning as
        // WeaponRangedProjectilePD/EnemyBasePD's death handling.
        if (verified.HasValue && verified.Value.expired)
            hierarchy.Delete(this);
    }
}
