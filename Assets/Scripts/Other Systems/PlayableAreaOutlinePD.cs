using System.Collections.Generic;
using UnityEngine;

// Authors the playable area's own outer boundary shape directly (2026-09-20) — replaces the old
// array-of-plotted-Transforms authoring on PlayableAreaPD with the same Rectangle/Circle/Custom-with-
// bulge-curve authoring convenience ObstacleOutlinePD already gives obstacles, so a level's boundary shape can
// be entirely hand-typed in the Inspector instead of hand-placed as a pile of empty scene GameObjects.
//
// Lives on a SEPARATE CHILD GameObject from PlayableAreaPD/PlayableAreaBoundsPD (2026-09-20 revision) —
// sitting at local position (0,0,0) but rotated 90 degrees on X, same fixed-tilt convention ObstacleOutlinePD
// uses (see that class's own doc comment for the full quaternion derivation) — needed because a LineRenderer
// using Local alignment (rather than View/billboard, which looked wrong when tried live) renders its ribbon in
// the object's own local plane; on an UNTILTED object that plane stands vertically instead of lying flat on
// the ground. PlayableAreaPD/PlayableAreaBoundsPD themselves stay on the UNTILTED parent (they have no tilt-
// cancellation of their own — rotating THEM would break their own math), and read this component's
// BuildLocalPoints() via GetComponentInChildren instead of GetComponent — that method is pure shape data (no
// transform involved at all), so it doesn't matter which object it physically lives on; only THIS component's
// own LocalToWorld (used for its own LineRenderer/gizmo) needs to account for its own child's tilt.
//
// The bulge-arc curve math (AppendArcPoints) is intentionally DUPLICATED from ObstacleOutlinePD rather than
// extracted into a shared utility — despite being identical, extracting it would mean changing
// ObstacleOutlinePD's own serialized field layout, which risks silently resetting every already-authored
// Obstacle prefab's hand-plotted shape data (Tree Obstacle, Tree Line Obstacle, Tree Polygon Obstacle, etc.).
// A small, self-contained, unlikely-to-change static method is a safe place to accept duplication in exchange
// for zero risk to existing live content.
[RequireComponent(typeof(LineRenderer))]
public class PlayableAreaOutlinePD : MonoBehaviour
{
    private enum Shape { Rectangle, Circle, Custom }

    [SerializeField] private LineRenderer lineRenderer;
    [SerializeField] private Shape shape = Shape.Rectangle;
    // Lifts the outline slightly above the playable area's own ground plane to avoid z-fighting with the
    // floor — purely a visual concern for this component's own LineRenderer, see class doc comment.
    [SerializeField] private float heightOffset = 0.05f;

    [Header("Rectangle")]
    // Local X/Z size, centered on this transform.
    [SerializeField] private Vector2 rectangleSize = new Vector2(50f, 50f);

    [Header("Circle")]
    [SerializeField] private float circleRadius = 25f;
    [SerializeField] private int circleSegments = 32;

    [System.Serializable]
    private struct CustomPoint
    {
        public Vector2 position;
        [Tooltip("Curves the segment from THIS point to the next one in the list (not the previous one). 0 = straight line. Same 'bulge' convention CAD polylines use: the magnitude is tan(includedAngle/4), so 1 = an exact semicircle between this point and the next, and small values are a gentle bulge. Sign flips which side it curves toward — flip it if it bulges the wrong way.")]
        public float curveToNext;
    }

    [Header("Custom")]
    // Local X/Z offsets from this transform, in order — the loop back to the first point is automatic
    // (LineRenderer.loop = true), don't repeat it here.
    [SerializeField] private CustomPoint[] customPoints;
    // Tessellation resolution for any curved (curveToNext != 0) segment — higher is smoother, same
    // "authored resolution, not adaptive" convention circleSegments above already uses.
    [SerializeField] private int curveSegments = 12;

    private void Start()
    {
        if (lineRenderer == null) return;

        Vector2[] localPoints = BuildLocalPoints();
        if (localPoints.Length < 2) return;

        var worldPoints = new Vector3[localPoints.Length];
        for (int i = 0; i < localPoints.Length; i++)
            worldPoints[i] = LocalToWorld(localPoints[i]);

        lineRenderer.useWorldSpace = true;
        lineRenderer.loop = true;
        lineRenderer.positionCount = worldPoints.Length;
        lineRenderer.SetPositions(worldPoints);
    }

