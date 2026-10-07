using UnityEngine;

public class PlayerColorApplicator : MonoBehaviour
{
    [SerializeField] private Renderer[] coloredRenderers;
    [SerializeField] private int materialIndex = 0;
    [SerializeField] private SpriteRenderer[] coloredSprites;

    [Header("Tint (multiplies original color by player color)")]
    [SerializeField] private Renderer[] tintedRenderers;
    [SerializeField] private int tintedMaterialIndex = 0;

    // Weapon/projectile trail systems (WeaponMelee/Axe/Ranged's own weaponParticles, WeaponRangedProjectile's
    // arrow trail, ExeAxeThrowProjectile's throw trail) authored with a white Color-over-Lifetime gradient
    // (e.g. white@150 alpha -> white@0 alpha). Tinted by setting Main.startColor instead of touching that
    // gradient at all — Unity composites a particle's final color as startColor * Color-over-Lifetime, so
    // driving startColor to the player's color (alpha forced to 1 here) turns "white -> transparent white"
    // into "playerColor -> transparent playerColor" for free, alpha fade included, with zero changes needed
    // to each system's authored gradient.
    [SerializeField] private ParticleSystem[] coloredParticleSystems;

    // Player movement trail (2026-09-20) — a plain TrailRenderer using a Particles/Unlit material (Color Mode:
    // Multiply) so its own colorGradient actually has a visible effect (a default URP Lit/Unlit shader ignores
    // vertex color entirely, which colorGradient writes into — see the live debugging that led here). Tinted
    // the same way coloredParticleSystems is above: the ORIGINAL authored gradient's color keys (cached once
    // in Awake(), e.g. white -> transparent white) are multiplied by the player's color, its alpha keys left
    // completely untouched, so the trail's own authored fade-out survives unchanged and only its hue changes.
    [SerializeField] private TrailRenderer[] coloredTrailRenderers;

    // Overrides the incoming color's own alpha before it's applied anywhere below — lets one object (e.g.
    // a range indicator ring) render at partial opacity while still tracking the player's color, without
    // needing a separate non-color-driven alpha mechanism. Applies uniformly to colored renderers/sprites
    // (direct override) and tinted renderers (multiplies into the original alpha same as the color itself).
    [SerializeField, Range(0f, 1f)] private float alpha = 1f;

    private Color[] _originalTintColors;
    private GradientColorKey[][] _originalTrailColorKeys;
    // Cached once, in Awake() — each trail's own authored `time` (how long a point stays visible), same
    // "capture the real original before touching it" convention _originalTrailColorKeys above already uses.
    // Needed for the pause fix below: restoring the exact original value on unpause, not some hardcoded
    // guess, since different trails could plausibly be authored with different lengths.
    private float[] _originalTrailTimes;
    private bool _lastPaused;

    private void Awake()
    {
        if (tintedRenderers != null && tintedRenderers.Length > 0)
        {
            _originalTintColors = new Color[tintedRenderers.Length];
            for (int i = 0; i < tintedRenderers.Length; i++)
            {
                if (tintedRenderers[i] == null) continue;
                var mats = tintedRenderers[i].materials;
                _originalTintColors[i] = tintedMaterialIndex < mats.Length
                    ? mats[tintedMaterialIndex].color
                    : Color.white;
            }
        }

        if (coloredTrailRenderers != null && coloredTrailRenderers.Length > 0)
        {
            _originalTrailColorKeys = new GradientColorKey[coloredTrailRenderers.Length][];
            _originalTrailTimes = new float[coloredTrailRenderers.Length];
            for (int i = 0; i < coloredTrailRenderers.Length; i++)
                if (coloredTrailRenderers[i] != null)
                {
                    _originalTrailColorKeys[i] = coloredTrailRenderers[i].colorGradient.colorKeys;
                    _originalTrailTimes[i] = coloredTrailRenderers[i].time;
                }
        }
    }

