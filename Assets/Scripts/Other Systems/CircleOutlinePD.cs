using UnityEngine;

// Minimal circle-only LineRenderer generator — for ArrowRainVFXPD's ground ring, which unlike
// ObstacleOutlinePD/PlayableAreaOutlinePD is NOT static author-time geometry baked into world space. This
// object is expected to sit nested under a RadiusIndicatorVFXPD root, which IndicatorMath.ApplyRadialTransform
// scales every frame to match the real gameplay radius — so the points here are authored once, in LOCAL
// space, at a fixed radius of 0.5 (matching the same diameter-1-at-scale-1 convention the old cylinder mesh
// used), and left for the parent's own scaling to stretch them to the real radius. useWorldSpace = false is
// what makes that scaling apply automatically; if this were ever baked to world space instead (like
// ObstacleOutlinePD), the ring would stop tracking the parent's scale entirely.
//
// Points are authored in the local XY plane (Z=0), matching ObstacleOutlinePD/PlayableAreaOutlinePD's own
// "flat sheet lying in local XY, tipped onto the ground via a 90-degree X rotation" convention — this
// GameObject is expected to carry that same fixed 90-degree X tilt every other ground outline in this
// codebase does. NOT the XZ plane (which would already be flat with zero rotation) — that was this script's
// original, inconsistent authoring, corrected once it turned out to stand the ring up on its edge once the
// usual tilt was applied on top.
[RequireComponent(typeof(LineRenderer))]
public class CircleOutlinePD : MonoBehaviour
{
    [SerializeField] private int segments = 32;
    private const float Radius = 0.5f;

    private LineRenderer _line;
    // Captured once in Awake(), before the parent root's own ApplyRadialTransform scaling has ever run this
    // life — the true, undistorted width the LineRenderer was authored with (whatever widthMultiplier/curve
    // combo is set on the prefab), preserved separately from the live-scaled value LateUpdate() writes back
    // every frame below.
    private float _authoredWidthMultiplier;

    private void Awake()
    {
        _line = GetComponent<LineRenderer>();
        _authoredWidthMultiplier = _line.widthMultiplier;
        _line.useWorldSpace = false;
        _line.loop = true;
        _line.positionCount = segments;
        for (int i = 0; i < segments; i++)
        {
            float angle = i / (float)segments * Mathf.PI * 2f;
            _line.SetPosition(i, new Vector3(Mathf.Cos(angle) * Radius, Mathf.Sin(angle) * Radius, 0f));
        }
    }

    // Start(), not Awake() — GetComponentInParent<RadiusIndicatorVFXPD>() would find the component fine
    // either way, but its TintColor isn't set until Launch() runs, which happens synchronously right after
    // hierarchy.Create() returns — AFTER every freshly-instantiated child's own Awake() already fired, but
    // before Unity's deferred Start() (same timing reasoning ArrowRainParticleRadiusPD's own radius sync
    // already uses). No manual Inspector wiring needed (unlike ArrowRainParticleRadiusPD.radiusSource) — this
    // object is always nested under its own RadiusIndicatorVFXPD root by construction (see class doc
    // comment), so the parent lookup is guaranteed to find it.
    private void Start()
    {
        var source = GetComponentInParent<RadiusIndicatorVFXPD>();
        if (source == null || !source.TintColor.HasValue) return;

        Color tint = source.TintColor.Value;
        Color start = _line.startColor;
        Color end = _line.endColor;
        start.r = tint.r; start.g = tint.g; start.b = tint.b;
        end.r = tint.r; end.g = tint.g; end.b = tint.b;
        _line.startColor = start;
        _line.endColor = end;
    }

    // Counter-scales the rendered width against the parent's own live ApplyRadialTransform scaling (2026-09-27)
    // — the ring's POINTS are meant to stretch with that scale (that's how the outline tracks the real AoE
    // radius at all), but the LineRenderer's stroke width would otherwise stretch right along with them (same
    // local-space transform matrix affects both), making a big AoE's outline look absurdly thick instead of a
    // consistent authored thickness. LateUpdate, not Update — needs to run AFTER whatever this frame set the
    // parent's scale to (RadiusIndicatorVFXPD.UpdateView's own ApplyRadialTransform call), same ordering
    // reasoning already established elsewhere for read-after-write per-frame corrections.
    private void LateUpdate()
    {
        float scale = transform.lossyScale.x;
        _line.widthMultiplier = _authoredWidthMultiplier / Mathf.Max(0.0001f, scale);
    }
}
