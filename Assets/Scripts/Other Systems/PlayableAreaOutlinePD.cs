using UnityEngine;

// Draws a LineRenderer loop tracing PlayableAreaPD.outerBoundary's own exact authored points — replaces the
// old transparent-rectangle boundary visuals (removed 2026-09-16; the physical boundary colliders themselves
// are separate hand-placed "Wall" GameObjects, untouched by this). Plain MonoBehaviour, not PredictedIdentity
// — same "fixed, author-time level geometry, identical on every machine, never changes at runtime" category
// PlayableAreaPD itself is in; purely a client-side visual with no gameplay effect, so nothing here needs to
// be predicted/networked.
//
// Reads PlayableAreaPD.GetOuterBoundaryPoints() (the raw plotted points, no CCW normalization or inset/
// corner math — see that method's own doc comment for why this deliberately does NOT go through
// GetInsetPerimeter, which is convex-only and built for a different consumer) once and sets it on the
// assigned LineRenderer as a closed loop. Color/width/material are authored directly on the LineRenderer
// component itself in the Inspector, same convention every other LineRenderer-driven visual in this codebase
// already follows — this script only ever touches positions.
public class PlayableAreaOutlinePD : MonoBehaviour
{
    [SerializeField] private LineRenderer lineRenderer;
    // Lifts the outline slightly above the ground plane to avoid z-fighting with the floor — outerBoundary's
    // own points are plotted at whatever Y the level designer happened to place those Transforms at, which
    // may sit exactly at floor height.
    [SerializeField] private float heightOffset = 0.05f;

    // Start, not Awake — PlayableAreaPD.instance is itself assigned in ITS OWN Awake(), and script execution
    // order between the two isn't guaranteed, so this waits until every Awake() in the scene has already run.
    private void Start()
    {
        if (lineRenderer == null || PlayableAreaPD.instance == null) return;

        Vector3[] points = PlayableAreaPD.instance.GetOuterBoundaryPoints();
        if (points.Length < 2) return;

        for (int i = 0; i < points.Length; i++)
            points[i] += Vector3.up * heightOffset;

        lineRenderer.useWorldSpace = true;
        lineRenderer.loop = true;
        lineRenderer.positionCount = points.Length;
        lineRenderer.SetPositions(points);
    }
}