    // Public — PlayableAreaPD reads this same tessellated point list (curves already flattened into real line
    // segments, whichever Shape is configured) to build its own world-space boundary, so the visual outline
    // and the actual gameplay boundary are always built from the exact same authored geometry.
    public Vector2[] BuildLocalPoints()
    {
        switch (shape)
        {
            case Shape.Rectangle:
                float hx = rectangleSize.x * 0.5f;
                float hz = rectangleSize.y * 0.5f;
                return new[]
                {
                    new Vector2(-hx, -hz),
                    new Vector2(hx, -hz),
                    new Vector2(hx, hz),
                    new Vector2(-hx, hz),
                };

            case Shape.Circle:
                int segments = Mathf.Max(3, circleSegments);
                var circlePoints = new Vector2[segments];
                for (int i = 0; i < segments; i++)
                {
                    float angle = i / (float)segments * Mathf.PI * 2f;
                    circlePoints[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * circleRadius;
                }
                return circlePoints;

            case Shape.Custom:
            default:
                return BuildCustomPoints();
        }
    }

    private Vector2[] BuildCustomPoints()
    {
        if (customPoints == null || customPoints.Length < 2) return System.Array.Empty<Vector2>();

        var result = new List<Vector2>(customPoints.Length);
        for (int i = 0; i < customPoints.Length; i++)
        {
            Vector2 p0 = customPoints[i].position;
            Vector2 p1 = customPoints[(i + 1) % customPoints.Length].position;
            float bulge = customPoints[i].curveToNext;

            result.Add(p0);
            if (!Mathf.Approximately(bulge, 0f))
                AppendArcPoints(result, p0, p1, bulge, curveSegments);
        }
        return result.ToArray();
    }

    // Standard CAD/DXF "bulge arc" tessellation — see class doc comment for why this is a deliberate
    // duplication of ObstacleOutlinePD's own identical method rather than a shared extraction. Appends only
    // the INTERMEDIATE points strictly between p0 and p1 (both endpoints are already present elsewhere in the
    // outline's own point list).
    private static void AppendArcPoints(List<Vector2> output, Vector2 p0, Vector2 p1, float bulge, int segments)
    {
        segments = Mathf.Max(2, segments);
        float chordLength = Vector2.Distance(p0, p1);
        if (chordLength < 0.0001f) return;

        float includedAngle = 4f * Mathf.Atan(bulge);
        float halfAngle = includedAngle * 0.5f;
        float radius = (chordLength * 0.5f) / Mathf.Sin(halfAngle);

        Vector2 midpoint = (p0 + p1) * 0.5f;
        Vector2 direction = (p1 - p0) / chordLength;
        Vector2 perpendicular = new Vector2(-direction.y, direction.x);
        Vector2 center = midpoint + perpendicular * (radius * Mathf.Cos(halfAngle));

        float startAngle = Mathf.Atan2(p0.y - center.y, p0.x - center.x);
        float absRadius = Mathf.Abs(radius);

        for (int i = 1; i < segments; i++)
        {
            float angle = startAngle + includedAngle * (i / (float)segments);
            output.Add(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * absRadius);
        }
    }

    // Cancels this object's assumed-fixed 90-degree X tilt out of its rotation, leaving just any intentional
    // yaw, then applies ONLY that yaw to the local point — identical derivation to ObstacleOutlinePD's own
    // LocalToWorld (see that class's own doc comment for the full quaternion algebra); this component now
    // lives on the same kind of tilted child object. The height offset rides along unaffected either way: a
    // pure Y-axis rotation never changes a vector's own Y component. PlayableAreaPD does its own SEPARATE
    // conversion for gameplay points (its own untilted transform, Y=0, no heightOffset) — see class doc
    // comment for why the two are kept independent.
    private static readonly Quaternion FixedTiltInverse = Quaternion.Inverse(Quaternion.Euler(90f, 0f, 0f));

    private Vector3 LocalToWorld(Vector2 localPoint)
    {
        Quaternion yawOnly = transform.rotation * FixedTiltInverse;
        return transform.position + yawOnly * new Vector3(localPoint.x, heightOffset, localPoint.y);
    }

    // OnDrawGizmos, not OnDrawGizmosSelected (2026-09-16 convention, matches ObstacleOutlinePD) — shows the
    // playable area's own outline preview always, unselected, live as shape parameters are edited.
    private void OnDrawGizmos()
    {
        Vector2[] localPoints = BuildLocalPoints();
        if (localPoints.Length < 2) return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < localPoints.Length; i++)
        {
            Vector3 a = LocalToWorld(localPoints[i]);
            Vector3 b = LocalToWorld(localPoints[(i + 1) % localPoints.Length]);
            Gizmos.DrawLine(a, b);
        }
    }
}
