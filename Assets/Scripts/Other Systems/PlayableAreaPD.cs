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
    }

    // Uniform-ish random point for something of a given `zoneRadius` footprint (e.g. an event's own circular
    // zone) — two SEPARATE rules, not one shared margin:
    //   1. Outer boundary is a HARD requirement, checked against the candidate's own CENTER only, not a
    //      zoneRadius-inflated footprint (2026-08-25, was "(zoneRadius + wallMargin)-inflated footprint must
    //      stay fully inside" — loosened to match minCenterDistance's own "center must clear it, edge is free
    //      to dip in" shape below, per explicit user request): the center must be at least wallMargin from
    //      the nearest edge. A large zone's own outer edge is allowed to reach the wall itself (even slightly
    //      past it) as long as its center clears the margin.
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
    // using its own "zoneRadius + margin" shape (unrelated to/unaffected by the wall check's own 2026-08-25
    // loosening above, which now checks only the candidate's center) — a candidate is rejected if its own
    // zoneRadius footprint would overlap a playerOffset-radius "personal space" circle around any currently
    // alive player. See EventBasePD's own avoidPlayersOnPlacement/playerOffset fields for where a concrete
    // event actually opts into this.
    //
    // minCenterDistance (2026-08-25) — a FOURTH, HARD constraint, mandatory by default (10) rather than
    // opt-in: outwardBiasSamples above is only ever a SOFT tournament preference (picks the farthest of N
    // valid candidates, but "too close to center" was still a perfectly valid candidate on its own, just a
    // less-favored one) — in practice events kept landing near center anyway. This rejects any candidate
    // whose CENTER point is within minCenterDistance of the boundary's own centroid outright, before it's
    // ever eligible for the tournament — deliberately checked against the zone's own center, not its
    // zoneRadius-inflated edge, so a large zone is allowed to have half its own footprint peek into that
    // offset as long as its actual center clears it. Every existing caller gets this automatically (default
    // 10, not 0) since the whole point is that it's mandatory, not something each event has to remember to
    // opt into — pass 0 explicitly for a hypothetical future caller that genuinely doesn't want it.
    public Vector3 RandomPoint(float wallMargin, float zoneRadius, float minUnobstructedFraction = 0.5f, int maxAttempts = 30, int outwardBiasSamples = 1, bool avoidPlayers = false, float playerOffset = 0f, float minCenterDistance = 10f)
    {
        Vector3 fallback = Centroid(outerBoundary);
        if (outerBoundary == null || outerBoundary.Length < 3) return fallback;

        GetBounds(outerBoundary, out float minX, out float maxX, out float minZ, out float maxZ);
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
            if (!PointInPolygon(candidate, outerBoundary)) continue;
            // Checked against wallMargin alone now (2026-08-25, was zoneRadius + wallMargin) — see this
            // method's own doc comment for why: only the candidate's CENTER needs to clear the wall, the
            // zone's own edge is free to reach it (even slightly past).
            if (DistanceToPolygonEdges(candidate, outerBoundary) < wallMargin) continue;
            if (UnobstructedFraction(candidate, zoneRadius) < minUnobstructedFraction) continue;
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
    // urgency out), no enemy-specific concept leaking in here.
    //
    // Checks the outer boundary AND every inner obstruction (2026-08-26 — no innerObstructions exist in the
    // current level yet, added so the exact same margin/strafe system HandleMovement already drives will work
    // unmodified the moment one does), sharing the same wallMargin for both — see TryGetPolygonSteering's own
    // doc comment for the shared per-polygon math and the outer-vs-inner polarity flip (outside is dangerous
    // for the outer boundary; inside is dangerous for an obstruction). When more than one polygon reports
    // steering at once (e.g. squeezed into a gap between the outer wall and an obstruction), the single most
    // urgent result wins — callers only ever see one direction/urgency, never need to combine multiple
    // themselves.
    //
    // Returns false (no steering needed, inwardDir/urgency both left at their default zero) only when the
    // position clears wallMargin against every polygon checked — the overwhelmingly common case for anything
    // spawned/wandering well away from any wall or obstruction.
    public bool TryGetInwardSteering(Vector3 position, float wallMargin, out Vector3 inwardDir, out float urgency01)
    {
        inwardDir = Vector3.zero;
        urgency01 = 0f;
        Vector2 p = ToXZ(position);
        bool found = false;

        if (outerBoundary != null && outerBoundary.Length >= 3)
        {
            // Cheap reject: well inside the boundary's own bounding box, margin included on every side —
            // can't possibly be within wallMargin of any real edge, so skip the polygon math entirely.
            GetBounds(outerBoundary, out float minX, out float maxX, out float minZ, out float maxZ);
            bool clearOfOuter = p.x > minX + wallMargin && p.x < maxX - wallMargin && p.y > minZ + wallMargin && p.y < maxZ - wallMargin;
            if (!clearOfOuter)
            {
                bool isOutside = !PointInPolygon(p, outerBoundary);
                if (TryGetPolygonSteering(p, outerBoundary, wallMargin, isOutside, out Vector3 dir, out float urg))
                {
                    found = true;
                    inwardDir = dir;
                    urgency01 = urg;
                }
            }
        }

        if (innerObstructions != null)
        {
            for (int i = 0; i < innerObstructions.Length; i++)
            {
                var points = innerObstructions[i].points;
                if (points == null || points.Length < 3) continue;

                // Same cheap-reject idea as the outer boundary above, just margin-OUTWARD instead of inward
                // (an obstruction's danger zone extends wallMargin past its own edge, not inward from it).
                GetBounds(points, out float minX, out float maxX, out float minZ, out float maxZ);
                bool clearOfThis = p.x < minX - wallMargin || p.x > maxX + wallMargin || p.y < minZ - wallMargin || p.y > maxZ + wallMargin;
                if (clearOfThis) continue;

                bool isInside = PointInPolygon(p, points);
                if (TryGetPolygonSteering(p, points, wallMargin, isInside, out Vector3 dir, out float urg))
                {
                    // Most urgent wins if multiple polygons are steering at once — see class/method doc
                    // comment above.
                    if (!found || urg > urgency01)
                    {
                        found = true;
                        inwardDir = dir;
                        urgency01 = urg;
                    }
                }
            }
        }

        return found;
    }

    // Shared per-polygon steering math for TryGetInwardSteering above — factored out (2026-08-26) so the
    // outer boundary and every inner obstruction use IDENTICAL nearest-edge math, differing only in which
    // side of the polygon counts as dangerous. `dangerSide` is that flag: true means `p` is already on the
    // side that needs correcting RIGHT NOW (outside the polygon, for the outer boundary; inside it, for an
    // obstruction) — urgency is pinned to 1 and dir points straight back across the nearest edge. false means
    // `p` is on the safe side but might still be within `margin` of the edge — urgency ramps 0 (at exactly
    // `margin` away) to just-under-1 (right at the edge) and dir points further away from it, matching
    // TryGetInwardSteering's own original doc comment on why both cases reduce to one "closest edge point
    // minus/plus position" formula with only the sign flipped.
    private static bool TryGetPolygonSteering(Vector2 p, Transform[] polygon, float margin, bool dangerSide, out Vector3 dir, out float urgency01)
    {
        dir = Vector3.zero;
        urgency01 = 0f;

        float minDistSq = float.MaxValue;
        Vector2 closest = default;
        int n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (polygon[i] == null || polygon[j] == null) continue;
            Vector2 a = ToXZ(polygon[j].position);
            Vector2 b = ToXZ(polygon[i].position);
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            float t = lenSq > 0.0001f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) : 0f;
            Vector2 candidate = a + ab * t;
            float distSq = (p - candidate).sqrMagnitude;
            if (distSq < minDistSq) { minDistSq = distSq; closest = candidate; }
        }
        if (minDistSq == float.MaxValue) return false;

        float dist = Mathf.Sqrt(minDistSq);
        if (!dangerSide && dist >= margin) return false;

        Vector2 dir2D = dangerSide ? (closest - p) : (p - closest);
        if (dir2D.sqrMagnitude > 0.0001f) dir = new Vector3(dir2D.x, 0f, dir2D.y).normalized;

        urgency01 = dangerSide ? 1f : Mathf.Clamp01((margin - dist) / margin);
        return true;
    }

    // Outer boundary inset inward by `offset`, as an ordered, closed-loop Vector3[] — a walkable path
    // hugging the wall at a fixed distance (added for a patrol-enemy Event's own spawn/movement). ALWAYS
    // returned in a normalized counterclockwise winding (see the signed-area check below) regardless of
    // which order outerBoundary's own points happen to be authored in, so a consumer walking this array in
    // increasing index order never needs to know or care how the level was actually plotted — see
    // GetPushedEdge's own comment for why that normalization matters.
    //
    // Convex outerBoundary ONLY. For each edge, translates both endpoints inward along the edge's own
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
        if (outerBoundary == null || outerBoundary.Length < 3) return System.Array.Empty<Vector3>();

        int n = outerBoundary.Length;
        var points = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            if (outerBoundary[i] == null) return System.Array.Empty<Vector3>();
            points[i] = ToXZ(outerBoundary[i].position);
        }

        // Shoelace signed area — reverse the working point order if the raw authoring order came out
        // clockwise, so every downstream step (inward-normal direction, final array order) is correct
        // regardless of how outerBoundary was actually plotted.
        float signedArea = 0f;
        for (int i = 0, j = n - 1; i < n; j = i++)
            signedArea += points[j].x * points[i].y - points[i].x * points[j].y;
        if (signedArea < 0f) System.Array.Reverse(points);

        float baseY = outerBoundary[0].position.y;
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

    // Plain bounding-box rejection sampling against PointInPolygon alone — no wall margin, no obstruction
    // check, no centroid bias (none of that applies to a hand-authored area; a designer already put it
    // somewhere valid). General-purpose (2026-08-27, added for EventManagerPD's own hand-authored spawn
    // areas) — safe for NON-CONVEX polygons, unlike GetInsetPerimeter/ComputeInsetPerimeter: there's no
    // corner-intersection math here at all, just "is this random point inside the polygon," which
    // PointInPolygon's ray-casting test already handles correctly for any simple (non-self-intersecting)
    // polygon regardless of convexity. Returns false only if the polygon is degenerate (null/<3 points, or a
    // degenerate bounding box) or maxAttempts is exhausted without ever landing inside it (should be rare —
    // the only rejection criterion is "outside the polygon," so the hit rate is roughly the polygon's own
    // area versus its bounding box's).
    public static bool TryRandomPointInPolygon(Transform[] polygon, out Vector3 point, int maxAttempts = 30)
    {
        point = default;
        if (polygon == null || polygon.Length < 3) return false;

        GetBounds(polygon, out float minX, out float maxX, out float minZ, out float maxZ);
        if (maxX <= minX || maxZ <= minZ) return false;

        // Same "first non-null point's own Y" convention ComputeInsetPerimeter's own baseY already uses —
        // this is an XZ-plane-only system, Y is just carried through for the final Vector3.
        float y = 0f;
        for (int i = 0; i < polygon.Length; i++)
        {
            if (polygon[i] == null) continue;
            y = polygon[i].position.y;
            break;
        }

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            float x = GameRandom.instance.NextFloat(minX, maxX);
            float z = GameRandom.instance.NextFloat(minZ, maxZ);
            var candidate = new Vector2(x, z);
            if (!PointInPolygon(candidate, polygon)) continue;

            point = new Vector3(candidate.x, y, candidate.y);
            return true;
        }
        return false;
    }

    // True polygon area via the shoelace formula — the SAME math ComputeInsetPerimeter already computes
    // internally for its own CCW-winding-normalization check (see that method's own signedArea local),
    // extracted into a small reusable method (2026-08-27). Used by EventManagerPD to weight "which spawn
    // area gets picked" by actual footprint size rather than a rough bounding-box proxy, which would
    // overestimate area for any non-rectangular shape. Returns 0 for a degenerate polygon (null/<3 points).
    public static float PolygonArea(Transform[] polygon)
    {
        if (polygon == null || polygon.Length < 3) return 0f;

        float signedArea = 0f;
        int n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (polygon[i] == null || polygon[j] == null) continue;
            Vector2 pi = ToXZ(polygon[i].position);
            Vector2 pj = ToXZ(polygon[j].position);
            signedArea += pj.x * pi.y - pi.x * pj.y;
        }
        return Mathf.Abs(signedArea) * 0.5f;
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
