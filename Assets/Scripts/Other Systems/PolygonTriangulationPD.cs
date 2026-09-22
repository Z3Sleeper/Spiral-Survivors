using System.Collections.Generic;
using UnityEngine;

// Standard ear-clipping polygon triangulation — the ONE shared implementation (2026-09-17, extracted out of
// ObstacleColliderPD, which was its only consumer until ObstacleMinimapIconPD needed the exact same
// algorithm for its own flat minimap mesh) — nothing hand-duplicates this a second time, same "single source
// of truth, no drift-prone copies" principle already applied to EnemySpawnerPD's own Compute*Multiplier
// methods.
public static class PolygonTriangulationPD
{
    // Works for ANY simple (non-self-intersecting) polygon, convex or concave, unlike a naive triangle-fan
    // (which only produces correct results for convex input). Returns triangle indices into the ORIGINAL
    // `points` array, 3 per triangle. Normalizes to a consistent winding internally (via the same
    // shoelace-sign convention PlayableAreaPD.ComputeInsetPerimeter already uses) purely so the convex/reflex
    // vertex test below has a stable meaning — callers should self-correct the OUTPUT triangles' own winding
    // for their own purposes (e.g. ObstacleColliderPD's own AddTriangle) rather than relying on it directly.
    public static List<int> EarClipTriangulate(Vector2[] points)
    {
        int n = points.Length;
        var indices = new List<int>(n);
        for (int i = 0; i < n; i++) indices.Add(i);

        float signedArea = 0f;
        for (int i = 0, j = n - 1; i < n; j = i++)
            signedArea += points[j].x * points[i].y - points[i].x * points[j].y;
        if (signedArea < 0f) indices.Reverse();

        var triangles = new List<int>((n - 2) * 3);
        int guard = 0;
        while (indices.Count > 3 && guard++ < n * n)
        {
            bool clipped = false;
            for (int i = 0; i < indices.Count; i++)
            {
                int iPrev = indices[(i - 1 + indices.Count) % indices.Count];
                int iCurr = indices[i];
                int iNext = indices[(i + 1) % indices.Count];

                Vector2 a = points[iPrev], b = points[iCurr], c = points[iNext];
                if (!IsConvexVertex(a, b, c)) continue;

                bool anyInside = false;
                for (int k = 0; k < indices.Count; k++)
                {
                    int idx = indices[k];
                    if (idx == iPrev || idx == iCurr || idx == iNext) continue;
                    if (PointInTriangle(points[idx], a, b, c)) { anyInside = true; break; }
                }
                if (anyInside) continue;

                triangles.Add(iPrev);
                triangles.Add(iCurr);
                triangles.Add(iNext);
                indices.RemoveAt(i);
                clipped = true;
                break;
            }
            // Degenerate/self-intersecting input (should not happen for a properly plotted obstacle) — bail
            // rather than spin; whatever triangles were already clipped are still added below/kept.
            if (!clipped) break;
        }
        if (indices.Count == 3)
        {
            triangles.Add(indices[0]);
            triangles.Add(indices[1]);
            triangles.Add(indices[2]);
        }

        return triangles;
    }

    // Shared winding-normalization helper (2026-09-20, extracted out of PlayableAreaBoundsPD once
    // PolygonMeshExtrusionPD.BuildPrismMesh also needed a GUARANTEED winding to compute each side wall's own
    // outward direction correctly — see that method's own doc comment for the ring/annulus bug this fixed.
    // Same shoelace-sign convention EarClipTriangulate's own internal normalization above and
    // PlayableAreaPD.ComputeInsetPerimeter already use. Reverses `points` in place if its current winding
    // doesn't match wantCCW.
    public static void EnsureWinding(Vector2[] points, bool wantCCW)
    {
        float signedArea = 0f;
        int n = points.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
            signedArea += points[j].x * points[i].y - points[i].x * points[j].y;

        bool isCCW = signedArea > 0f;
        if (isCCW != wantCCW) System.Array.Reverse(points);
    }

    private static bool IsConvexVertex(Vector2 a, Vector2 b, Vector2 c) =>
        (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) > 0f;

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Sign(p, a, b);
        float d2 = Sign(p, b, c);
        float d3 = Sign(p, c, a);
        bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(hasNeg && hasPos);
    }

    private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3) =>
        (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
}
