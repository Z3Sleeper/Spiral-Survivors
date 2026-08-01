using PurrNet.Prediction;
using UnityEngine;

// Self-timed, radius-scaled visual-only effect — spawned by augments that want a "flat cylinder scaled to
// the effect's radius" indicator (e.g. DashHealAugment, DashExplosionAugment) without carrying any of the
// damage/collision logic a gameplay hazard like RemoteSpikeBurstPD would. Spawned via hierarchy.Create() so
// every client sees the same indicator at the same position and moment, even though its own "simulation" is
// nothing more than a countdown timer. Reuses EnemyBasePD's IndicatorMath.ApplyRadialTransform, the same
// flat-cylinder scaling helper RemoteSpikeBurstPD/KingSpikePD already use for their own AOE indicators.
public class RadiusIndicatorVFXPD : PredictedIdentity<RadiusIndicatorVFXPD.State>
{
    public struct State : IPredictedData<State>
    {
        public float timer;
        // Deferred hierarchy.Delete() to UpdateView() off verified state, same pattern as every other
        // spawned one-shot effect (RemoteSpikeBurstPD/WeaponRangedProjectilePD/PhantomBladePD).
        public bool  expired;
        public void Dispose() { }
    }

    // Self-contained, not caller-supplied — this VFX owns its own playback timing, same convention as any
    // other prefab-authored effect (particle systems/animations tune their own duration on the asset, not
    // via a gameplay parameter threaded in from whatever triggered them). Callers only ever supply what's
    // actually gameplay-derived: the radius.
    [SerializeField] private float duration = 0.1f;

    private float _radius;

    // Called immediately after hierarchy.Create() returns — same "mutate currentState right after Create()"
    // pattern as every other spawned effect in this codebase (RemoteSpikeBurstPD.Launch(), XPOrbPD.SetValue()).
    public void Launch(float radius)
    {
        _radius = radius;
        currentState.timer = duration;
        currentState.expired = false;
    }

    protected override State GetInitialState() => default;

    protected override void Simulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        state.timer -= delta;
        if (state.timer <= 0f) state.expired = true;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Scales the root transform directly — same as every existing AOE indicator (RemoteSpikeBurstPD/
        // KingSpikePD). Deliberately not routed through PredictedTransform's Graphics slot: some enemy
        // indicators (the line-shaped ones) are intentionally kept OUT of the scaled visuals subtree so they
        // don't get auto-scaled, meaning "what should scale" is a per-prefab authoring decision, not
        // something safe to infer from wherever Graphics happens to point. This VFX only ever has the one
        // Visuals child, so root-scaling here has the same effect either way. Re-applied every frame, not
        // just once at Launch() — if _radius were ever changed mid-life, the visible footprint should never
        // silently drift out of sync.
        IndicatorMath.ApplyRadialTransform(transform, _radius);

        // State-critical — only act on server-confirmed expiry, same reasoning as every other spawned
        // one-shot effect's deletion.
        if (verified.HasValue && verified.Value.expired)
            hierarchy.Delete(this);
    }
}
