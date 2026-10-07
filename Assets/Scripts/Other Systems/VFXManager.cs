using System.Collections.Generic;
using UnityEngine;

// Pooled, purely local/cosmetic particle-effect playback — same shape as SFXManager's own AudioSource pool
// (pre-instantiated once at Awake, reused round-robin), just for ParticleSystems instead of AudioSources.
// Deliberately plain MonoBehaviour, no PurrDiction involved at all — nothing here is gameplay-affecting or
// needs to be deterministic/networked, it's called once per machine off an already-verified/edge-detected
// transition (see call sites), same "purely local" reasoning LocalUpgradePointsTracker's own doc comment
// gives for why IT lives outside PurrDiction too.
//
// Generalized (2026-09-26) from a single hardcoded heal-only pool into one reusable PooledVfx engine, so
// adding another VFX variant (the user's own plan: copy the heal VFX prefab, swap color/sprite, reuse for
// other buff/restoration effects) only needs one new PooledVfx instance + a thin public wrapper method below,
// not a duplicated pool/cooldown/pause implementation each time.
public class VFXManager : MonoBehaviour
{
    public static VFXManager instance;

    // One pool + per-target cooldown + pause state, fully self-contained — see PlayHealVfx's own doc comment
    // for the mechanics each of these methods implements (round-robin reuse, Stop-before-Play, parent-to-
    // follow-target, per-target cooldown, pause polling). Kept as a plain nested class (not a struct) since
    // it owns a Dictionary and gets mutated through methods — a struct copy here would silently operate on a
    // throwaway copy instead of the real pool.
    private class PooledVfx
    {
        private readonly ParticleSystem[] _pool;
        private readonly float _cooldown;
        private readonly Dictionary<Transform, float> _lastPlayTime = new();
        // Cached once from the source prefab (2026-09-26, shockwave support) — lets Play(Transform, Color,
        // Color, float) below override just a Color-over-Lifetime gradient's RGB start/end, while preserving
        // whatever alpha fade was actually authored on the prefab, same "keep alpha, only touch color" split
        // PlayerColorApplicator's own trail-tinting already uses. Falls back to a plain opaque->transparent
        // fade if the prefab's own Color over Lifetime isn't in single-Gradient mode, so this never throws
        // for a pool (heal/dash-restore) that doesn't use this feature at all.
        private readonly GradientAlphaKey[] _originalAlphaKeys;
        // Cached once from the source prefab (2026-09-26, fixed-position support) — the prefab's own
        // authored local position/rotation (e.g. a ShockwaveVFX prefab lifted (0, 0.5, 0) off the ground to
        // avoid Z-fighting with it). Play(Vector3, ...) below re-applies this as an additive offset on top
        // of the passed-in cast position — same "height lifts apply only to final position, never facing"
        // convention already established elsewhere in this codebase — rather than discarding it, which is
        // what a raw SetPositionAndRotation(worldPosition, identity) would otherwise do once the instance is
        // unparented (the authored local offset has no parent left to apply against, so it just vanishes).
        private readonly Vector3 _originalLocalPosition;
        private readonly Quaternion _originalLocalRotation;
        private int _index;
        private bool _lastPaused;

        public PooledVfx(ParticleSystem prefab, int poolSize, float cooldown, Transform poolParent)
        {
            _cooldown = cooldown;
            if (prefab == null) { _pool = null; return; }

            var col = prefab.colorOverLifetime;
            _originalAlphaKeys = (col.enabled && col.color.mode == ParticleSystemGradientMode.Gradient && col.color.gradient != null)
                ? col.color.gradient.alphaKeys
                : new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) };
            _originalLocalPosition = prefab.transform.localPosition;
            _originalLocalRotation = prefab.transform.localRotation;

