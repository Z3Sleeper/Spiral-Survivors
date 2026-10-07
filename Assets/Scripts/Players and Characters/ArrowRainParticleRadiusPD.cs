using UnityEngine;

// Keeps ArrowRainVFXPD's raining-particle emitter's spawn AREA matching the real gameplay radius, without
// touching individual particle size/speed. The ParticleSystem's own Scaling Mode is deliberately left at
// Local (ignores hierarchy scale entirely) — Hierarchy mode would scale everything proportionally, so a wider
// AreaEffectSize would also make each arrow bigger/faster, which isn't wanted. Reads RadiusIndicatorVFXPD's
// exposed Radius directly (the same value ApplyRadialTransform scales the root/outline with) rather than
// re-deriving it from transform.lossyScale.
//
// One-shot in Start(), not a per-frame poll — _radius is set once by Launch() and never changes for the rest
// of this instance's life (no augment retroactively resizes an already-spawned cast). Start() is safe timing:
// Launch() always runs synchronously right after hierarchy.Create() returns (same convention as every other
// spawned effect here), which completes well before Unity's own deferred Start() callback fires.
//
// Also owns this particle system's pause handling (2026-09-27) — PausePD never touches Time.timeScale (see
// its own doc comment), so a native ParticleSystem has zero built-in pause awareness and just keeps animating
// in real time straight through a pause. Same poll-and-edge-detect Pause(true)/Play(true) convention
// VFXManager.PooledVfx.UpdatePause already uses for its own pooled effects — this particle system isn't
// pooled/managed by VFXManager (it's a real per-cast instance owned by this hazard's own PredictedIdentity,
// not a purely local cosmetic pool), so it needs its own copy of that same polling rather than being folded
// into that manager.
[RequireComponent(typeof(ParticleSystem))]
public class ArrowRainParticleRadiusPD : MonoBehaviour
{
    [SerializeField] private RadiusIndicatorVFXPD radiusSource;

    private ParticleSystem _ps;
    private bool _lastPaused;

    // Set by AreaEffectCastHelper.SpawnRadiusVFX (2026-10-05, ArcherArrowRainAbilityPD's own duration/
    // tickInterval rework), called synchronously right after hierarchy.Create() — same timing as
    // RadiusIndicatorVFXPD.Launch() itself, well before Unity's own deferred Start() callback fires, so
    // this just caches the raw value for Start() to actually act on. Deliberately NOT routed through
    // RadiusIndicatorVFXPD (which stays a dumb, reusable "radius + tint" passthrough for every OTHER AoE
    // VFX caller) — this is purely an ArrowRain-specific concept (the raining-particle emitter's own
    // duration/startLifetime shape), so it lives entirely in this ArrowRain-specific script instead.
    private float? _abilityDuration;

    // Called once, right after hierarchy.Create() — see _abilityDuration's own doc comment for the timing
    // reasoning. Stuart's own framing: "tell the VFX its duration and the VFX will account for it."
    public void SetAbilityDuration(float duration) => _abilityDuration = duration;

    // Growth support (2026-10-05, ArcherArrowRainGrowthAugment). Same one-shot-cache-then-act-in-
    // Start()/Update() shape as _abilityDuration above — set once, right after hierarchy.Create(), well
    // before Start() fires. Deliberately a CONSTANT per-second rate applied every real frame (Update()),
    // not stepped to match the real gameplay radius's own discrete per-Simulate-tick growth
    // (AreaEffectPD.SetRadius) — Stuart's own explicit framing: "have the outline and particle system grow
    // over time at a constant rate while the actual gameplay tick rate stuff under the hood is actually
    // doing it in incrementally larger radii. Very tiny visual to gameplay mismatch that should be
    // unnoticeable." _startRadius is captured in Start() (radiusSource.Radius, same value the shape.radius
    // assignment right above it uses), so growth always starts from whatever the real cast radius was,
    // never a hardcoded baseline.
    private float? _growthPerSecond;
    private float _startRadius;
    private float _elapsedSinceStart;

    public void SetGrowth(float growthPerSecond) => _growthPerSecond = growthPerSecond;

    private void Start()
    {
        _ps = GetComponent<ParticleSystem>();
        if (radiusSource == null) return;

        var shape = _ps.shape;
        _startRadius = radiusSource.Radius;
        shape.radius = _startRadius;

        // Keeps the authored "falling particle" shape (duration: 4.5, start lifetime: 0.5, emulating a 5s
        // rain) intact for WHATEVER the real ability duration turns out to be — the particle system's own
        // duration becomes (ability duration - its own already-authored startLifetime), read live off
        // main.startLifetime itself rather than a hardcoded 0.5, so this stays correct even if that's ever
        // retuned on the asset. Net effect: the system stops EMITTING with exactly enough time left for the
        // very last particle's own lifetime to run out right as the ability's duration ends. Assumes
        // startLifetime is authored in Constant mode (a single number, per Stuart's own "start lifetime:
        // 0.5" framing) — .constant reads that value directly; a Curve/Random mode would need a different
        // read, not needed here.
        if (_abilityDuration.HasValue)
        {
            var main = _ps.main;
            main.duration = Mathf.Max(0f, _abilityDuration.Value - main.startLifetime.constant);
        }

        // RGB-only tint, alpha keys preserved — same "keep whatever fade was authored, only touch color"
        // convention VFXManager.PooledVfx.Play(Vector3, Color, Color, float) already uses for the shockwave
        // pool. Falls back to a plain opaque->transparent fade if this prefab's own Color over Lifetime isn't
        // already in single-Gradient mode, so this never throws.
        if (radiusSource.TintColor.HasValue)
        {
            var col = _ps.colorOverLifetime;
            var alphaKeys = (col.enabled && col.color.mode == ParticleSystemGradientMode.Gradient && col.color.gradient != null)
                ? col.color.gradient.alphaKeys
                : new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) };

            Color tint = radiusSource.TintColor.Value;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(tint, 0f), new GradientColorKey(tint, 1f) }, alphaKeys);
            col.color = gradient;
        }
    }

    private void Update()
    {
        bool paused = PausePD.instance != null && PausePD.instance.IsPaused;
        if (paused != _lastPaused)
        {
            _lastPaused = paused;
            if (paused) { if (_ps.isPlaying) _ps.Pause(true); }
            else        { if (_ps.isPaused)  _ps.Play(true); }
        }

        if (!_growthPerSecond.HasValue || paused) return;

        // Capped at _abilityDuration (falls back to no cap if that was never set) so a leftover frame or
        // two after the ability ends can't keep growing the radius past what the gameplay side ever reaches.
        float cap = _abilityDuration ?? float.MaxValue;
        if (_elapsedSinceStart >= cap) return;

        _elapsedSinceStart = Mathf.Min(cap, _elapsedSinceStart + Time.deltaTime);
        float currentRadius = _startRadius + _growthPerSecond.Value * _elapsedSinceStart;

        var shape = _ps.shape;
        shape.radius = currentRadius;
        if (radiusSource != null) radiusSource.SetRadius(currentRadius);
    }
}
