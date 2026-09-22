using System.Collections.Generic;
using UnityEngine;

// Shared "extrude a flat polygon into a solid prism collider, plus per-triangle overlap-query proxies" pipeline
// — extracted out of ObstacleColliderPD (2026-09-19) once PlayableAreaBoundsPD needed the exact same
// triangulate/extrude/proxy pipeline for a level's own boundary walls, not just decorative obstacles. Nothing
// about the algorithm changed, just its ownership — one shared implementation now, not copy-pasted a second
// time, same "single source of truth" principle PolygonTriangulationPD itself is already built on.
//
// Always non-convex (MeshCollider.convex = false, set by each caller) — deliberate, not a limitation: since
// this already computes the EXACT triangulated shape itself, there's no reason to fall back to Unity's
// approximate convex-hull mode even for a convex input polygon.
//
// Overlap-query proxies (AddOverlapQueryProxies): one convex MeshCollider PER EAR-CLIPPED TRIANGLE, each
// marked as a trigger, that exist ONLY so overlap-style physics queries can see the real (non-convex)
// collider's shape at all. Confirmed live (TeleportSafetyPD's own diagnostic logging, root-causing
// BWBlinkAbilityPD teleporting straight into obstacles): Physics.CheckSphere/OverlapSphere cannot detect a
// non-convex MeshCollider under any circumstance, regardless of layer — a real, documented PhysX limitation.
// Sweep-based queries (SphereCast, Raycast) and real solid Rigidbody collision both work fine against the
// non-convex collider already, so these extra colliders change nothing about actual physical collision —
// isTrigger=true keeps them from ever participating in real collision response, they're purely detection
// proxies. Per-triangle, not one convex hull of the whole shape: a hull would round off any genuinely concave
// notch, treating real walkable/teleportable space as blocked. A triangle is ALWAYS exactly convex regardless
// of the source polygon's own shape, so a triangular prism has zero approximation error.
//
// Rotation handling: BuildPrismMesh's applyFixedTilt parameter exists because its two current callers sit in
// two different rotation conventions. ObstacleColliderPD's GameObject always carries the fixed 90-degree X
// tilt described in ObstacleOutlinePD's own class doc comment (every vertex is built in a plain, untilted
// authoring space, then FixedTiltInverse is applied once at the end so Unity's real transform.rotation
// re-applies just the intentional yaw once the tilt cancels out algebraically — see that class's history for
// the full derivation). PlayableAreaBoundsPD's GameObject carries no such tilt (it's a plain, identity-rotation
// level-management object), so it passes applyFixedTilt: false and its vertices are used exactly as given,
// with no rotation compensation needed at all.
public static class PolygonMeshExtrusionPD
{
    private static readonly Quaternion FixedTiltInverse = Quaternion.Inverse(Quaternion.Euler(90f, 0f, 0f));

    public static Mesh BuildPrismMesh(Vector2[] points, float bottomY, float topY, bool applyFixedTilt)
    {
        // Winding normalized to CCW first (2026-09-20, same shoelace convention PolygonTriangulationPD/
        // PlayableAreaPD.ComputeInsetPerimeter already use) so each edge's own outward direction can be
        // computed LOCALLY — a fixed 90-degree rotation of that edge's own direction — instead of by comparing
        // against the whole shape's average centroid. The centroid-based version this replaced broke
        // specifically for a RING/annulus polygon (PlayableAreaBoundsPD's bridged outer+inner loop): since the
        // outer and inner loops are roughly concentric, the centroid of ALL the ring's points lands inside the
        // EXCLUDED hole, not inside the actual solid wall material — which flipped the inner boundary's own
        // wall faces backward (confirmed live: players could walk out of the playable area freely but couldn't
        // walk back in, the exact signature of a one-sided/inverted collision mesh). A per-edge rotation has no
        // such blind spot — it only ever looks at that one edge's own two endpoints, regardless of the
        // polygon's overall shape. Copied rather than mutating the caller's own array in place, since some
        // callers (e.g. ObstacleColliderPD) reuse their own points array again right after this call.
        points = (Vector2[])points.Clone();
        PolygonTriangulationPD.EnsureWinding(points, wantCCW: true);

        int n = points.Length;

        // Bottom ring at indices 0..n-1, top ring at n..2n-1 — built in the plain authoring space described
        // in the class doc comment above.
        var vertices = new List<Vector3>(n * 2);
        for (int i = 0; i < n; i++) vertices.Add(new Vector3(points[i].x, bottomY, points[i].y));
        for (int i = 0; i < n; i++) vertices.Add(new Vector3(points[i].x, topY, points[i].y));

        var triangles = new List<int>(n * 12);

        // Side walls — one quad (as 2 triangles) per polygon edge. outwardHint is the edge's own direction
        // rotated 90 degrees — (dir.y, -dir.x) always points away from the interior once winding is CCW (the
        // exact opposite of PlayableAreaPD.GetPushedEdge's own (-dir.y, dir.x) "always points toward the
        // interior" convention — same rotation, just the other sign, reused here for the identical reason).
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int b0 = i, b1 = next, t0 = n + i, t1 = n + next;
            Vector2 dir = (points[next] - points[i]).normalized;
            Vector3 outwardHint = new Vector3(dir.y, 0f, -dir.x);
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

        // The one-time tilt compensation — see class doc comment. Applied last, uniformly, to every vertex,
        // only for a caller whose real transform.rotation actually carries the fixed 90-degree X tilt this
        // cancels out.
        if (applyFixedTilt)
            for (int i = 0; i < vertices.Count; i++)
                vertices[i] = FixedTiltInverse * vertices[i];

        var mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // See class doc comment for why this is per-triangle rather than one hull of the whole shape.
    public static void AddOverlapQueryProxies(GameObject target, Vector2[] points, float bottomY, float topY, bool applyFixedTilt)
    {
        List<int> ears = PolygonTriangulationPD.EarClipTriangulate(points);
        for (int i = 0; i < ears.Count; i += 3)
        {
            Vector2[] triPoints = { points[ears[i]], points[ears[i + 1]], points[ears[i + 2]] };
            var overlapQueryCollider = target.AddComponent<MeshCollider>();
            overlapQueryCollider.sharedMesh = BuildPrismMesh(triPoints, bottomY, topY, applyFixedTilt);
            overlapQueryCollider.convex = true;
            overlapQueryCollider.isTrigger = true;
        }
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
}