    // PausePD never touches Time.timeScale (pause is a plain flag every gameplay system checks manually —
    // see VFXManager's own identical doc comment), so a TrailRenderer has no built-in awareness of it: its
    // existing points keep aging out and disappearing in real time straight through a "paused" game, even
    // though the player itself is frozen. Fixed here (2026-09-26), not in a new component, specifically to
    // reuse coloredTrailRenderers' own already-wired Inspector references instead of asking for the same
    // array twice. Sets `time` to PositiveInfinity while paused so no currently-visible point ever ages out
    // (freezing the trail exactly as it looked the instant pause hit), restores the real authored time on
    // unpause. `emitting` is also toggled off/on — belt-and-suspenders against LocalGraphicsSmoother-style
    // view-layer interpolation nudging the transform slightly even while gameplay logic itself is frozen,
    // which would otherwise still lay down new trail points during the "paused" window.
    private void Update()
    {
        if (coloredTrailRenderers == null || _originalTrailTimes == null) return;
        bool paused = PausePD.instance != null && PausePD.instance.IsPaused;
        if (paused == _lastPaused) return;
        _lastPaused = paused;

        for (int i = 0; i < coloredTrailRenderers.Length; i++)
        {
            var t = coloredTrailRenderers[i];
            if (t == null) continue;
            if (paused)
            {
                t.emitting = false;
                t.time = float.PositiveInfinity;
            }
            else
            {
                t.time = _originalTrailTimes[i];
                t.emitting = true;
            }
        }
    }

    public void Apply(Color color)
    {
        color.a = alpha;

        foreach (var r in coloredRenderers)
        {
            if (r == null) continue;
            var mats = r.materials;
            if (materialIndex < mats.Length)
                mats[materialIndex].color = color;
            r.materials = mats;
        }

        foreach (var s in coloredSprites)
            if (s != null) s.color = color;

        if (_originalTintColors != null)
        {
            for (int i = 0; i < tintedRenderers.Length; i++)
            {
                if (tintedRenderers[i] == null) continue;
                var mats = tintedRenderers[i].materials;
                if (tintedMaterialIndex < mats.Length)
                    mats[tintedMaterialIndex].color = _originalTintColors[i] * color;
                tintedRenderers[i].materials = mats;
            }
        }

        if (coloredParticleSystems != null)
        {
            // Full alpha regardless of this component's own `alpha` override above — the trail's fade is
            // already authored entirely into its Color-over-Lifetime alpha keys (see coloredParticleSystems's
            // own doc comment); forcing 1 here just means "don't ALSO multiply a second alpha on top of that."
            var particleColor = color;
            particleColor.a = 1f;
            for (int i = 0; i < coloredParticleSystems.Length; i++)
            {
                if (coloredParticleSystems[i] == null) continue;
                var main = coloredParticleSystems[i].main;
                main.startColor = new ParticleSystem.MinMaxGradient(particleColor);
            }
        }

        if (coloredTrailRenderers == null || _originalTrailColorKeys == null) return;
        // Same "force alpha to 1, don't touch the authored fade" reasoning as coloredParticleSystems above.
        var trailColor = color;
        trailColor.a = 1f;
        for (int i = 0; i < coloredTrailRenderers.Length; i++)
        {
            if (coloredTrailRenderers[i] == null || _originalTrailColorKeys[i] == null) continue;

            var originalKeys = _originalTrailColorKeys[i];
            var tintedKeys = new GradientColorKey[originalKeys.Length];
            for (int k = 0; k < originalKeys.Length; k++)
                tintedKeys[k] = new GradientColorKey(originalKeys[k].color * trailColor, originalKeys[k].time);

            Gradient gradient = coloredTrailRenderers[i].colorGradient;
            gradient.SetKeys(tintedKeys, gradient.alphaKeys);
            coloredTrailRenderers[i].colorGradient = gradient;
        }
    }
}
