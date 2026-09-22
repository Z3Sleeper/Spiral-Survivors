using System.Collections.Generic;
using UnityEngine;

// Auto-generates a level's ENTIRE boundary wall collider from a single already-authored shape (2026-09-19,
// simplified 2026-09-20) — reuses PlayableAreaPD's own plotted outerBoundary points (the "as usual"
// playable-area authoring, same GameObject) directly, with no separate outer-wall shape ever needing to be
// hand-plotted.
//
// Originally built as a filled RING between the playable outline and a large assumed outer rectangle — dropped
// once it became clear that approach was solving a problem that didn't need solving: a THIN wall panel hugging
// the outline is exactly as effective at stopping real physical movement (walking, dashing) as a thick one —
// Unity's discrete collision detection doesn't care how thick a solid collider is, only that it's there. The
// only reason "how far past the wall is genuinely open space" ever mattered was for a teleport-style ability
// (BWBlink) that sets position directly, bypassing physics entirely — and that's now handled correctly and
// unconditionally by TeleportSafetyPD's own isValidPosition parameter (PlayableAreaPD.IsInsidePlayableArea, a
// real point-in-polygon test), independent of wall geometry. So the wall itself only needs to be a real,
// thin, physically-solid barrier — no outer boundary, no assumed rectangle, no polygon-with-a-hole/bridging
// technique needed at all.
//
// One independent rectangular wall PANEL per edge of the outline, each extending outward from that edge by
// wallThickness — not one continuous ring mesh. A rectangle is trivially convex regardless of the outline's
// own overall shape (which CAN be concave — see PlayableAreaOutlinePD's own authoring), so this needs no
// ear-clipping/triangulation and no polygon-offsetting (the kind PlayableAreaPD.ComputeInsetPerimeter's own
// doc comment flags as convex-only) at all: each panel is built and reasoned about completely independently of
// every other one. All panels still combine into the ONE MeshCollider this component requires (Mesh.
// CombineMeshes), matching the single-MeshCollider Editor setup already wired up for this GameObject.
//
// This GameObject carries no fixed-tilt convention (unlike an Obstacle's own GameObject — see
// PolygonMeshExtrusionPD's own doc comment) — it's a plain, identity-rotation level-management object, same
// as PlayableAreaPD itself already assumes (its own outerBoundary points are read via world position with no
// rotation handling at all). Every point is converted from PlayableAreaPD's world-space authoring into THIS
// transform's own local space (via InverseTransformPoint) before being handed to the mesh pipeline, so the
// generated collider still lands correctly even if this object doesn't sit at the world origin.
[RequireComponent(typeof(PlayableAreaPD))]
[RequireComponent(typeof(MeshCollider))]
public class PlayableAreaBoundsPD : MonoBehaviour
{
    [Tooltip("How far outward each wall panel extends from the playable area's own outline. Only needs to be thick enough to read as a real wall visually/physically — TeleportSafetyPD's own point-in-polygon check (not wall thickness) is what actually prevents a teleport from ever landing past it.")]
    [SerializeField] private float wallThickness = 2f;

    [SerializeField] private float bottomY = -0.5f;
    [SerializeField] private float topY = 0.5f;

    private void Start()
    {
        Vector3[] innerWorld = GetComponent<PlayableAreaPD>().GetOuterBoundaryPoints();
        if (innerWorld.Length < 3) return;

        var outline = new Vector2[innerWorld.Length];
        for (int i = 0; i < innerWorld.Length; i++)
        {
            Vector3 local = transform.InverseTransformPoint(innerWorld[i]);
            outline[i] = new Vector2(local.x, local.z);
        }

        // Guaranteed winding (2026-09-20, same convention PolygonMeshExtrusionPD.BuildPrismMesh's own side
        // walls rely on) so each edge's own outward direction — a fixed 90-degree rotation of that edge's own
        // direction — is correct regardless of which order the outline was actually plotted in.
        PolygonTriangulationPD.EnsureWinding(outline, wantCCW: true);

        int n = outline.Length;
        var combine = new List<CombineInstance>(n);
        var proxyPanels = new List<Mesh>(n);

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            Vector2 dir = (outline[next] - outline[i]).normalized;
            Vector2 outward = new Vector2(dir.y, -dir.x);
            Vector2 farA = outline[i] + outward * wallThickness;
            Vector2 farB = outline[next] + outward * wallThickness;

            // outline[i] -> outline[next] -> farB -> farA simply traces this rectangle's own 4 corners in
            // order around its perimeter — whichever rotational direction that happens to read as doesn't
            // matter, since BuildPrismMesh normalizes winding internally regardless of input order (see its
            // own doc comment).
            Vector2[] panelPoints = { outline[i], outline[next], farB, farA };
            Mesh panelMesh = PolygonMeshExtrusionPD.BuildPrismMesh(panelPoints, bottomY, topY, applyFixedTilt: false);

            combine.Add(new CombineInstance { mesh = panelMesh, transform = Matrix4x4.identity });
            proxyPanels.Add(panelMesh);
        }

        var combinedMesh = new Mesh();
        combinedMesh.CombineMeshes(combine.ToArray(), mergeSubMeshes: true, useMatrices: true);

        var meshCollider = GetComponent<MeshCollider>();
        meshCollider.sharedMesh = combinedMesh;
        meshCollider.convex = false;

        // Each panel is already independently convex by construction (a simple rectangle) — no ear-clipping
        // needed the way ObstacleColliderPD's own concave shapes require it, so its own already-built mesh IS
        // its overlap-query proxy directly, just reused with convex/isTrigger flipped on. See
        // PolygonMeshExtrusionPD's own doc comment for why a convex, trigger-only sibling is needed at all
        // (Physics.CheckSphere/OverlapSphere can't detect the real non-convex combined collider above).
        foreach (Mesh panelMesh in proxyPanels)
        {
            var overlapQueryCollider = gameObject.AddComponent<MeshCollider>();
            overlapQueryCollider.sharedMesh = panelMesh;
            overlapQueryCollider.convex = true;
            overlapQueryCollider.isTrigger = true;
        }
    }
}
