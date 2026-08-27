using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Scaffolding for the upcoming mid-cycle events system (2026-08-24) — see EnemySpawnerPD's own class-level
// "Tier Cycle"/"Quarter"/"Wave" terminology comment for the surrounding context this plugs into.
// EnemySpawnerPD calls StartEvent()/ResolveEvent() directly (see its own "Mid-cycle event trigger" block in
// Simulate()) since it's the one identity that already has direct knowledge of the Tier Cycle timers this is
// scheduled off of.
//
// PredictedIdentity (not a plain MonoBehaviour) from the start — these calls happen from inside
// EnemySpawnerPD.Simulate(), so tracking which event is currently active needs to be genuinely
// resimulation-safe predicted State (see feedback_pd_architecture — PredictedIdentity for sim state, plain
// MonoBehaviour otherwise).
//
// Individual event TYPES are their own EventBasePD<TState>-derived prefabs (see EventBasePD.cs's own doc
// comment for the shared IEventPD contract) — completely unrelated to each other beyond that contract, per
// explicit design. eventPrefabs is empty until the first real event type is actually built (2026-08-24: none
// exist yet); StartEvent()/ResolveEvent() below already handle that gracefully (no-op with nothing to pick
// from), so nothing here needs to change once real prefabs get added to that array.
public class EventManagerPD : PredictedIdentity<EventManagerPD.State>
{
    public static EventManagerPD instance;

    [SerializeField] private GameObject[] eventPrefabs;

    // Hand-authored replacement (2026-08-27) for the old PlayableAreaPD.RandomPoint/GetInsetPerimeter-derived
    // event placement — see PlayableAreaPD.GetInsetPerimeter's own doc comment for why that approach was
    // abandoned (only correct for a convex outerBoundary; a concave notch or a very sharp corner produces
    // broken/overshooting results, and building a proper general fix — a straight-skeleton algorithm, or
    // integrating a library like Clipper2 — was judged disproportionate effort for a solo dev's project with
    // no concrete plan for a non-convex arena). All three arrays are plain level-authored data, same
    // "designer places Transforms in the Inspector" convention EnemySpawnerPD.spawnPoints/PlayableAreaPD.
    // outerBoundary already use — full designer control, zero geometry-robustness concerns, at the cost of
    // needing manual re-sync if the arena shape ever changes (acceptable for a largely-static level).
    [System.Serializable]
    public struct SpawnArea
    {
        // Ordered closed-loop polygon, same authoring convention as PlayableAreaPD.outerBoundary — CAN be
        // non-convex, unlike outerBoundary's own inset-perimeter use: PlayableAreaPD.TryRandomPointInPolygon
        // only ever does plain rejection sampling + PointInPolygon, none of GetInsetPerimeter's corner-
        // intersection math, so it's safe for any simple (non-self-intersecting) shape.
        public Transform[] points;
    }

    [Header("Event Spawn Data")]
    // Single designated spots — an event that needs just one (or a few) exact positions picks ONE of these
    // verbatim (not edge-interpolated the way EnemySpawnerPD.spawnPoints treats its own array as a loop of
    // chords — confirmed explicit design choice: a "designated spawn point" should always land exactly where
    // it was placed, not somewhere randomized along an edge between two of them).
    [SerializeField] private Transform[] eventSpawnPoints;
    // Level-authored polygons (can be non-convex — see SpawnArea's own doc comment) for events that scatter
    // MULTIPLE things (e.g. DestroyTheTargetsPD's dummies). Multiple named areas can exist per level; an
    // event opts into "anywhere across any area" or "confined to one randomly-picked area" via
    // BeginSpawnAreaSelection below — it never hand-picks a SPECIFIC area itself, only the mode.
    [SerializeField] private SpawnArea[] spawnAreas;
    // One shared patrol loop for the whole level, walked by PerimeterPatrolGloomPD and spaced onto by
    // PerimeterPatrolEventPD (via PlayableAreaPD.GetLoopLength/GetPointAtArcLength, already built generically
    // against any Vector3[] loop) — replaces the old GetInsetPerimeter(wallOffset)-derived path.
    [SerializeField] private Transform[] patrolGraph;

