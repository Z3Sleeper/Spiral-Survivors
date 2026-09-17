using System.Collections.Generic;
using UnityEngine;

// Generates a solid MeshCollider matching ObstacleOutlinePD's own polygon exactly — extrudes its flat
// (already curve-tessellated) point list into a prism (bottomY to topY) and triangulates it via
// PolygonTriangulationPD.EarClipTriangulate (see that class's own doc comment — shared with
// ObstacleMinimapIconPD, not a hand-duplicated copy), so it works for ANY simple polygon, convex or concave,
// through one code path. Reuses ObstacleOutlinePD.BuildLocalPoints() directly rather than a second
// hand-authored point list — the shape is authored exactly once, and both the visual outline and this
// physical collider are built from it.
//
// Always non-convex (MeshCollider.convex = false) — deliberate, not a limitation: since this already computes
// the EXACT triangulated shape itself, there's no reason to fall back to Unity's approximate convex-hull mode
// even for a convex input polygon. Non-convex MeshColliders can only collide with NON-kinematic Rigidbodies
// incorrectly (Unity restricts them to being used only against kinematic/no-Rigidbody objects) — a total
// non-issue here since every obstacle IS a kinematic Rigidbody by design.
//
// Rotation handling (2026-09-17): this sits on the SAME GameObject as ObstacleOutlinePD/LineRenderer — the
// one assumed to always carry a fixed 90-degree X tilt (see ObstacleOutlinePD's own class doc comment for the
// full reasoning). Unlike that script's LineRenderer (which manually computes world-space positions and so
// can freely cancel the tilt at read-time), a MeshCollider has no such escape hatch — Unity ALWAYS applies
// this object's full, real rotation to whatever local-space vertices the mesh contains. So instead of
// cancelling the tilt when READING positions, this cancels it when WRITING them: every vertex is built first
// in a plain, intuitive, untilted authoring space (Vector3.up really means up, ground plane is XZ — the same
// space ObstacleOutlinePD's own points are authored in), with winding correctness verified/fixed in THAT
// simple space, and the fixed-tilt-inverse rotation is applied to every vertex only as the very last step.
// This ordering is safe because a pure rotation never changes a mesh's winding/handedness (only a reflection
// would) — so building correctly in the simple space and rotating everything afterward is equivalent to (and
// far easier to reason about than) trying to build directly in the tilted space. Once Unity re-applies this
// object's own full rotation (yaw * fixedTilt) on top of our pre-applied fixedTilt-inverse, the tilt cancels
// out algebraically and only the intentional yaw survives — same cancellation identity ObstacleOutlinePD's
// own LocalToWorld() doc comment derives, just applied to mesh vertices instead of LineRenderer positions.
[RequireComponent(typeof(ObstacleOutlinePD))]
[RequireComponent(typeof(MeshCollider))]
public class ObstacleColliderPD : MonoBehaviour
{
    // -0.5/0.5 (2026-09-17) — matches the Y-extent every other collider in this project already uses, per
    // explicit request, rather than an arbitrary/independent thickness value.
    [SerializeField] private float bottomY = -0.5f;
    [SerializeField] private float topY = 0.5f;

    private static readonly Quaternion FixedTiltInverse = Quaternion.Inverse(Quaternion.Euler(90f, 0f, 0f));

    private void Start()
    {
        Vector2[] points = GetComponent<ObstacleOutlinePD>().BuildLocalPoints();
        if (points.Length < 3) return;

        var meshCollider = GetComponent<MeshCollider>();
        meshCollider.sharedMesh = BuildMesh(points);
        meshCollider.convex = false;
    }

    private Mesh BuildMesh(Vector2[] points)
    {
        int n = points.Length;

        // Bottom ring at indices 0..n-1, top ring at n..2n-1 — built in the plain untilted authoring space
        // described in the class doc comment above.
        var vertices = new List<Vector3>(n * 2);
        for (int i = 0; i < n; i++) vertices.Add(new Vector3(points[i].x, bottomY, points[i].y));
        for (int i = 0; i < n; i++) vertices.Add(new Vector3(points[i].x, topY, points[i].y));

        var triangles = new List<int>(n * 12);

        // Side walls — one quad (as 2 triangles) per polygon edge, hinted to face away from the polygon's
        // own centroid (i.e. outward, regardless of the polygon's own winding direction).
        Vector2 centroid = Centroid(points);
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int b0 = i, b1 = next, t0 = n + i, t1 = n + next;
            Vector2 edgeMid = (points[i] + points[next]) * 0.5f;
            Vector3 outwardHint = new Vector3(edgeMid.x - centroid.x, 0f, edgeMid.y - centroid.y);
            AddTriangle(triangles, vertices, b0, t0, b1, outwardHint);
            AddTriangle(triangles, vertices, b1, t0, t1, outwardHint);
        }

        // Top/bottom faces — same ear-clipped triangle set for both (indices into the ORIGINAL point array),
        // remapped onto the top ring vs bottom ring and hinted up/down respectively. PolygonTriangulationPD
        // is the one shared implementation — see its own class doc comment.
        List<int> ears = PolygonTriangulationPD.EarClipTriangulate(points);
        for (int i = 0; i < ears.Count; i += 3)
            AddTriangle(triangles, vertices, n + ears[i], n + ears[i + 1], n + ears[i + 2], Vector3.up);
        for (int i = 0; i < ears.Count; i += 3)
            AddTriangle(triangles, vertices, ears[i], ears[i + 1], ears[i + 2], Vector3.down);

        // The one-time tilt compensation — see class doc comment. Applied last, uniformly, to every vertex.
        for (int i = 0; i < vertices.Count; i++)
            vertices[i] = FixedTiltInverse * vertices[i];

        var mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Adds triangle (a,b,c), self-correcting its winding so the resulting face normal points toward
    // outwardHint — computed via the SAME Vector3.Cross(edge1, edge2) formula Mesh.RecalculateNormals()
    // itself uses, so this doesn't depend on independently reasoning out Unity's raw clockwise/counter-
    // clockwise front-face convention; it just verifies the real result and flips b/c if it came out backward.
    private static void AddTriangle(List<int> triangles, List<Vector3> vertices, int a, int b, int c, Vector3 outwardHint)
    {
        Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
        if (Vector3.Dot(normal, outwardHint) < 0f) (b, c) = (c, b);
        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }

    private static Vector2 Centroid(Vector2[] points)
    {
        Vector2 sum = Vector2.zero;
        for (int i = 0; i < points.Length; i++) sum += points[i];
        return sum / points.Length;
    }
}
