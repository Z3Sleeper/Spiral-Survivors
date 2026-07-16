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
    private bool  _visualStarted;

    // Called by KingSpikePD.SpawnRemoteBurstWave() immediately after hierarchy.Create() returns —
    // synchronous and deterministic, same "mutate currentState right after Create()" pattern as
    // XPOrbPD.SetValue()/WeaponRangedProjectilePD.Launch().
    public void Launch(float chargeTime, float radius, float damage)
    {
        _radius = radius;
        _damage = damage;
        currentState.chargeTimer = chargeTime;
        // Reset here, not just declared false at the field — this GameObject can be a pooled instance
        // reused from a previous life (same PurrNet.Pooling mechanism EnemyBasePD relies on), and Launch()
        // is the one thing reliably called on every reuse, unlike a lifecycle hook.
        _visualStarted = false;

        // A pooled instance's PREVIOUS life ends with the fill fully complete (that's what a finished
        // windup looks like right before detonation) — WindupFillPD's own Awake() (where it resets to
        // empty) never re-fires on reuse, only on true first creation. StartFill() now waits on
        // verified.HasValue (see UpdateView()) rather than firing near-instantly off viewState, so without
        // this explicit reset a reused instance would visibly sit fully-filled for a moment before that
        // first verified snapshot arrives and finally calls StartFill().
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
            if (stats.IsDead || stats.IsInvincible) continue;
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

        // Purely cosmetic, render rate — start the fill exactly once per life (guards against re-starting
        // it every frame, and against a pooled reuse inheriting a previous life's _visualStarted).
        //
        // Sourced from verified, not viewState — same reason as KingSpikePD's chargeDirection fix:
        // viewState linearly interpolates every STATE field by default, including plain floats like
        // chargeTimer. Right after Launch() sets currentState.chargeTimer to the real duration, viewState
        // can still be blending in from whatever this pooled instance's chargeTimer happened to be at the
        // end of its PREVIOUS life (typically near 0, since that's how the previous life expired) —
        // handing StartFill() a too-short blended duration, so the fill races to full almost immediately
        // and then just sits there until the real (un-interpolated) detonation timer in LateSimulate()
        // actually expires. verified is the exact, non-interpolated snapshot.
        // Gated on chargeTimer > 0, not just verified.HasValue — verified can legitimately turn non-null
        // for a tick or two before Launch()'s synchronous currentState.chargeTimer write has actually
        // propagated through to a verified snapshot, reading as the struct's default 0 in the meantime.
        // The old fallback (starting a near-instant 0.01s fill whenever chargeTimer read <= 0) was meant
        // for a genuine "already expired" edge case, but it was actually eating this default-0 transient
        // every time, which is why the fill always looked instantly full instead of counting up — waiting
        // for an actual positive value instead means it only ever starts against a real duration.
        if (!_visualStarted && _windupFill != null && verified.HasValue && verified.Value.chargeTimer > 0f)
        {
            _windupFill.StartFill(verified.Value.chargeTimer);
            _visualStarted = true;
        }

        // State-critical — only act on server-confirmed expiry, same reasoning as
        // WeaponRangedProjectilePD/EnemyBasePD's death handling.
        if (verified.HasValue && verified.Value.expired)
            hierarchy.Delete(this);
    }
}
