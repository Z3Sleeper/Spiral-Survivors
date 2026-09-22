using UnityEngine;

// Defines the level's own walkable/playable region — used by anything that needs to place something
// "somewhere valid in the arena" without hand-rolling wall-avoidance math itself (mid-cycle events' own zone
// placement is the first consumer — see EventManagerPD.StartEvent()/IEventPD.OnEventStart).
//
// Outer boundary shape is authored via PlayableAreaOutlinePD (2026-09-20, replaced the old plotted-array-of-
// Transforms authoring) — same Rectangle/Circle/Custom-with-bulge-curve authoring ObstacleOutlinePD already
// gives obstacles, so a level's boundary can be entirely hand-typed rather than a pile of hand-placed empty
// GameObjects. That component lives on a SEPARATE CHILD GameObject (tilted 90 degrees on X, same convention
// ObstacleOutlinePD's own GameObject uses — needed for its LineRenderer to render flat; see that class's own
// doc comment), found via GetComponentInChildren rather than [RequireComponent]/GetComponent, since it's no
// longer on this same object. THIS class's own transform stays untilted — it has no tilt-cancellation of its
// own (BuildWorldBoundaryPoints uses a plain transform.TransformPoint), so it must never be rotated itself.
// BuildLocalPoints() is pure shape data with no transform involved at all, so it doesn't matter which object
// physically holds that component; the result is cached once in Awake() (this is fixed, author-time
// geometry — see class doc comment further down), and every method below reads from that cache instead of
// re-deriving it. General POLYGON shape, not just an axis-aligned rectangle, since a level's room won't
// necessarily be one.
//
// Interior obstructions (pillars, rocks, trees, etc.) used to be a SEPARATE hand-plotted polygon system
// (innerObstructions) living on this class — removed entirely 2026-09-17, since the real Obstacle system
// (physics colliders + ObstacleOutlinePD, see that class's own doc comment) now covers the exact same role
// better: real collision instead of a duplicate hand-authored polygon that had to be kept in sync by hand,
// and EventManagerPD.TryGetSpawnAreaPoint's own obstacle-avoidance check already reads real Obstacle-layer
// colliders directly. innerObstructions was confirmed fully dead before removal — its only two consumers
// (RandomPoint's own UnobstructedFraction check, and TryGetInwardSteering) had zero live callers themselves,
// and the scene itself had innerObstructions authored empty.
//
// Plain MonoBehaviour, not PredictedIdentity: this is fixed, author-time level-layout config, identical on
// every machine and never changes at runtime — same category as EnemySpawnerPD's own plain spawnPoints/
// spawnMinDistance/spawnMaxDistance tunables, nothing here needs to be predicted State. RandomPoint() below
// still draws from GameRandom.instance (not UnityEngine.Random), since it gets called from
// EventManagerPD.StartEvent() — Simulate()-context, so it has to stay deterministic across every machine.
public class PlayableAreaPD : MonoBehaviour
{
    public static PlayableAreaPD instance;

    // World-space, cached once in Awake() — see class doc comment. NOT recomputed per-call the way the old
    // Transform-array version implicitly was (each Transform's own .position was already always live); a
    // plain Vector3[] has nothing to go stale, since PlayableAreaOutlinePD's own shape is fixed, author-time
    // geometry that never changes at runtime.
    private Vector3[] _boundaryPoints;