            _pool = new ParticleSystem[poolSize];
            for (int i = 0; i < poolSize; i++)
                _pool[i] = Instantiate(prefab, poolParent);
        }

        // Round-robin reuse, same convention as SFXManager.Play's own _index cycling — but unlike
        // AudioSource.Play() (which always restarts regardless of current state), ParticleSystem.Play() is a
        // no-op if the instance is still mid-burst from a recent reuse. Stop(true, StopEmittingAndClear)
        // first guarantees Play() actually (re)starts every call regardless of what the pooled instance was
        // doing.
        //
        // Parented to followTarget for the duration of the effect, not just position-snapped once — targets
        // are constantly moving, so an effect that plays out over any real duration needs to actually follow
        // them. Safe to parent under a PurrDiction-driven root despite this being a plain, non-predicted
        // object: Rollback() only ever calls SetPositionAndRotation on the PREDICTED transform itself, never
        // on arbitrary children — a plain child just inherits the parent's current world transform every
        // frame via ordinary Unity transform math, with no back-solve/drift risk (that risk is specific to a
        // child that ITSELF has SetPositionAndRotation called on it directly, e.g. the weapon-orbit-jitter
        // bug). For a player target specifically, parent to their ROOT transform, not the "Model" child —
        // Model has a continuous idle spin applied to its local rotation, which would otherwise spin the
        // whole particle emission shape.
        //
        // KNOWN GAP, not yet handled (2026-09-26, flagged not fixed): a pooled instance stays parented to
        // whichever target last used it — if that target's GameObject is destroyed (disconnect/scene
        // teardown) before this instance is reused, it's destroyed right along with it as a child, silently
        // shrinking the pool. Fine for now; needs a real fix (e.g. reparent back to this manager once the
        // effect finishes) before this ships for real.
        public void Play(Transform followTarget)
        {
            var vfx = TryStart(followTarget);
            if (vfx != null) vfx.Play();
        }

        // Fixed-position variant (2026-09-26, shockwave support) — no parent, no follow: a shockwave-style
        // burst is short-lived and should play exactly where its AoE actually happens (e.g. a tip-strike's
        // own hit location, not the caster), not trail after whoever cast it. Also overrides the Color over
        // Lifetime gradient's start/end RGB (alpha keys stay whatever was authored, see _originalAlphaKeys'
        // own doc comment) and Main.startSize — lets each CALLER decide color/size rather than baking one
        // fixed look into the prefab, since a shockwave-style ring is meant to be reused/recolored per
        // source (explicit user request). No cooldown tracking here at all (not just skipped when 0) — a
        // per-WORLD-POSITION cooldown wouldn't mean anything anyway, since two casts essentially never share
        // the exact same point.
        public void Play(Vector3 worldPosition, Color startColor, Color endColor, float startSize)
        {
            if (_pool == null) return;

            var vfx = _pool[_index % _pool.Length];
            _index++;
            vfx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            vfx.transform.SetParent(null, worldPositionStays: false);
            vfx.transform.SetPositionAndRotation(worldPosition + _originalLocalPosition, _originalLocalRotation);

            var main = vfx.main;
            main.startSize = startSize;

            var col = vfx.colorOverLifetime;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(startColor, 0f), new GradientColorKey(endColor, 1f) },
                _originalAlphaKeys);
            col.color = gradient;

            vfx.Play();
        }

        // Shared setup for the follow-target Play() overload above — cooldown check (skipped entirely when
        // _cooldown <= 0, i.e. a pool that's explicitly meant to be spammable, so it never even allocates a
        // dictionary entry for a target that has no cooldown to track), round-robin pool selection, and
        // parent/position/rotation reset.
        private ParticleSystem TryStart(Transform followTarget)
        {
            if (_pool == null || followTarget == null) return null;

            if (_cooldown > 0f)
            {
                if (_lastPlayTime.TryGetValue(followTarget, out float last) && Time.time - last < _cooldown)
                    return null;
                _lastPlayTime[followTarget] = Time.time;
            }

            var vfx = _pool[_index % _pool.Length];
            _index++;
            vfx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            vfx.transform.SetParent(followTarget, worldPositionStays: false);
            vfx.transform.localPosition = Vector3.zero;
            vfx.transform.localRotation = Quaternion.identity;
            return vfx;
        }

        // PausePD never touches Time.timeScale (see its own doc comment — pause is a plain flag every
        // gameplay system checks manually), so a native ParticleSystem has no built-in awareness of it at
        // all and just keeps animating in real time straight through a pause. Polled here (not event-driven
        // off PausePD.OnPauseChanged) because that event only fires for the raw manual-pause toggle
        // (currentState.isPaused) — it deliberately does NOT fire for level-up-presenting/win/loss, which
        // PausePD.IsPaused itself folds in too (see its own doc comment). Change-guarded so this is a no-op
        // every frame nothing actually changed, same "every per-frame UI write guarded against redundant
        // values" convention as everywhere else in this codebase.
        public void UpdatePause(bool paused)
        {
            if (_pool == null || paused == _lastPaused) return;
            _lastPaused = paused;

            foreach (var vfx in _pool)
            {
                if (vfx == null) continue;
                // Only touches instances actually mid-effect (isPlaying) or actually paused-by-us (isPaused)
                // — never blanket-calls Play() on every pooled instance on unpause, which would incorrectly
                // kick off a fresh burst on every currently-idle instance too.
                if (paused) { if (vfx.isPlaying) vfx.Pause(true); }
                else { if (vfx.isPaused) vfx.Play(true); }
            }
        }
    }

    [Header("Heal")]
    [SerializeField] private ParticleSystem healVfxPrefab;
    [SerializeField] private int healVfxPoolSize = 32;
    // Per-target anti-spam throttle — several different heal augments can all fire in quick succession off
    // independent trigger sources (ability start, dodge, onhit highroll, etc.), and this keeps any ONE
    // character from popping the effect more than once every this-many-seconds, regardless of which augment
    // fired it. Keyed by followTarget itself (not per-augment), so it's a genuine shared per-character
    // cooldown across every call site, not something each augment needs to track separately.
    [SerializeField] private float healVfxCooldown = 0.75f;

    [Header("Dash Restore")]
    [SerializeField] private ParticleSystem dashRestoreVfxPrefab;
    [SerializeField] private int dashRestoreVfxPoolSize = 32;
    // Same per-target anti-spam role as healVfxCooldown above, entirely independent of it — a heal and a
    // dash-restore landing on the same player within the same window should both still show, since they're
    // different effects; this only throttles repeats of THIS SAME effect.
    [SerializeField] private float dashRestoreVfxCooldown = 0.75f;

    [Header("Shockwave (Recolorable Ring)")]
    [SerializeField] private ParticleSystem shockwaveVfxPrefab;
    [SerializeField] private int shockwaveVfxPoolSize = 32;
    // No cooldown field, deliberately (explicit user request — "free to be spammed") — PooledVfx's own
    // cooldown check is skipped entirely whenever it's constructed with 0 here.
    //
    // Conversion factor from a real world-space radius to THIS ring prefab's own particle Start Size units
    // (found empirically — 2.72 Start Size per 1 unit of radius). Was briefly 1.36 (half the real value) —
    // the calibration test that produced that number used a default Unity Sphere primitive, which has
    // RADIUS 0.5 at scale 1 (i.e. scaling it to X gives a DIAMETER of X, not a radius of X) — so every
    // "radius" measurement against it was actually double the sphere's true radius, silently halving the
    // derived ratio. Confirmed against RangeScalingAoEBase * radiusMultiplier's own formula output directly
    // (3.125 at default stats) rather than a primitive a second time to be sure. Was 1.5 before that, an
    // even earlier, less precise estimate taken before Main Module's own Max Particle Size clamp — default
    // 0.5, a percentage-of-viewport ceiling — was raised to stop silently capping this ring's true size).
    // Baked in here rather than left to each call site, so callers can think in the same real-world-unit
    // terms every other AoE/effect-size formula in this codebase already uses (WeaponRange, AreaEffectSize,
    // the AoE radius formula, etc.) — nobody calling PlayShockwaveVfx should need to separately know/
    // remember a particle-specific ratio, and if this ring's own texture/proportions ever change, only this
    // one constant needs retuning, not every call site. A future second ring variant with a different
    // texture gets its own separate constant in its own pool, cleanly isolated from this one.
    private const float ShockwaveRadiusToStartSize = 2.72f;
    // Base height lift off the ground at Size=1 (2026-09-26, was a fixed prefab-authored local offset —
    // moved here instead, scaling with whichever character's own Size the caller passes in, since the ring
    // isn't always centered on the caster — TipStrikeRangeExplosionAugment centers it on the struck enemy,
    // whose own Size should determine how high it sits, not the player's). Prefab itself is authored flat at
    // height 0 to match.
    private const float ShockwaveBaseHeight = 0.5f;

    private PooledVfx _healVfx;
    private PooledVfx _dashRestoreVfx;
    private PooledVfx _shockwaveVfx;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);

        _healVfx = new PooledVfx(healVfxPrefab, healVfxPoolSize, healVfxCooldown, transform);
        _dashRestoreVfx = new PooledVfx(dashRestoreVfxPrefab, dashRestoreVfxPoolSize, dashRestoreVfxCooldown, transform);
        _shockwaveVfx = new PooledVfx(shockwaveVfxPrefab, shockwaveVfxPoolSize, 0f, transform);
    }

    public void PlayHealVfx(Transform followTarget) => _healVfx.Play(followTarget);
    public void PlayDashRestoreVfx(Transform followTarget) => _dashRestoreVfx.Play(followTarget);
    // groundPosition is a fixed WORLD point AT GROUND LEVEL (e.g. an AoE's own cast/hit location) — see
    // PooledVfx.Play(Vector3, ...)'s own doc comment for why this isn't a followed Transform. heightScale is
    // whichever character's own Size stat is relevant (CharacterStatsPD.Size/IEnemyPD.Size/IFriendlyPD.Size
    // — the caster for most AoEs, but e.g. the struck enemy for TipStrikeRangeExplosionAugment, since the
    // ring is centered on them, not the player) — see ShockwaveBaseHeight's own doc comment for why the lift
    // lives here instead of on the prefab. radius is the ring's final size, in real world
    // units (e.g. matching Stats.RangeScalingAoEBase or any other AoE radius formula) — converted internally
    // to this prefab's own particle Start Size units via ShockwaveRadiusToStartSize (see its own doc comment
    // for why that conversion lives here, not at each call site). Size over Lifetime is authored as a
    // 0%->100% curve of Main's own startSize on the prefab, so the converted value alone controls how big
    // the ring actually grows.
    public void PlayShockwaveVfx(Vector3 groundPosition, float heightScale, Color startColor, Color endColor, float radius) =>
        _shockwaveVfx.Play(groundPosition + Vector3.up * (ShockwaveBaseHeight * heightScale), startColor, endColor, radius * ShockwaveRadiusToStartSize);

    private void Update()
    {
        bool paused = PausePD.instance != null && PausePD.instance.IsPaused;
        _healVfx.UpdatePause(paused);
        _dashRestoreVfx.UpdatePause(paused);
        _shockwaveVfx.UpdatePause(paused);
    }
}
