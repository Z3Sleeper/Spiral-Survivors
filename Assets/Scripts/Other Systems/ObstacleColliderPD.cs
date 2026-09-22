using UnityEngine;

// Generates a solid MeshCollider matching ObstacleOutlinePD's own polygon exactly — extrudes its flat
// (already curve-tessellated) point list into a prism (bottomY to topY) via PolygonMeshExtrusionPD (2026-09-19,
// extracted out of this class once PlayableAreaBoundsPD needed the exact same pipeline for a level's own
// boundary walls — see that shared class's own doc comment for the full algorithm: triangulation, per-triangle
// overlap-query proxies, and the fixed-tilt rotation handling this class's own GameObject relies on).
// Reuses ObstacleOutlinePD.BuildLocalPoints() directly rather than a second hand-authored point list — the
// shape is authored exactly once, and both the visual outline and this physical collider are built from it.
[RequireComponent(typeof(ObstacleOutlinePD))]
[RequireComponent(typeof(MeshCollider))]
public class ObstacleColliderPD : MonoBehaviour
{
    // -0.5/0.5 (2026-09-17) — matches the Y-extent every other collider in this project already uses, per
    // explicit request, rather than an arbitrary/independent thickness value.
    [SerializeField] private float bottomY = -0.5f;
    [SerializeField] private float topY = 0.5f;

    private void Start()
    {
        Vector2[] points = GetComponent<ObstacleOutlinePD>().BuildLocalPoints();
        if (points.Length < 3) return;

        var meshCollider = GetComponent<MeshCollider>();
        meshCollider.sharedMesh = PolygonMeshExtrusionPD.BuildPrismMesh(points, bottomY, topY, applyFixedTilt: true);
        meshCollider.convex = false;

        // This GameObject always carries the fixed 90-degree X tilt (see ObstacleOutlinePD's own class doc
        // comment) — applyFixedTilt: true here matches the real collider build above.
        PolygonMeshExtrusionPD.AddOverlapQueryProxies(gameObject, points, bottomY, topY, applyFixedTilt: true);
    }
}