    // Cached result for GetInsetPerimeter — keyed by the offset it was computed for. GetInsetPerimeter itself
    // is unreferenced as of 2026-08-27 (its one caller, PerimeterPatrolEventPD, migrated to EventManagerPD's
    // own hand-authored patrolGraph — see that method's own doc comment for why), left in place rather than
    // deleted in case a future caller needs it. Fixed, author-time level geometry, same "never changes at
    // runtime" category as everything else in this file, so a single-entry cache (recomputed only when a
    // DIFFERENT offset is requested) is enough — no invalidation needed beyond that.
    private Vector3[] _cachedInsetPerimeter;
    private float _cachedInsetOffset = float.NaN;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        _boundaryPoints = BuildWorldBoundaryPoints();
    }

    // Converts PlayableAreaOutlinePD's own local, untilted authoring points into world space — Y is always 0
    // in the local point before conversion (deliberately NOT PlayableAreaOutlinePD's own heightOffset, which
    // exists purely to keep ITS OWN LineRenderer visual from z-fighting the floor and has no business
    // affecting the real gameplay boundary used here).
    private Vector3[] BuildWorldBoundaryPoints()
    {
        Vector2[] localPoints = GetComponentInChildren<PlayableAreaOutlinePD>().BuildLocalPoints();
        var worldPoints = new Vector3[localPoints.Length];
        for (int i = 0; i < localPoints.Length; i++)
            worldPoints[i] = transform.TransformPoint(new Vector3(localPoints[i].x, 0f, localPoints[i].y));
        return worldPoints;
    }

    // Uniform-ish random point for something of a given `zoneRadius` footprint (e.g. an event's own circular
    // zone) — two SEPARATE rules, not one shared margin:
    //   1. Outer boundary is a HARD requirement, checked against the candidate's own CENTER only, not a
    //      zoneRadius-inflated footprint (2026-08-25, was "(zoneRadius + wallMargin)-inflated footprint must
    //      stay fully inside" — loosened to match minCenterDistance's own "center must clear it, edge is free
    //      to dip in" shape below, per explicit user request): the center must be at least wallMargin from
    //      the nearest edge. A large zone's own outer edge is allowed to reach the wall itself (even slightly
    //      past it) as long as its center clears the margin.
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
    // SECOND constraint, folded into the same rejection-sampling loop as the wall check above, using its own
    // "zoneRadius + margin" shape (unrelated to/unaffected by the wall check's own 2026-08-25 loosening above,
    // which now checks only the candidate's center) — a candidate is rejected if its own zoneRadius footprint
    // would overlap a playerOffset-radius "personal space" circle around any currently alive player. See
    // EventBasePD's own avoidPlayersOnPlacement/playerOffset fields for where a concrete event actually opts
    // into this.
    //
    // minCenterDistance (2026-08-25) — a THIRD, HARD constraint, mandatory by default (10) rather than
    // opt-in: outwardBiasSamples above is only ever a SOFT tournament preference (picks the farthest of N
    // valid candidates, but "too close to center" was still a perfectly valid candidate on its own, just a
    // less-favored one) — in practice events kept landing near center anyway. This rejects any candidate
    // whose CENTER point is within minCenterDistance of the boundary's own centroid outright, before it's
    // ever eligible for the tournament — deliberately checked against the zone's own center, not its
    // zoneRadius-inflated edge, so a large zone is allowed to have half its own footprint peek into that
    // offset as long as its actual center clears it. Every existing caller gets this automatically (default
    // 10, not 0) since the whole point is that it's mandatory, not something each event has to remember to
    // opt into — pass 0 explicitly for a hypothetical future caller that genuinely doesn't want it.
    //
    // footprintRadius (2026-09-21, added when EventManagerPD's own hand-authored spawnAreas system was
    // removed in favor of this method) — a FOURTH constraint, same "Obstacle" layer CheckSphere the old
    // TryGetSpawnAreaPoint used, gated on footprintRadius > 0 the same way (0, the default, skips the check
    // entirely for a caller that doesn't size its own footprint).
    public Vector3 RandomPoint(float wallMargin, float zoneRadius, int maxAttempts = 30, int outwardBiasSamples = 1, bool avoidPlayers = false, float playerOffset = 0f, float minCenterDistance = 10f, float footprintRadius = 0f)
    {
        Vector3 fallback = Centroid(_boundaryPoints);
        if (_boundaryPoints == null || _boundaryPoints.Length < 3) return fallback;

        GetBounds(_boundaryPoints, out float minX, out float maxX, out float minZ, out float maxZ);
        if (maxX <= minX || maxZ <= minZ) return fallback;

        Vector2 center = ToXZ(fallback);
        int wantedSamples = Mathf.Max(1, outwardBiasSamples);

        Vector2 bestCandidate = default;
        float bestDistSq = -1f;
        int validFound = 0;

        float minCenterDistSq = minCenterDistance * minCenterDistance;
        for (int attempt = 0; attempt < maxAttempts && validFound < wantedSamples; attempt++)
        {
            float x = GameRandom.instance.NextFloat(minX, maxX);
            float z = GameRandom.instance.NextFloat(minZ, maxZ);
            var candidate = new Vector2(x, z);

            float distSq = (candidate - center).sqrMagnitude;
            if (distSq < minCenterDistSq) continue;
            if (!PointInPolygon(candidate, _boundaryPoints)) continue;
            // Checked against wallMargin alone now (2026-08-25, was zoneRadius + wallMargin) — see this
            // method's own doc comment for why: only the candidate's CENTER needs to clear the wall, the
            // zone's own edge is free to reach it (even slightly past).
            if (DistanceToPolygonEdges(candidate, _boundaryPoints) < wallMargin) continue;
            if (footprintRadius > 0f && Physics.CheckSphere(new Vector3(x, fallback.y, z), footprintRadius, LayerMask.GetMask("Obstacle")))
                continue;
            if (avoidPlayers && !IsFarEnoughFromEveryPlayer(candidate, zoneRadius + playerOffset)) continue;

            validFound++;
            if (distSq > bestDistSq)
            {
                bestDistSq = distSq;
                bestCandidate = candidate;
            }
        }

        return validFound > 0 ? new Vector3(bestCandidate.x, fallback.y, bestCandidate.y) : fallback;
    }

    // Flat XZ distance against every currently alive player — a dead player's corpse shouldn't block
    // placement, same "alive-only" reasoning every other physical-crowding check in this codebase uses (e.g.
    // EnemyBasePD.HandleSeparation's own near-player radius exception).
    // internal (was private, 2026-08-27) — reused as-is by EventManagerPD's own spawn-point/spawn-area
    // player-avoidance checks, so hand-authored event placement doesn't need a third copy of this same
    // "distance to every alive player" logic (EnemySpawnerPD already has a separate one for its own unrelated
    // spawn-position pipeline).
    internal static bool IsFarEnoughFromEveryPlayer(Vector2 candidate, float minDistance)
    {
        float minDistSq = minDistance * minDistance;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead) continue;
            if ((candidate - ToXZ(stats.transform.position)).sqrMagnitude < minDistSq) return false;
        }
        return true;
    }

    private static Vector3 Centroid(Vector3[] points)
    {
        if (points == null || points.Length == 0) return Vector3.zero;
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < points.Length; i++) sum += points[i];
        return sum / points.Length;
    }

    private static void GetBounds(Vector3[] points, out float minX, out float maxX, out float minZ, out float maxZ)
    {
        minX = float.MaxValue; maxX = float.MinValue;
        minZ = float.MaxValue; maxZ = float.MinValue;
        for (int i = 0; i < points.Length; i++)
        {
            Vector3 p = points[i];
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }
    }

    // Standard ray-casting point-in-polygon test (even-odd rule) on the XZ plane — treats `polygon` as a
    // closed loop (wraps from the last point back to the first).
    private static bool PointInPolygon(Vector2 point, Vector3[] polygon)
    {
        bool inside = false;
        int n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 pi = ToXZ(polygon[i]);
            Vector2 pj = ToXZ(polygon[j]);

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
    private static float DistanceToPolygonEdges(Vector2 point, Vector3[] polygon)
    {
        float minDist = float.MaxValue;
        int n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float dist = DistanceToSegment(point, ToXZ(polygon[j]), ToXZ(polygon[i]));
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

    // Public wrapper around the existing PointInPolygon ray-casting test (2026-09-20) — reused as the "must
    // stay inside the actual playable shape" check for TeleportSafetyPD.FindSafeTeleportPosition's own
    // isValidPosition parameter (see that method's own doc comment). Correct for a concave outline too, same
    // as every other PointInPolygon consumer here — no separate bounding-box approximation needed.
    public bool IsInsidePlayableArea(Vector3 worldPosition) =>
        _boundaryPoints != null && _boundaryPoints.Length >= 3 && PointInPolygon(ToXZ(worldPosition), _boundaryPoints);

    // Raw boundary points, in whatever order PlayableAreaOutlinePD's own shape produced them (no CCW
    // normalization, no inset/corner math at all) — for a pure visual OUTLINE consumer that has no use for
    // GetInsetPerimeter's convex-only corner-intersection machinery below. Returns a fresh COPY each call
    // (the internal cache is shared/reused elsewhere and must never be mutated by a caller — e.g.
    // PlayableAreaBoundsPD's own winding-normalization step reverses its own copy in place).
    public Vector3[] GetOuterBoundaryPoints() => (Vector3[])(_boundaryPoints ?? System.Array.Empty<Vector3>()).Clone();

    // Outer boundary inset inward by `offset`, as an ordered, closed-loop Vector3[] — a walkable path
    // hugging the wall at a fixed distance (added for a patrol-enemy Event's own spawn/movement). ALWAYS
    // returned in a normalized counterclockwise winding (see the signed-area check below) regardless of
    // which order the boundary's own points happen to be authored in, so a consumer walking this array in
    // increasing index order never needs to know or care how the level was actually plotted — see
    // GetPushedEdge's own comment for why that normalization matters.
    //
    // Convex outer boundary ONLY. For each edge, translates both endpoints inward along the edge's own
    // inward normal by `offset` (GetPushedEdge), then intersects each pair of adjacent translated edges
    // (LineIntersection) to find the new inset corner at that vertex. A concave boundary can make adjacent
    // translated edges cross/pinch near a reflex corner instead of intersecting where expected — not
    // handled here; would need real polygon offsetting (straight-skeleton style) if the arena shape ever
    // stops being convex. Direction (does increasing index actually read as counterclockwise from a
    // top-down camera, not just "consistent") is confirmed empirically once tested live, same as every
    // other rotational convention in this codebase — flip the `signedArea < 0f` condition below if it
    // comes out backward.
    public Vector3[] GetInsetPerimeter(float offset)
    {
        if (_cachedInsetPerimeter != null && Mathf.Approximately(_cachedInsetOffset, offset))
            return _cachedInsetPerimeter;

        _cachedInsetOffset = offset;
        _cachedInsetPerimeter = ComputeInsetPerimeter(offset);
        return _cachedInsetPerimeter;
    }

    private Vector3[] ComputeInsetPerimeter(float offset)
    {
        if (_boundaryPoints == null || _boundaryPoints.Length < 3) return System.Array.Empty<Vector3>();

        int n = _boundaryPoints.Length;
        var points = new Vector2[n];
        for (int i = 0; i < n; i++)
            points[i] = ToXZ(_boundaryPoints[i]);

        // Shoelace signed area — reverse the working point order if the raw authoring order came out
        // clockwise, so every downstream step (inward-normal direction, final array order) is correct
        // regardless of how the boundary was actually plotted.
        float signedArea = 0f;
        for (int i = 0, j = n - 1; i < n; j = i++)
            signedArea += points[j].x * points[i].y - points[i].x * points[j].y;
        if (signedArea < 0f) System.Array.Reverse(points);

        float baseY = _boundaryPoints[0].y;
        var inset = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            int prev = (i - 1 + n) % n;
            int next = (i + 1) % n;

            GetPushedEdge(points[prev], points[i], offset, out Vector2 prevA, out Vector2 prevB);
            GetPushedEdge(points[i], points[next], offset, out Vector2 nextA, out Vector2 nextB);

            Vector2 corner = LineIntersection(prevA, prevB, nextA, nextB, out Vector2 hit)
                ? hit
                : prevB; // degenerate (parallel edges) fallback — vertex pushed along just the prev edge's normal
            inset[i] = new Vector3(corner.x, baseY, corner.y);
        }
        return inset;
    }

    // Translates edge (a -> b) inward by `offset` along its own inward normal — rotating the edge direction
    // 90° via (-dir.y, dir.x) always points toward the interior once winding is normalized to counterclockwise
    // (see GetInsetPerimeter's own doc comment).
    private static void GetPushedEdge(Vector2 a, Vector2 b, float offset, out Vector2 pushedA, out Vector2 pushedB)
    {
        Vector2 dir = (b - a).normalized;
        Vector2 inward = new Vector2(-dir.y, dir.x);
        pushedA = a + inward * offset;
        pushedB = b + inward * offset;
    }

    // Standard 2D line-line intersection, treating both as infinite lines through the given point pairs
    // (not segments — correct here since GetPushedEdge only ever hands in genuinely adjacent pushed edges,
    // which are always meant to actually cross near the original shared vertex for a convex polygon).
    // Returns false for parallel/near-parallel lines.
    private static bool LineIntersection(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4, out Vector2 result)
    {
        result = default;
        float d1x = p2.x - p1.x, d1y = p2.y - p1.y;
        float d2x = p4.x - p3.x, d2y = p4.y - p3.y;
        float denom = d1x * d2y - d1y * d2x;
        if (Mathf.Abs(denom) < 0.0001f) return false;

        float t = ((p3.x - p1.x) * d2y - (p3.y - p1.y) * d2x) / denom;
        result = new Vector2(p1.x + t * d1x, p1.y + t * d1y);
        return true;
    }

    // Total length of a closed-loop Vector3[] (e.g. GetInsetPerimeter's own output), summing every edge
    // including the wraparound edge from the last point back to the first. General-purpose — not tied to
    // GetInsetPerimeter specifically, works on any closed-loop point array (2026-08-27, added alongside
    // GetPointAtArcLength below so future events can space anything evenly by TRUE arc length around a
    // loop, not just PerimeterPatrolEventPD's own patrol perimeter).
    public static float GetLoopLength(Vector3[] loop)
    {
        if (loop == null || loop.Length < 2) return 0f;
        float length = 0f;
        for (int i = 0; i < loop.Length; i++)
            length += Vector3.Distance(loop[i], loop[(i + 1) % loop.Length]);
        return length;
    }

    // Walks a closed loop's own edges by cumulative distance and returns the point sitting `arcLength`
    // along it from loop[0] (wrapped via modulo against the loop's own total length via GetLoopLength — a
    // negative or over-long arcLength still resolves sensibly rather than walking off the end), plus the
    // index of the NEXT vertex ahead of that point in increasing-index order — the natural "which waypoint
    // should something walking this loop head toward from here" answer, since the interpolated point
    // generally doesn't land exactly ON a vertex (only exactly at arcLength 0, or already-exact multiples
    // of a segment length, would).
    //
    // General-purpose (2026-08-27) — not perimeter-patrol-specific. PerimeterPatrolEventPD.OnEventStart is
    // the first caller (spacing enemyCount > 1 spawns evenly around the patrol loop by picking one random
    // arcLength, then calling this at arcLength + i * GetLoopLength(loop) / enemyCount for each), but any
    // future event wanting N things evenly spaced by true arc length around a closed loop — or just a
    // single point at a specific distance along one — can call this directly. Increasing index == counter-
    // clockwise specifically for GetInsetPerimeter's own output (see that method's own doc comment); for
    // any other loop, "next" simply means "the next point in array order," whatever that happens to mean
    // for that particular array.
    public static Vector3 GetPointAtArcLength(Vector3[] loop, float arcLength, out int nextIndex)
    {
        if (loop == null || loop.Length == 0) { nextIndex = 0; return Vector3.zero; }
        if (loop.Length == 1) { nextIndex = 0; return loop[0]; }

        float totalLength = GetLoopLength(loop);
        if (totalLength <= 0f) { nextIndex = 0; return loop[0]; }

        arcLength = ((arcLength % totalLength) + totalLength) % totalLength;

        float walked = 0f;
        for (int i = 0; i < loop.Length; i++)
        {
            Vector3 a = loop[i];
            Vector3 b = loop[(i + 1) % loop.Length];
            float segLength = Vector3.Distance(a, b);
            if (segLength <= 0f) continue;

            if (walked + segLength >= arcLength)
            {
                float t = (arcLength - walked) / segLength;
                nextIndex = (i + 1) % loop.Length;
                return Vector3.Lerp(a, b, t);
            }
            walked += segLength;
        }

        // Floating-point edge case only — arcLength landed fractionally past the summed total length due to
        // rounding. Resolves to the very last vertex; the next waypoint from there wraps to the start.
        nextIndex = 0;
        return loop[loop.Length - 1];
    }

    // Editor-only visualization — lives on PlayableAreaOutlinePD now (reads BuildLocalPoints() live, so it
    // works in Edit mode too, before this component's own Awake() has ever run and cached anything).
}
