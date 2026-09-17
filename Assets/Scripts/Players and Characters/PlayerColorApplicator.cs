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

    // Overrides the incoming color's own alpha before it's applied anywhere below — lets one object (e.g.
    // a range indicator ring) render at partial opacity while still tracking the player's color, without
    // needing a separate non-color-driven alpha mechanism. Applies uniformly to colored renderers/sprites
    // (direct override) and tinted renderers (multiplies into the original alpha same as the color itself).
    [SerializeField, Range(0f, 1f)] private float alpha = 1f;

    private Color[] _originalTintColors;

    private void Awake()
    {
        if (tintedRenderers == null || tintedRenderers.Length == 0) return;
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

        if (coloredParticleSystems == null) return;
        // Full alpha regardless of this component's own `alpha` override above — the trail's fade is already
        // authored entirely into its Color-over-Lifetime alpha keys (see coloredParticleSystems's own doc
        // comment); forcing 1 here just means "don't ALSO multiply a second alpha on top of that."
        var particleColor = color;
        particleColor.a = 1f;
        for (int i = 0; i < coloredParticleSystems.Length; i++)
        {
            if (coloredParticleSystems[i] == null) continue;
            var main = coloredParticleSystems[i].main;
            main.startColor = new ParticleSystem.MinMaxGradient(particleColor);
        }
    }
}
