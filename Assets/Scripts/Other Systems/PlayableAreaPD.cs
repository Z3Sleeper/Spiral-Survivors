using UnityEngine;

// Defines the level's own walkable/playable region — used by anything that needs to place something
// "somewhere valid in the arena" without hand-rolling wall/obstruction-avoidance math itself (mid-cycle
// events' own zone placement is the first consumer — see EventManagerPD.StartEvent()/IEventPD.OnEventStart).
//
// Same authoring convention as EnemySpawnerPD's own spawnPoints (2026-08-25) — a plotted array of scene
// Transforms, not hand-typed numbers, so the shape can be freely re-plotted in the Editor to match whatever
// the level's actual walls look like. General POLYGON shape, not just an axis-aligned rectangle, since a
// future level's room won't necessarily be one: outerBoundary traces the walkable region's own outer edge
// (plot one point per wall corner, in order around the room, forming a closed loop — last point implicitly
// connects back to the first). innerObstructions is an OPTIONAL list of additional closed-loop polygons
// (e.g. a pillar, rock formation, any interior obstruction) to exclude from that region — empty by default,
// meaning "no interior obstructions, just the outer boundary."
//
// Plain MonoBehaviour, not PredictedIdentity: this is fixed, author-time level-layout config, identical on
// every machine and never changes at runtime — same category as EnemySpawnerPD's own plain spawnPoints/
// spawnMinDistance/spawnMaxDistance tunables, nothing here needs to be predicted State. RandomPoint() below
// still draws from GameRandom.instance (not UnityEngine.Random), since it gets called from
// EventManagerPD.StartEvent() — Simulate()-context, so it has to stay deterministic across every machine.
public class PlayableAreaPD : MonoBehaviour
{
    public static PlayableAreaPD instance;

    [System.Serializable]
    public struct Obstruction
    {
        public Transform[] points;
    }

    [Tooltip("Plotted points tracing the playable region's own outer edge, in order, forming a closed loop (one point per wall corner is the usual shape — last point implicitly connects back to the first). Y is ignored — this is a flat XZ region.")]
    [SerializeField] private Transform[] outerBoundary;

