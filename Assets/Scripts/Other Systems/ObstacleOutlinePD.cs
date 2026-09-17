using UnityEngine;

// Draws a LineRenderer outline around a static obstacle — Rectangle/Circle/Custom presets, all authored as
// LOCAL X/Z ground-plane offsets from this object's own position (not world space, unlike
// PlayableAreaOutlinePD, which only ever has exactly one instance and never moves).
//
// This GameObject is assumed to ALWAYS carry a fixed 90-degree X rotation (2026-09-16) — needed so its
// LineRenderer renders lying flat on the ground instead of standing upright. On TOP of that fixed tilt, this
// object's rotation is also allowed to carry a genuine yaw (rotation around world Y) for real gameplay
// placement — e.g. angling a rectangular obstacle's collider/outline to match its visuals. LocalToWorld()
// below extracts JUST that yaw and applies it to the outline's own points, while the fixed X tilt itself is
// never applied to them (a full transform.TransformPoint would apply BOTH, re-tilting the ground-plane-
// authored points right back off the ground).
//
// The extraction is exact quaternion algebra, not an approximation, PROVIDED this object's rotation is
// always exactly Quaternion.Euler(90, yaw, 0) — i.e. only ever set X=90/Z=0 with Y free to be whatever yaw is
// wanted (matches how the Inspector's own Rotation X/Y/Z fields are normally authored). Unity composes
// Euler(x,y,z) as qY(y) * qX(x) * qZ(z) internally, so with z=0: rotation = qY(yaw) * qX(90). Right-
// multiplying by qX(90)'s own inverse cancels the tilt exactly: rotation * Inverse(qX(90)) = qY(yaw) *
// qX(90) * Inverse(qX(90)) = qY(yaw). Deliberately NOT done via transform.eulerAngles.y — X=90 sits exactly
// on the standard Euler XYZ gimbal-lock singularity, where Unity's own Euler-angle decomposition of a
// quaternion becomes ambiguous; working entirely in quaternions here sidesteps that.
//
// Plain MonoBehaviour, not PredictedIdentity — same "fixed, author-time geometry, identical on every machine,
// purely a client-side visual with no gameplay effect" category PlayableAreaOutlinePD itself is in.
public class ObstacleOutlinePD : MonoBehaviour
{
    private enum Shape { Rectangle, Circle, Custom }

    [SerializeField] private LineRenderer lineRenderer;
    [SerializeField] private Shape shape = Shape.Rectangle;
    // Lifts the outline slightly above the obstacle's own ground plane to avoid z-fighting with the floor —
    // 0.01 is fine for an obstacle sitting exactly at y=0; bump it if the obstacle's own collision mesh is
    // taller/sits higher.
    [SerializeField] private float height = 0.01f;

    [Header("Rectangle")]
    // Local X/Z size, centered on this transform.
    [SerializeField] private Vector2 rectangleSize = new Vector2(1f, 1f);

    [Header("Circle")]
    [SerializeField] private float circleRadius = 0.5f;
    [SerializeField] private int circleSegments = 24;

    [System.Serializable]
    private struct CustomPoint
    {
        public Vector2 position;
        [Tooltip("Curves the segment from THIS point to the next one in the list (not the previous one). 0 = straight line. Same 'bulge' convention CAD polylines use: the magnitude is tan(includedAngle/4), so 1 = an exact semicircle between this point and the next (e.g. a capsule's rounded end-cap) and small values are a gentle bulge. Sign flips which side it curves toward — flip it if it bulges the wrong way.")]
        public float curveToNext;
    }

    [Header("Custom")]
    // Local X/Z offsets from this transform, in order — the loop back to the first point is automatic
    // (LineRenderer.loop = true), don't repeat it here. A capsule shape, for example, is just 4 points (the
    // two straight sides' corners) with curveToNext = 1 on the two points that start each rounded end.
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

    // Public (2026-09-17, was private) — ObstacleColliderPD reads this same tessellated point list (curves
    // already flattened into real line segments, whichever Shape is configured) to generate a matching
    // MeshCollider, so the physical collision shape and this visual outline are always built from the exact
    // same authored geometry, never a hand-duplicated approximation.
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

        var result = new System.Collections.Generic.List<Vector2>(customPoints.Length);
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

    // Standard CAD/DXF "bulge arc" tessellation — bulge = tan(includedAngle/4) fully determines the circular
    // arc between p0 and p1 (see class doc comment's sibling reasoning on CustomPoint.curveToNext's own
    // tooltip). Appends only the INTERMEDIATE points strictly between p0 and p1 (both endpoints are already
    // present elsewhere in the outline's own point list — p0 from this segment's own iteration, p1 as the
    // start of the next one). Derivation: for a chord half-length c subtending half-angle phi at the arc's
    // center, c = r*sin(phi) and the center sits a signed distance r*cos(phi) from the chord's midpoint along
    // its perpendicular bisector — this generalizes cleanly across the whole bulge range without a special
    // case, including bulge=1 (phi=90 degrees, cos(phi)=0, so the center lands exactly ON the chord's
    // midpoint — correct, since a semicircle's diameter IS the chord).
    private static void AppendArcPoints(System.Collections.Generic.List<Vector2> output, Vector2 p0, Vector2 p1, float bulge, int segments)
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

    // OnDrawGizmos, not OnDrawGizmosSelected (2026-09-16) — shows every obstacle's outline preview at once,
    // unselected, same always-visible convention the playable area's own outline follows in-Scene-view.
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

    // See class doc comment for the full derivation — cancels this object's assumed-fixed 90-degree X tilt
    // out of its rotation, leaving just the intentional yaw, then applies ONLY that yaw to the local point.
    // The height offset rides along unaffected either way: a pure Y-axis rotation never changes a vector's
    // own Y component.
    private static readonly Quaternion FixedTiltInverse = Quaternion.Inverse(Quaternion.Euler(90f, 0f, 0f));

    private Vector3 LocalToWorld(Vector2 localPoint)
    {
        Quaternion yawOnly = transform.rotation * FixedTiltInverse;
        return transform.position + yawOnly * new Vector3(localPoint.x, height, localPoint.y);
    }
}
