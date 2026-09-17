using System.Collections.Generic;
using UnityEngine;

// Generates a flat filled mesh matching an obstacle's own outline exactly, for a minimap-only icon showing
// the real footprint the obstacle's collider blocks off — reuses ObstacleOutlinePD.BuildLocalPoints() (the
// same tessellated point list, curves included, feeding both the LineRenderer outline and ObstacleColliderPD)
// via a cross-object Inspector reference, and PolygonTriangulationPD.EarClipTriangulate for the fill (the
// same shared triangulation ObstacleColliderPD uses for its own top/bottom faces) — so this shape is never a
// hand-drawn approximation, just the authored polygon filled in flat.
//
// Deliberately on its OWN separate GameObject, NOT the same "Obstacle" child that holds ObstacleOutlinePD/
// ObstacleColliderPD/MeshCollider — a GameObject's Layer is one shared value, and that object needs to stay
// on the "Obstacle" layer for the collider-layer-based systems (XP orb push-back, EventManagerPD's
// obstacle-avoidance spawn check) to keep working. This object's own Layer should be set to "Minimap Icon" by
// hand in the Inspector instead (same convention the player's own existing minimap icon already uses — a
// hand-placed object on that layer, not something set via code) so the main camera never renders it and only
// the minimap camera does.
//
// No rotation compensation needed, unlike ObstacleColliderPD/ObstacleOutlinePD — this object is expected to
// sit at identity rotation (it has no LineRenderer-flatness requirement forcing a 90-degree X tilt the way
// those do), so vertices are written directly in plain (x, height, z) ground-plane space with no tilt-inverse
// math at all. If this object ever DOES need to inherit a tilt for some other reason, it would need the same
// fixed-tilt-cancellation trick those two scripts already use — see ObstacleOutlinePD's own class doc comment
// for the full derivation.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class ObstacleMinimapIconPD : MonoBehaviour
{
    // Cross-object reference (direct Inspector wiring, not a name-string lookup or GetComponent — same
    // "direct reference, fails loudly if unwired" convention CharacterStatsPD.unscaledSizeChildren already
    // establishes) — this object doesn't carry ObstacleOutlinePD itself, its sibling "Obstacle" child does.
    [SerializeField] private ObstacleOutlinePD outline;
    // 1 (2026-09-17, per explicit request) — well above the ground plane, not just a z-fighting-avoidance
    // nudge like ObstacleOutlinePD's own height field. Matches wherever else the minimap's own visible
    // layer expects things to sit above ground level.
    [SerializeField] private float height = 1f;

    private void Start()
    {
        if (outline == null) return;

        Vector2[] points = outline.BuildLocalPoints();
        if (points.Length < 3) return;

        GetComponent<MeshFilter>().mesh = BuildMesh(points);
    }

    private Mesh BuildMesh(Vector2[] points)
    {
        var vertices = new List<Vector3>(points.Length);
        for (int i = 0; i < points.Length; i++)
            vertices.Add(new Vector3(points[i].x, height, points[i].y));

        List<int> ears = PolygonTriangulationPD.EarClipTriangulate(points);
        var triangles = new List<int>(ears.Count);
        for (int i = 0; i < ears.Count; i += 3)
            AddTriangle(triangles, vertices, ears[i], ears[i + 1], ears[i + 2]);

        var mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // Self-corrects winding so the face normal points up (+Y) — see ObstacleColliderPD.AddTriangle's own
    // doc comment for why this doesn't depend on independently reasoning out Unity's raw winding convention.
    // A minimap camera looking straight down only ever sees this from above, so only an upward normal matters
    // here — unlike ObstacleColliderPD's solid prism, there's no "wrong side" to also guard against.
    private static void AddTriangle(List<int> triangles, List<Vector3> vertices, int a, int b, int c)
    {
        Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
        if (Vector3.Dot(normal, Vector3.up) < 0f) (b, c) = (c, b);
        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }
}
