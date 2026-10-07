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

    // Exposed (2026-09-27) so sibling/child components can react to the real gameplay radius directly instead
    // of re-deriving it from transform.lossyScale — needed by anything that can't just ride the root's own
    // ApplyRadialTransform scaling for free, e.g. a ParticleSystem Shape module, which has its own Scaling
    // Mode setting that can deliberately ignore hierarchy scale (so growing the AoE doesn't also grow/speedup
    // individual particles).
    public float Radius => _radius;

    // Growth-only mutator (2026-10-05, ArrowRainParticleRadiusPD's own per-frame growth push) — stays a
    // dumb passthrough, same as everything else on this class: no growth concept of its own, just lets a
    // sibling that DOES own one (ArrowRainParticleRadiusPD) drive this root's radius directly. UpdateView
    // already re-applies ApplyRadialTransform(transform, _radius) every frame (see its own comment), so
    // this alone is enough to make the root/outline visually grow — no further wiring needed here.
    public void SetRadius(float newRadius) => _radius = newRadius;

    // Optional per-cast tint (2026-09-27, ArrowRainVFXPD's own outline/particle color-matching-caster need)
    // — null (default, every pre-existing caller) means "leave whatever color the prefab was authored with
    // alone," same opt-in shape TintColor's own consumers (CircleOutlinePD/ArrowRainParticleRadiusPD) already
    // check for before touching anything. Not State-backed — purely cosmetic, same category _radius already
    // is (a plain field, mutated once at Launch() and never again).
    private Color? _tintColor;
    public Color? TintColor => _tintColor;

    // Called immediately after hierarchy.Create() returns — same "mutate currentState right after Create()"
    // pattern as every other spawned effect in this codebase (RemoteSpikeBurstPD.Launch(), XPOrbPD.SetValue()).
    public void Launch(float radius, Color? tintColor = null)
    {
        _radius = radius;
        _tintColor = tintColor;
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