    [Tooltip("Optional interior obstructions (pillars, rock formations, inner walls) to exclude from the playable region — each entry is its own closed point loop, same convention as outerBoundary. Leave empty if the level has no interior obstructions.")]
    [SerializeField] private Obstruction[] innerObstructions;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    // Uniform-ish random point for something of a given `zoneRadius` footprint (e.g. an event's own circular
    // zone) — two SEPARATE rules, not one shared margin:
    //   1. Outer boundary is a HARD requirement: the candidate's own (zoneRadius + wallMargin)-inflated
    //      footprint must stay fully inside outerBoundary, no exceptions — a zone should never hug or clip a
    //      wall, however small the overlap.
    //   2. Inner obstructions are a SOFT requirement: at least `minUnobstructedFraction` (0-1, default 0.5 —
    //      "at least half clear") of the zone's own circle (radius = zoneRadius, wallMargin NOT included —
    //      see UnobstructedFraction's own doc comment for why) must be clear of every obstruction. This is
    //      what lets a big zone legitimately overlap a small interior obstacle instead of being blocked
    //      outright by it, while still rejecting a candidate that's mostly sitting inside one.
    // Rejection sampling against the outer polygon's own bounding box — this only ever needs to run once per
    // event start, not a real-time-critical path, so a proper direct-uniform-sampling/polygon-offset
    // algorithm would be solving a harder problem than this actually has. Falls back to the outer boundary's
    // own centroid if maxAttempts is exhausted (a pathologically small/cluttered area) — never returns
    // something undefined.
    //
    // outwardBiasSamples (default 1 = no bias, original behavior — a caller that wants center-avoidance opts
    // in explicitly): a simple "tournament" bias, not a true distribution reshape. Instead of returning the
    // FIRST valid candidate found, it keeps sampling (still capped by maxAttempts total) until it's collected
    // this many independent valid candidates, then returns whichever of THOSE is farthest from the outer
    // boundary's own centroid. Higher values pull the result further outward on average, with diminishing
    // returns (and more rejection-sampling work) as it climbs — good enough for "bias toward the edges,"
    // not meant to guarantee the single farthest point in the whole area.
    //
    // avoidPlayers/playerOffset (2026-08-21, opt-in — off by default, every existing caller unaffected): a
    // THIRD constraint, folded into the same rejection-sampling loop as the wall/obstruction checks above,
    // using the identical "zoneRadius + margin" shape the wall check already establishes (outerMargin =
    // zoneRadius + wallMargin) — a candidate is rejected if its own zoneRadius footprint would overlap a
    // playerOffset-radius "personal space" circle around any currently alive player. See EventBasePD's own
    // avoidPlayersOnPlacement/playerOffset fields for where a concrete event actually opts into this.
    public Vector3 RandomPoint(float wallMargin, float zoneRadius, float minUnobstructedFraction = 0.5f, int maxAttempts = 30, int outwardBiasSamples = 1, bool avoidPlayers = false, float playerOffset = 0f)
    {
        Vector3 fallback = Centroid(outerBoundary);
        if (outerBoundary == null || outerBoundary.Length < 3) return fallback;

        GetBounds(outerBoundary, out float minX, out float maxX, out float minZ, out float maxZ);
        if (maxX <= minX || maxZ <= minZ) return fallback;

        Vector2 center = ToXZ(fallback);
        int wantedSamples = Mathf.Max(1, outwardBiasSamples);
        float outerMargin = zoneRadius + wallMargin;

        Vector2 bestCandidate = default;
        float bestDistSq = -1f;
        int validFound = 0;

        for (int attempt = 0; attempt < maxAttempts && validFound < wantedSamples; attempt++)
        {
            float x = GameRandom.instance.NextFloat(minX, maxX);
            float z = GameRandom.instance.NextFloat(minZ, maxZ);
            var candidate = new Vector2(x, z);

            if (!PointInPolygon(candidate, outerBoundary)) continue;
            if (DistanceToPolygonEdges(candidate, outerBoundary) < outerMargin) continue;
            if (UnobstructedFraction(candidate, zoneRadius) < minUnobstructedFraction) continue;
            if (avoidPlayers && !IsFarEnoughFromEveryPlayer(candidate, zoneRadius + playerOffset)) continue;

            validFound++;
            float distSq = (candidate - center).sqrMagnitude;
            if (distSq > bestDistSq)
            {
                bestDistSq = distSq;
                bestCandidate = candidate;
            }
        }

        return validFound > 0 ? new Vector3(bestCandidate.x, fallback.y, bestCandidate.y) : fallback;
    }

    // Deterministic grid-sampled estimate (not exact circle-polygon geometry — same "good enough, not a hot
    // path" reasoning as RandomPoint's own rejection sampling) of what fraction of a circle of `radius`
    // centered at `center` is OUTSIDE every innerObstructions entry. wallMargin is deliberately NOT folded
    // in here — this measures the zone's own actual physical footprint, not an inflated avoidance radius;
    // padding it would work against the whole point of allowing a small overlap. Returns 1 (fully clear) if
    // there are no inner obstructions at all.
    private float UnobstructedFraction(Vector2 center, float radius, int gridResolution = 9)
    {
        if (innerObstructions == null || innerObstructions.Length == 0) return 1f;
        if (radius <= 0f) return 1f;

        float radiusSq = radius * radius;
        float step = (radius * 2f) / gridResolution;
        int total = 0;
        int clear = 0;

        for (int ix = 0; ix < gridResolution; ix++)
        {
            for (int iz = 0; iz < gridResolution; iz++)
            {
                var sample = new Vector2(
                    center.x - radius + step * (ix + 0.5f),
                    center.y - radius + step * (iz + 0.5f));
                if ((sample - center).sqrMagnitude > radiusSq) continue; // outside the circle itself

                total++;
                bool blocked = false;
                for (int i = 0; i < innerObstructions.Length; i++)
                {
                    var points = innerObstructions[i].points;
                    if (points == null || points.Length < 3) continue;
                    if (PointInPolygon(sample, points)) { blocked = true; break; }
                }
                if (!blocked) clear++;
            }
        }

        return total > 0 ? (float)clear / total : 1f;
    }

