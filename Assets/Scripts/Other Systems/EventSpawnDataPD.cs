using UnityEngine;

// Plain data holder for EventManagerPD's own hand-authored spawn geometry (2026-09-21) — split out of
// EventManagerPD itself after confirmed live testing: EventManagerPD is a PredictedIdentity, whose Inspector
// is PurrDiction's own PredictedIdentityEditor, which throws "Unsupported type Vector2f"/"Vector3f" the moment
// ANY array-of-struct field (Vector2[], Vector3[]) is declared directly on a PredictedIdentity-derived class —
// confirmed by trying both Vector2[] and Vector3[] in turn, both failing identically. Transform[] arrays
// (arrays of UnityEngine.Object REFERENCES) never had this problem, which is exactly why the old
// Transform-based authoring worked fine before this migration.
//
// This is a plain MonoBehaviour specifically so it uses Unity's own default Inspector instead — completely
// unaffected by PredictedIdentityEditor, since it isn't one. EventManagerPD holds a single serialized
// REFERENCE to an instance of this component (one Object-reference field, the one kind of field confirmed to
// work fine on a PredictedIdentity) rather than authoring any of this data on itself directly.
//
// The old hand-authored spawnAreas/SpawnArea system was removed entirely (2026-09-21) — its only consumer,
// DestroyTheTargetsPD, now uses PlayableAreaPD.RandomPoint (wallMargin/outwardBiasSamples/minCenterDistance/
// footprintRadius) against the whole playable area instead, per explicit design: "prefer away from center,
// avoid obstacles, never right at the edge" is exactly what that method already does, and — confirmed before
// removing this — RandomPoint's own wallMargin uses plain rejection sampling against the boundary's edges,
// NOT GetInsetPerimeter's polygon-shrinking corner-math (which is genuinely concave-broken), so there was
// never a real reason spawnAreas needed to exist as a separate hand-authored system in the first place.
//
// Both remaining arrays are direct Vector3 coordinates (X/Z used, Y ignored — every consumer explicitly
// discards it rather than trusting it, since every one of these is always authored on the ground plane),
// replacing the old hand-placed-Transform authoring — no scene marker GameObjects needed, same motivation as
// PlayableAreaPD's own outerBoundary migration to PlayableAreaOutlinePD.
public class EventSpawnDataPD : MonoBehaviour
{
    [Header("Event Spawn Data")]
    // Single designated spots (X/Z used, Y ignored) — an event that needs just one (or a few) exact positions
    // picks ONE of these verbatim (not edge-interpolated the way EnemySpawnerPD.spawnPoints treats its own
    // array as a loop of chords — confirmed explicit design choice: a "designated spawn point" should always
    // land exactly where it was placed, not somewhere randomized along an edge between two of them).
    [SerializeField] private Vector3[] eventSpawnPoints;
    // One shared patrol loop for the whole level (X/Z used, Y ignored), walked by PerimeterPatrolGloomPD and
    // spaced onto by PerimeterPatrolEventPD (via PlayableAreaPD.GetLoopLength/GetPointAtArcLength, already
    // built generically against any Vector3[] loop) — replaces the old GetInsetPerimeter(wallOffset)-derived
    // path.
    [SerializeField] private Vector3[] patrolGraph;

    [Header("Scene View Colors")]
    // Deliberately NOT cyan — that color is already used by ObstacleOutlinePD/PlayableAreaOutlinePD's own
    // gizmos, so a distinct default here keeps every hand-authored geometry system visually distinguishable
    // in the Scene view at a glance. Serialized (not hardcoded) so a level with a LOT of points can pick
    // colors that read clearly against its own specific scene dressing.
    [SerializeField] private Color patrolGraphGizmoColor = Color.yellow;
    [SerializeField] private Color eventSpawnPointGizmoColor = Color.magenta;
    // Purely a Scene view preview radius — has no bearing on EventManagerPD.TryGetEventSpawnPoint's own logic
    // (that method picks a spawn point verbatim; nothing in this codebase currently uses a "radius around an
    // event spawn point" for anything gameplay-relevant). Serialized specifically so it can be adjusted
    // without touching code if that ever changes.
    [SerializeField] private float eventSpawnPointGizmoRadius = 5f;

    public Vector3[] EventSpawnPoints => eventSpawnPoints;
    public Vector3[] PatrolGraph => patrolGraph;

    // Editor-only visualization — draws the shared patrol graph (closed-loop outline, same "wraps last back
    // to first" convention PlayableAreaPD.GetLoopLength/GetPointAtArcLength already use) and a radius circle
    // around each individual event spawn point, each in its own configurable color. OnDrawGizmos, not
    // OnDrawGizmosSelected — matches the always-visible convention every other hand-authored-geometry gizmo
    // in this codebase (ObstacleOutlinePD, PlayableAreaOutlinePD) already uses.
    private void OnDrawGizmos()
    {
        Gizmos.color = patrolGraphGizmoColor;
        DrawLoop(patrolGraph);

        if (eventSpawnPoints != null)
        {
            Gizmos.color = eventSpawnPointGizmoColor;
            foreach (var p in eventSpawnPoints)
                DrawCircle(new Vector3(p.x, 0f, p.z), eventSpawnPointGizmoRadius);
        }
    }

    // Closed loop (wraps from the last point back to the first) — Y ignored (see class doc comment).
    private static void DrawLoop(Vector3[] points)
    {
        if (points == null || points.Length < 2) return;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 a = points[i];
            Vector3 b = points[(i + 1) % points.Length];
            Gizmos.DrawLine(new Vector3(a.x, 0f, a.z), new Vector3(b.x, 0f, b.z));
        }
    }

    // Flat XZ circle — matches every other ground-plane outline's own convention in this codebase. Segments
    // fixed at a modest resolution since this is a pure Editor preview, not real geometry.
    private static void DrawCircle(Vector3 center, float radius, int segments = 32)
    {
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i / (float)segments * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