    // Lazily built once — spawnAreas is static level-authored data, never changes at runtime, so there's no
    // invalidation logic needed beyond "computed yet or not." Index i's value is the CUMULATIVE polygon area
    // (PlayableAreaPD.PolygonArea, true shoelace-formula area, not a bounding-box proxy) of every area up to
    // and including i — PickWeightedRandomAreaIndex rolls against this table so bigger areas are
    // proportionally more likely to be picked in "anywhere across any area" mode, per explicit design (a
    // tiny area and a huge one should not have equal odds).
    private float[] _cachedAreaCumulativeWeights;

    // Lazily built once, same "fixed author-time geometry, single-entry cache" convention
    // PlayableAreaPD.GetInsetPerimeter already uses for its own cached array.
    private Vector3[] _cachedPatrolGraph;

    // Returned by BeginSpawnAreaSelection, consumed by TryGetSpawnAreaPoint — see that method's own doc
    // comment for the confined-vs-anywhere contract.
    public readonly struct SpawnAreaSelection
    {
        public readonly bool confined;
        public readonly int lockedAreaIndex; // meaningful only when confined == true; -1 if none could be locked
        public SpawnAreaSelection(bool confined, int lockedAreaIndex)
        {
            this.confined = confined;
            this.lockedAreaIndex = lockedAreaIndex;
        }
    }

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        // Default(PredictedObjectID) is the "no event currently active" sentinel — same unguarded
        // default-sentinel convention every other PredictedObjectID/PlayerID field in this codebase already
        // uses (e.g. KingOrbiterPD.State.lastShotTargetId).
        public PredictedObjectID activeEventId;
        public void Dispose() { }
        public State Duplicate() => this;
    }

    protected override void LateAwake() => instance = this;

    // Nothing to simulate here directly — the active event's own PredictedIdentity runs its own Simulate()
    // independently once spawned. An event that wants to resolve EARLY (before EnemySpawnerPD's own flat
    // eventDuration timeout) just calls EventManagerPD.instance.ResolveEvent() on itself directly, the
    // instant it succeeds (see CaptureThePointPD.Simulate() for the precedent, and ResolveEvent()'s own doc
    // comment for why that's safe — it never destroys anything synchronously, so there's no same-tick
    // self-deletion hazard in doing that).
    protected override void Simulate(ref State state, float delta) { }

    // Called by EnemySpawnerPD once per Tier Cycle, at the halfway point of its pre-boss Quarters window —
    // see EnemySpawnerPD's own "Mid-cycle event trigger" doc comment. Picks a random event prefab and spawns
    // it, then calls its own OnEventStart() hook. hierarchy.Create() (not plain Instantiate()) — deterministic
    // and idempotent under replay, same reasoning every other Simulate()-context spawn in this codebase uses.
    // currentTier — EnemySpawnerPD's own _currentTierNumber at the moment it decided to trigger this event
    // (see its own "Mid-cycle event trigger" doc comment for the call site) — threaded straight through to
    // every event's own OnEventStart unconditionally, see IEventPD.OnEventStart's own doc comment for why.
    public void StartEvent(int currentTier)
    {
        if (eventPrefabs == null || eventPrefabs.Length == 0) return;

        var prefab = eventPrefabs[GameRandom.instance.Next(eventPrefabs.Length)];
        var id = hierarchy.Create(prefab, transform.position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go == null || !go.TryGetComponent(out IEventPD ev)) return;

        currentState.activeEventId = id.Value;
        ev.OnEventStart(PlayableAreaPD.instance, currentTier);
    }

    // Called from TWO places — remember this whenever changing it: (1) EnemySpawnerPD, a flat, hardcoded 1
    // minute (eventDuration) after StartEvent(), for the "ran out of time" case; (2) the active event itself,
    // directly, the instant it decides it succeeded early (see CaptureThePointPD.Simulate() for the
    // precedent). Reads the active event's own Succeeded flag (defaults false — see EventBasePD's own doc
    // comment for why that's already correct for a timed-out resolve), triggers the pseudo-level-up bonus
    // augment round on success, then runs Cleanup() + MarkResolved() — see EventBasePD's own class-level doc
    // comment for why this does NOT destroy the event itself here (that's deferred to verified state, in
    // EventBasePD's own UpdateView).
    //
    // Idempotent by construction, not by an extra explicit guard: the FIRST call to actually find a live
    // event (whichever of the two callers gets there first) clears activeEventId to default at its own end,
    // so the OTHER caller's later call finds hierarchy.GetGameObject(default) returning null and is a
    // harmless no-op — same sentinel convention this codebase already relies on elsewhere (e.g.
    // KingOrbiterPD's own default-PredictedObjectID checks).
    public void ResolveEvent()
    {
        var go = hierarchy.GetGameObject(currentState.activeEventId);
        if (go != null && go.TryGetComponent(out IEventPD ev))
        {
            if (ev.Succeeded && AugmentManagerPD.instance != null)
                AugmentManagerPD.instance.TriggerBonusAugmentRound();

            ev.Cleanup();
            ev.MarkResolved();
        }

        currentState.activeEventId = default;
    }

    // ── Hand-authored spawn data (2026-08-27) ────────────────────────────────

    // Called ONCE per event activation, not per spawn point — this is what makes mode selection "decided
    // once, not re-decided per individual point." confineToOneArea locks a single (size-weighted) random
    // area index for the WHOLE event's spawn batch; false means every subsequent TryGetSpawnAreaPoint call
    // independently re-rolls its own (also size-weighted) area. Falls back to "not confined" if there's
    // nothing to lock onto (no spawnAreas authored) rather than returning a meaningless locked index.
    public SpawnAreaSelection BeginSpawnAreaSelection(bool confineToOneArea)
    {
        if (!confineToOneArea || spawnAreas == null || spawnAreas.Length == 0)
            return new SpawnAreaSelection(false, -1);
        return new SpawnAreaSelection(true, PickWeightedRandomAreaIndex());
    }

    // avoidPlayers/playerOffset/footprintRadius: same "don't spawn on a player" contract
    // PlayableAreaPD.RandomPoint's own avoidPlayers param already has — reuses IsFarEnoughFromEveryPlayer
    // directly rather than a third copy of that check. Best-effort: if avoidPlayers rejects every in-polygon
    // candidate within maxAttempts, falls back to the LAST candidate that was genuinely inside a polygon
    // (never returns true with an undefined point, never silently fails just because a player happened to be
    // standing on every sampled spot this exact attempt).
    public bool TryGetSpawnAreaPoint(SpawnAreaSelection selection, out Vector3 point, bool avoidPlayers = false, float playerOffset = 0f, float footprintRadius = 0f, int maxAttempts = 30)
    {
        point = default;
        if (spawnAreas == null || spawnAreas.Length == 0) return false;

        Vector3 fallback = default;
        bool haveFallback = false;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int areaIndex = selection.confined ? selection.lockedAreaIndex : PickWeightedRandomAreaIndex();
            if (areaIndex < 0 || areaIndex >= spawnAreas.Length) continue;
            if (!PlayableAreaPD.TryRandomPointInPolygon(spawnAreas[areaIndex].points, out Vector3 candidate)) continue;

            if (!haveFallback) { fallback = candidate; haveFallback = true; }

            if (avoidPlayers && !PlayableAreaPD.IsFarEnoughFromEveryPlayer(new Vector2(candidate.x, candidate.z), footprintRadius + playerOffset))
                continue;

            point = candidate;
            return true;
        }

        if (haveFallback) { point = fallback; return true; }
        return false;
    }

    // Lazily sized to eventSpawnPoints.Length, reshuffled fresh every TryGetEventSpawnPoint call (Fisher-
    // Yates in place) — reused rather than reallocated, same "persistent scratch collection" convention
    // DestroyTheTargetsPD._placedPositionsScratch already establishes.
    private int[] _eventSpawnPointOrderScratch;

    // Verbatim pick from eventSpawnPoints (see that field's own doc comment for why this is NOT edge-
    // interpolated). Same avoidPlayers contract as TryGetSpawnAreaPoint above — but DETERMINISTIC
    // (2026-08-27, was random-with-replacement over maxAttempts): tries every distinct point EXACTLY ONCE,
    // in a fresh random order each call, rather than repeatedly re-rolling the same small array — with only
    // a handful of authored points (e.g. 9), random-with-replacement had a real, if small, chance of never
    // sampling a specific valid point at all within a fixed attempt budget, while redundantly re-trying
    // others. A Fisher-Yates shuffle over the actual point count guarantees every option gets checked.
    public bool TryGetEventSpawnPoint(out Vector3 point, bool avoidPlayers = false, float playerOffset = 0f, float footprintRadius = 0f)
    {
        point = default;
        if (eventSpawnPoints == null || eventSpawnPoints.Length == 0) return false;

        if (_eventSpawnPointOrderScratch == null || _eventSpawnPointOrderScratch.Length != eventSpawnPoints.Length)
        {
            _eventSpawnPointOrderScratch = new int[eventSpawnPoints.Length];
            for (int i = 0; i < _eventSpawnPointOrderScratch.Length; i++) _eventSpawnPointOrderScratch[i] = i;
        }

        for (int i = _eventSpawnPointOrderScratch.Length - 1; i > 0; i--)
        {
            int j = GameRandom.instance.Next(i + 1);
            (_eventSpawnPointOrderScratch[i], _eventSpawnPointOrderScratch[j]) = (_eventSpawnPointOrderScratch[j], _eventSpawnPointOrderScratch[i]);
        }

        Vector3 fallback = default;
        bool haveFallback = false;

        for (int k = 0; k < _eventSpawnPointOrderScratch.Length; k++)
        {
            var t = eventSpawnPoints[_eventSpawnPointOrderScratch[k]];
            if (t == null) continue;
            Vector3 candidate = t.position;

            if (!haveFallback) { fallback = candidate; haveFallback = true; }

            if (avoidPlayers && !PlayableAreaPD.IsFarEnoughFromEveryPlayer(new Vector2(candidate.x, candidate.z), footprintRadius + playerOffset))
                continue;

            point = candidate;
            return true;
        }

        if (haveFallback) { point = fallback; return true; }
        return false;
    }

    // Converts patrolGraph (Transform[]) to Vector3[] once and caches it. Returns Array.Empty<Vector3>() —
    // never null — if patrolGraph is null/empty/contains a null entry, so callers can always safely check
    // .Length without an extra null guard.
    public Vector3[] GetPatrolGraph()
    {
        if (_cachedPatrolGraph != null) return _cachedPatrolGraph;

        if (patrolGraph == null || patrolGraph.Length == 0)
        {
            _cachedPatrolGraph = System.Array.Empty<Vector3>();
            return _cachedPatrolGraph;
        }

        var result = new Vector3[patrolGraph.Length];
        for (int i = 0; i < patrolGraph.Length; i++)
        {
            if (patrolGraph[i] == null)
            {
                _cachedPatrolGraph = System.Array.Empty<Vector3>();
                return _cachedPatrolGraph;
            }
            result[i] = patrolGraph[i].position;
        }
        _cachedPatrolGraph = result;
        return _cachedPatrolGraph;
    }

    private void EnsureAreaWeightsCached()
    {
        if (_cachedAreaCumulativeWeights != null) return;
        int count = spawnAreas != null ? spawnAreas.Length : 0;
        _cachedAreaCumulativeWeights = new float[count];
        float running = 0f;
        for (int i = 0; i < count; i++)
        {
            running += PlayableAreaPD.PolygonArea(spawnAreas[i].points);
            _cachedAreaCumulativeWeights[i] = running;
        }
    }

    // Rolls against the cumulative-weight table built by EnsureAreaWeightsCached — a spawn area with double
    // the true polygon area of another is twice as likely to be picked. Falls back to a uniform pick if every
    // area came out zero-area (a degenerate/mis-authored SpawnArea, e.g. fewer than 3 points) rather than
    // dividing by zero.
    private int PickWeightedRandomAreaIndex()
    {
        EnsureAreaWeightsCached();
        int count = _cachedAreaCumulativeWeights.Length;
        if (count == 0) return -1;

        float total = _cachedAreaCumulativeWeights[count - 1];
        if (total <= 0f) return GameRandom.instance.Next(count);

        float roll = GameRandom.instance.NextFloat(0f, total);
        for (int i = 0; i < count; i++)
            if (roll <= _cachedAreaCumulativeWeights[i]) return i;
        return count - 1; // floating-point tail edge case
    }
}