    // Flat XZ distance against every currently alive player — a dead player's corpse shouldn't block
    // placement, same "alive-only" reasoning every other physical-crowding check in this codebase uses (e.g.
    // EnemyBasePD.HandleSeparation's own near-player radius exception).
    private static bool IsFarEnoughFromEveryPlayer(Vector2 candidate, float minDistance)
    {
        float minDistSq = minDistance * minDistance;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead) continue;
            if ((candidate - ToXZ(stats.transform.position)).sqrMagnitude < minDistSq) return false;
        }
        return true;
    }

    private static Vector3 Centroid(Transform[] points)
    {
        if (points == null || points.Length == 0) return Vector3.zero;
        Vector3 sum = Vector3.zero;
        int count = 0;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] == null) continue;
            sum += points[i].position;
            count++;
        }
        return count > 0 ? sum / count : Vector3.zero;
    }

    private static void GetBounds(Transform[] points, out float minX, out float maxX, out float minZ, out float maxZ)
    {
        minX = float.MaxValue; maxX = float.MinValue;
        minZ = float.MaxValue; maxZ = float.MinValue;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] == null) continue;
            Vector3 p = points[i].position;
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }
    }

    // Standard ray-casting point-in-polygon test (even-odd rule) on the XZ plane — treats `polygon` as a
    // closed loop (wraps from the last point back to the first).
    private static bool PointInPolygon(Vector2 point, Transform[] polygon)
    {
        bool inside = false;
        int n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (polygon[i] == null || polygon[j] == null) continue;
            Vector2 pi = ToXZ(polygon[i].position);
            Vector2 pj = ToXZ(polygon[j].position);

            bool crosses = (pi.y > point.y) != (pj.y > point.y);
            if (!crosses) continue;

            float intersectX = pj.x + (point.y - pj.y) / (pi.y - pj.y) * (pi.x - pj.x);
            if (point.x < intersectX) inside = !inside;
        }
        return inside;
    }

    // Minimum distance from `point` to any edge segment of `polygon` (closed loop) — used to keep a
    // placed point a real margin away from a wall/obstruction, not just technically on the correct side of
    // it.
    private static float DistanceToPolygonEdges(Vector2 point, Transform[] polygon)
    {
        float minDist = float.MaxValue;
        int n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (polygon[i] == null || polygon[j] == null) continue;
            float dist = DistanceToSegment(point, ToXZ(polygon[j].position), ToXZ(polygon[i].position));
            if (dist < minDist) minDist = dist;
        }
        return minDist;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        float t = lenSq > 0.0001f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
        Vector2 closest = a + ab * t;
        return Vector2.Distance(p, closest);
    }

    private static Vector2 ToXZ(Vector3 v) => new Vector2(v.x, v.z);

    // General-purpose "steer this position back toward safety" query — added for EnemyBasePD.HandleMovement's
    // own bounds-avoidance (2026-08-25), but deliberately generic (just Vector3 in, Vector3 direction + 0-1
    // urgency out), no enemy-specific concept leaking in here. Cheap AABB pre-check first (GetBounds is O(n)
    // over the boundary but the boundary itself is only ever a handful of wall-corner points, and this whole
    // method is skipped entirely — the common case, most positions most of the time — the instant the AABB
    // check alone proves the position is nowhere near any edge, before ever touching the real polygon math).
    //
    // inwardDir points toward the polygon interior when the position is already safely-ish placed (i.e. the
    // direction AWAY from the nearest edge), but FLIPS to point toward the boundary itself when the position
    // has wandered fully outside the polygon (the direction TOWARD the nearest edge, i.e. back across it) —
    // both cases reduce to the exact same "closestEdgePoint minus/plus position" formula, just with the sign
    // of "which side of the edge am I on" flipped, so callers never need to special-case "am I outside" for
    // themselves.
    //
    // urgency01 is 0 exactly at wallMargin's own distance from the edge, ramping linearly to 1 the instant the
    // position crosses the edge itself (0 distance) — deliberately NOT continuing past 1 once actually
    // outside, so a badly-stuck position doesn't get an ever-growing "more urgent than urgent" value a caller
    // would have to clamp itself.
    //
    // Returns false (no steering needed, inwardDir/urgency both left at their default zero) when the position
    // is already more than wallMargin inside the boundary — the overwhelmingly common case for anything
    // spawned/wandering well away from a wall.
    public bool TryGetInwardSteering(Vector3 position, float wallMargin, out Vector3 inwardDir, out float urgency01)
    {
        inwardDir = Vector3.zero;
        urgency01 = 0f;
        if (outerBoundary == null || outerBoundary.Length < 3) return false;

        Vector2 p = ToXZ(position);

        // Cheap reject: well inside the boundary's own bounding box, margin included on every side — can't
        // possibly be within wallMargin of any real edge, so skip the polygon math entirely.
        GetBounds(outerBoundary, out float minX, out float maxX, out float minZ, out float maxZ);
        if (p.x > minX + wallMargin && p.x < maxX - wallMargin && p.y > minZ + wallMargin && p.y < maxZ - wallMargin)
            return false;

        bool isOutside = !PointInPolygon(p, outerBoundary);

        float minDistSq = float.MaxValue;
        Vector2 closest = default;
        int n = outerBoundary.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (outerBoundary[i] == null || outerBoundary[j] == null) continue;
            Vector2 a = ToXZ(outerBoundary[j].position);
            Vector2 b = ToXZ(outerBoundary[i].position);
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            float t = lenSq > 0.0001f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
            Vector2 candidate = a + ab * t;
            float distSq = (p - candidate).sqrMagnitude;
            if (distSq < minDistSq) { minDistSq = distSq; closest = candidate; }
        }
        if (minDistSq == float.MaxValue) return false;

        float dist = Mathf.Sqrt(minDistSq);
        if (!isOutside && dist >= wallMargin) return false;

        // Outside: point TOWARD the nearest edge (back across it). Inside-but-too-close: point AWAY from it
        // (deeper into the interior) — see this method's own doc comment for why both collapse to one formula.
        Vector2 dir2D = isOutside ? (closest - p) : (p - closest);
        if (dir2D.sqrMagnitude > 0.0001f) inwardDir = new Vector3(dir2D.x, 0f, dir2D.y).normalized;

        urgency01 = isOutside ? 1f : Mathf.Clamp01((wallMargin - dist) / wallMargin);
        return true;
    }

    // Editor-only visualization — draws the outer boundary (cyan) and every inner obstruction (red) as
    // closed loops, matching exactly what RandomPoint() actually reasons about, so plotting points in the
    // Scene view shows the real resulting shape immediately.
    private void OnDrawGizmos()
    {
        DrawLoop(outerBoundary, Color.cyan);
        if (innerObstructions == null) return;
        for (int i = 0; i < innerObstructions.Length; i++)
            DrawLoop(innerObstructions[i].points, Color.red);
    }

    private static void DrawLoop(Transform[] points, Color color)
    {
        if (points == null || points.Length < 2) return;
        Gizmos.color = color;
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            if (a == null || b == null) continue;
            Gizmos.DrawLine(a.position, b.position);
        }
    }
}
