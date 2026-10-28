using UnityEngine;

// Keeps ArrowRainVFXPD's raining-particle emitter's spawn AREA matching the real gameplay radius, without
// touching individual particle size/speed. The ParticleSystem's own Scaling Mode is deliberately left at
// Local (ignores hierarchy scale entirely) — Hierarchy mode would scale everything proportionally, so a wider
// AreaEffectSize would also make each arrow bigger/faster, which isn't wanted. Reads RadiusIndicatorVFXPD's
// exposed Radius directly (the same value ApplyRadialTransform scales the root/outline with) rather than
// re-deriving it from transform.lossyScale.
//
// Real per-cast setup happens in Init(), called every cast from AreaEffectCastHelper.SpawnRadiusVFX right
// after hierarchy.Create() returns — NOT Start(), which only fires once per real object instance and never
// re-fires on this prefab's own pooled reuse (see Init()'s own doc comment for the bug that caused).
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
    // tickInterval rework), called synchronously right after hierarchy.Create(), right before Init() (see
    // its own doc comment — NOT Start(), which doesn't re-fire on a pooled reuse) actually acts on this.
    // Deliberately NOT routed through RadiusIndicatorVFXPD (which stays a dumb, reusable "radius + tint"
    // passthrough for every OTHER AoE VFX caller) — this is purely an ArrowRain-specific concept (the
    // raining-particle emitter's own duration/startLifetime shape), so it lives entirely in this
    // ArrowRain-specific script instead.
    private float? _abilityDuration;

    // Called every cast, right after hierarchy.Create() — see _abilityDuration's own doc comment for the
    // timing reasoning. Design intent: "tell the VFX its duration and the VFX will account for it."
    // Nullable (2026-10-11 fix) — see SetGrowth's own doc comment for why a pooled reuse needs this to be
    // able to explicitly clear, not just set.
    public void SetAbilityDuration(float? duration) => _abilityDuration = duration;

    // Growth support (2026-10-05, ArcherArrowRainGrowthAugment). Deliberately a CONSTANT per-second rate
    // applied every real frame (Update()), not stepped to match the real gameplay radius's own discrete
    // per-Simulate-tick growth (AreaEffectPD.SetRadius) — explicit design: "have the outline
    // and particle system grow over time at a constant rate while the actual gameplay tick rate stuff under
    // the hood is actually doing it in incrementally larger radii. Very tiny visual to gameplay mismatch that
    // should be unnoticeable." _startRadius is captured in Init() (radiusSource.Radius, same value the
    // shape.radius assignment right above it uses), so growth always starts from whatever the real cast
    // radius was, never a hardcoded baseline.
    private float? _growthPerSecond;
    private float _startRadius;
    private float _elapsedSinceStart;

    // Nullable (2026-10-11 fix) — both this and SetAbilityDuration above must be able to explicitly CLEAR a
    // previous cast's value, not just set a new one, now that Init() (not Start()) is the one being called
    // every real cast — see Init()'s own doc comment for why that distinction matters.
    public void SetGrowth(float? growthPerSecond) => _growthPerSecond = growthPerSecond;

    // Real per-cast initialization (2026-10-11 fix, replaces the old Start()-based version) — called every
    // cast, from AreaEffectCastHelper.SpawnRadiusVFX right after SetAbilityDuration/SetGrowth, same as every
    // other per-cast setter on this class. Start() was WRONG for this: these spawned VFX prefabs are pooled
    // and reused across casts (see [[feedback_pooled_lazy_init_pattern]]), and Unity's Start() only ever
    // fires once per real object instance, never again on a pooled reuse — so every cast after the VFX
    // instance's very first ever use would keep _elapsedSinceStart stuck at whatever the PREVIOUS cast's
    // growth left it at (already at/past that cast's own duration), silently skipping all growth from frame
    // one, while the real gameplay hitbox (driven through Simulate(), which the PD framework DOES reliably
    // re-run every cast) kept growing and dealing damage well outside the frozen-looking visual.
    public void Init()
    {
        _ps = GetComponent<ParticleSystem>();
        _elapsedSinceStart = 0f;

        // Hard stop+clear BEFORE touching shape/main below (2026-10-13 fix) — a pooled reuse can still be
        // actively playing out its PREVIOUS cast when this fires (confirmed live: "Setting the duration
        // while system is still playing is not supported"). Unity flatly refuses to change main.duration on
        // a still-playing system, silently leaving it at whatever the previous cast's value was. Also
        // clears any of that previous cast's still-falling particles, which would otherwise visually bleed
        // into the new cast. StopEmittingAndClear (not the default Stop, which only stops emitting and lets
        // existing particles finish their own lifetime) — this needs to be immediate, not a fade-out.
        _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

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
        // startLifetime is authored in Constant mode (a single number, per the authored "start lifetime:
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

        // Explicit restart (2026-10-13 fix) — playOnAwake only ever fires on this GameObject's first-ever
        // Awake()/OnEnable(), never again on a pooled reuse (same class of gap the rest of this Init()
        // rework already exists to close), and the Stop() call above just halted playback regardless. Without
        // this, every cast after the very first would reconfigure correctly but never actually emit anything.
        _ps.Play(true);
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
