using UnityEngine;

public class PlayerColorApplicator : MonoBehaviour
{
    [SerializeField] private Renderer[] coloredRenderers;
    [SerializeField] private int materialIndex = 0;
    [SerializeField] private SpriteRenderer[] coloredSprites;

    [Header("Tint (multiplies original color by player color)")]
    [SerializeField] private Renderer[] tintedRenderers;
    [SerializeField] private int tintedMaterialIndex = 0;

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

        if (_originalTintColors == null) return;
        for (int i = 0; i < tintedRenderers.Length; i++)
        {
            if (tintedRenderers[i] == null) continue;
            var mats = tintedRenderers[i].materials;
            if (tintedMaterialIndex < mats.Length)
                mats[tintedMaterialIndex].color = _originalTintColors[i] * color;
            tintedRenderers[i].materials = mats;
        }
    }
}
