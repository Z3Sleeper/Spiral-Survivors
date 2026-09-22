using PurrNet.Packing;
using PurrNet.Pooling;
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

    // Hand-authored spawn geometry (2026-08-27) lives on a SEPARATE plain-MonoBehaviour component,
    // EventSpawnDataPD, referenced here by a single serialized field — see that class's own doc comment for
    // why: EventManagerPD is a PredictedIdentity, and PurrDiction's own PredictedIdentityEditor throws
    // "Unsupported type Vector2f"/"Vector3f" on ANY array-of-struct field declared directly on a
    // PredictedIdentity (confirmed live trying both types) — a single Object-reference field like this one is
    // the kind of field that editor is confirmed to handle fine (same reason the old Transform[] arrays,
    // themselves arrays of Object references, never had this problem).
    [SerializeField] private EventSpawnDataPD spawnData;

    // Lazily built once, same "fixed author-time geometry, single-entry cache" convention
    // PlayableAreaPD.GetInsetPerimeter already uses for its own cached array.
    private Vector3[] _cachedPatrolGraph;

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        // Default(PredictedObjectID) is the "no event currently active" sentinel — same unguarded
        // default-sentinel convention every other PredictedObjectID/PlayerID field in this codebase already
        // uses (e.g. KingOrbiterPD.State.lastShotTargetId).
        public PredictedObjectID activeEventId;
        // Predetermined ONCE, on this identity's very first real Simulate() tick (see GenerateEventSequence's
        // own doc comment) — indices into eventPrefabs, in the exact order StartEvent() will hand them out.
        // Root-cause fix (2026-09-17) for a real bug: StartEvent() used to draw a LIVE GameRandom.Next() pick
        // at the moment each event actually started, which is vulnerable to ordinary GameRandom stream-
        // position drift — anything else earlier in the SAME tick drawing a different number of values between
        // a non-host client's speculative pass and the eventual server-confirmed replay (e.g. EnemySpawnerPD's
        // own spawn rolls, which run before the event trigger in its own Simulate()) shifts this draw's own
        // stream position, landing on a DIFFERENT event index — not a bug in GameRandom itself (same call
        // sequence always gives the same result), just unavoidable drift for any draw sitting downstream of
        // other conditional draws in the same tick. Confirmed live: a client briefly spawned/showed an entirely
        // wrong event (including its own spawned entities, e.g. DPSCheck's hazard) before a later resimulation
        // corrected it to match the host. Deciding the WHOLE sequence once, at match start (when GameRandom's
        // stream position is still trivially synced — almost nothing has run yet), means StartEvent() is later
        // just an index lookup into already-agreed, already-replicated data — nothing left to mispredict.
        public DisposableList<int> eventSequence;
        // How many entries of eventSequence have been consumed so far (StartEvent() calls) — same "running
        // cursor into a State-backed list" shape EnemySpawnerPD's own currentSetIndices/previousSetIndices
        // already use elsewhere.
        public int nextEventSequenceIndex;
        public void Dispose() => eventSequence.Dispose();
        public State Duplicate()
        {
            var copy = this;
            copy.eventSequence = eventSequence.Duplicate();
            return copy;
        }
    }

    protected override State GetInitialState() => new()
    {
        eventSequence = DisposableList<int>.Create(4),
    };

    protected override void LateAwake() => instance = this;

    // Only job here: generate eventSequence exactly once, on the very first real tick — see its own doc
    // comment for the full reasoning. Count == 0 is an unambiguous "not yet generated" sentinel (never
    // legitimately empty again once populated, given a non-empty eventPrefabs), so this can never
    // accidentally re-run later. Otherwise nothing to simulate here directly — the active event's own
    // PredictedIdentity runs its own Simulate() independently once spawned. An event that wants to resolve
    // EARLY (before EnemySpawnerPD's own flat eventDuration timeout) just calls
    // EventManagerPD.instance.ResolveEvent() on itself directly, the instant it succeeds (see
    // CaptureThePointPD.Simulate() for the precedent, and ResolveEvent()'s own doc comment for why that's
    // safe — it never destroys anything synchronously, so there's no same-tick self-deletion hazard in doing
    // that).
    protected override void Simulate(ref State state, float delta)
    {
        if (state.eventSequence.Count == 0 && eventPrefabs != null && eventPrefabs.Length > 0)
            GenerateEventSequence(ref state);
    }

    // Full Fisher-Yates shuffle of eventPrefabs' own indices (same shuffle shape TryGetEventSpawnPoint's own
    // _eventSpawnPointOrderScratch already uses), keeping only the first `count` — count is however many Tier
    // Cycles a run can ever have (EnemySpawnerPD.BossesToDefeatForWin, the real source of truth — a win fires
    // the instant that many bosses are defeated, and a Tier Cycle always ends in one), capped to however many
    // distinct event prefabs actually exist if there are fewer than that. Guarantees every event that fires
    // this run is unique, up to that limit.
    private void GenerateEventSequence(ref State state)
    {
        int maxEvents = EnemySpawnerPD.instance != null ? EnemySpawnerPD.instance.BossesToDefeatForWin : eventPrefabs.Length;
        int count = Mathf.Min(maxEvents, eventPrefabs.Length);

        var indices = new int[eventPrefabs.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        for (int i = indices.Length - 1; i > 0; i--)
        {
            int j = GameRandom.instance.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        for (int i = 0; i < count; i++)
            state.eventSequence.Add(indices[i]);
    }

    // Called by EnemySpawnerPD once per Tier Cycle, at the halfway point of its pre-boss Quarters window —
    // see EnemySpawnerPD's own "Mid-cycle event trigger" doc comment. Picks a random event prefab and spawns
    // it, then calls its own OnEventStart() hook. hierarchy.Create() (not plain Instantiate()) — deterministic
    // and idempotent under replay, same reasoning every other Simulate()-context spawn in this codebase uses.
    // currentTier — EnemySpawnerPD's own _currentTierNumber at the moment it decided to trigger this event
    // (see its own "Mid-cycle event trigger" doc comment for the call site) — threaded straight through to
    // every event's own OnEventStart unconditionally, see IEventPD.OnEventStart's own doc comment for why.
    //
    // Defensive precaution (2026-09-02): resolves whatever event is still active FIRST, before spawning a
    // new one — closes the "no defined interaction" gap EnemySpawnerPD.eventDuration's own doc comment
    // flags, where a mistakenly-long eventDuration could let this get called again while the previous
    // event's own resolve timer hasn't expired yet, silently orphaning it (never Cleanup()'d/MarkResolved()'d,
    // activeEventId just overwritten out from under it) and leaving EnemySpawnerPD's own single
    // eventResolveTimer resolving whichever event happens to be tracked by the time it expires, not
    // necessarily the one it actually started counting down for. Safe to call unconditionally even when
    // nothing is actually active — ResolveEvent() is already idempotent by construction (see its own doc
    // comment), so this is a harmless no-op in the normal case where the previous event already resolved
    // itself well before this ever runs again.
    public void StartEvent(int currentTier)
    {
        if (eventPrefabs == null || eventPrefabs.Length == 0) return;

        ResolveEvent();

        // Reads the NEXT entry off the sequence GenerateEventSequence already predetermined at match start —
        // see State.eventSequence's own doc comment for why this, not a live GameRandom draw here, is what
        // actually fixes the misprediction. Wraps via modulo purely as a defensive fallback (never the
        // expected case today) if this is ever called more times than eventSequence has entries — repeats
        // rather than throwing. The GameRandom.Next fallback below only matters if eventSequence somehow
        // never got generated at all (eventPrefabs empty at match start, then populated later — not a normal
        // scenario).
        int sequenceIndex = currentState.eventSequence.Count > 0
            ? currentState.eventSequence[currentState.nextEventSequenceIndex % currentState.eventSequence.Count]
            : GameRandom.instance.Next(eventPrefabs.Length);
        currentState.nextEventSequenceIndex++;

        var prefab = eventPrefabs[sequenceIndex];
        var id = hierarchy.Create(prefab, transform.position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go == null || !go.TryGetComponent(out IEventPD ev)) return;

        currentState.activeEventId = id.Value;
        ev.OnEventStart(PlayableAreaPD.instance, currentTier);
    }

    // Called from THREE places — remember this whenever changing it: (1) EnemySpawnerPD, a flat, hardcoded 1
    // minute (eventDuration) after StartEvent(), for the "ran out of time" case; (2) the active event itself,
    // directly, the instant it decides it succeeded early (see CaptureThePointPD.Simulate() for the
    // precedent); (3) StartEvent() itself, defensively, right before spawning a new event (see its own doc
    // comment for the orphaned-event gap this closes). Reads the active event's own Succeeded flag (defaults
    // false — see EventBasePD's own doc comment for why that's already correct for a timed-out resolve),
    // triggers the pseudo-level-up bonus augment round on success, then runs Cleanup() + MarkResolved() — see
    // EventBasePD's own class-level doc comment for why this does NOT destroy the event itself here (that's
    // deferred to verified state, in EventBasePD's own UpdateView).
    //
    // Idempotent by construction, not by an extra explicit guard: the FIRST call to actually find a live
    // event (whichever of the three callers gets there first) clears activeEventId to default at its own end,
    // so any OTHER caller's later call finds hierarchy.GetGameObject(default) returning null and is a
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

    // Lazily sized to eventSpawnPoints.Length, ALLOCATION reused across calls (avoids GC churn) but CONTENT
    // unconditionally reset to [0,1,2,...] at the top of every TryGetEventSpawnPoint call before shuffling —
    // see that method's own comment for why the reset can't be skipped even when the array's already the
    // right length. Same "persistent scratch collection" allocation-reuse convention
    // DestroyTheTargetsPD._placedPositionsScratch establishes, but that field ALSO fully resets its own
    // CONTENT (via .Clear()) every call — the allocation-reuse trick is only safe to copy if the content-reset
    // comes with it.
    private int[] _eventSpawnPointOrderScratch;

    // Verbatim pick from eventSpawnPoints (see that field's own doc comment for why this is NOT edge-
    // interpolated). Same avoidPlayers contract as TryGetSpawnAreaPoint above — but DETERMINISTIC
    // (2026-08-27, was random-with-replacement over maxAttempts): tries every distinct point EXACTLY ONCE,
    // in a fresh random order each call, rather than repeatedly re-rolling the same small array — with only
    // a handful of authored points (e.g. 9), random-with-replacement had a real, if small, chance of never
    // sampling a specific valid point at all within a fixed attempt budget, while redundantly re-trying
    // others. A Fisher-Yates shuffle over the actual point count guarantees every option gets checked.
    //
    // Simulation-safety note (2026-08-28 fix): _eventSpawnPointOrderScratch's CONTENT is reset to
    // [0,1,2,...] unconditionally every call, not just when its length changes — this is a PLAIN field, not
    // predicted State, so it does NOT roll back on a resimulation replay the way State would. If the shuffle
    // below were applied on top of whatever order a PREVIOUS call happened to leave the array in (rather
    // than a fixed, call-independent baseline), the result would depend on this field's entire mutation
    // history rather than purely on this call's own GameRandom draws — meaning a replay of the same tick
    // could produce a DIFFERENT pick than the original prediction did, even though GameRandom itself replays
    // identically, since this array's accumulated-but-unrolled-back state wouldn't match. Resetting to the
    // same baseline every time makes the shuffle a pure function of (point count, this call's own RNG
    // draws) — nothing else — which is what actually makes it replay-safe.
    public bool TryGetEventSpawnPoint(out Vector3 point, bool avoidPlayers = false, float playerOffset = 0f, float footprintRadius = 0f)
    {
        point = default;
        var eventSpawnPoints = spawnData != null ? spawnData.EventSpawnPoints : null;
        if (eventSpawnPoints == null || eventSpawnPoints.Length == 0) return false;

        if (_eventSpawnPointOrderScratch == null || _eventSpawnPointOrderScratch.Length != eventSpawnPoints.Length)
            _eventSpawnPointOrderScratch = new int[eventSpawnPoints.Length];
        for (int i = 0; i < _eventSpawnPointOrderScratch.Length; i++) _eventSpawnPointOrderScratch[i] = i;

        for (int i = _eventSpawnPointOrderScratch.Length - 1; i > 0; i--)
        {
            int j = GameRandom.instance.Next(i + 1);
            (_eventSpawnPointOrderScratch[i], _eventSpawnPointOrderScratch[j]) = (_eventSpawnPointOrderScratch[j], _eventSpawnPointOrderScratch[i]);
        }

        Vector3 fallback = default;
        bool haveFallback = false;

        for (int k = 0; k < _eventSpawnPointOrderScratch.Length; k++)
        {
            Vector3 p = eventSpawnPoints[_eventSpawnPointOrderScratch[k]];
            Vector3 candidate = new Vector3(p.x, 0f, p.z);

            if (!haveFallback) { fallback = candidate; haveFallback = true; }

            if (avoidPlayers && !PlayableAreaPD.IsFarEnoughFromEveryPlayer(new Vector2(candidate.x, candidate.z), footprintRadius + playerOffset))
                continue;

            point = candidate;
            return true;
        }

        if (haveFallback) { point = fallback; return true; }
        return false;
    }

    // Normalizes patrolGraph's own Y to 0 (ignoring whatever was actually authored there — see field's own
    // comment) once and caches it. Returns Array.Empty<Vector3>() — never null — if patrolGraph is null/empty,
    // so callers can always safely check .Length without an extra null guard.
    public Vector3[] GetPatrolGraph()
    {
        if (_cachedPatrolGraph != null) return _cachedPatrolGraph;

        var patrolGraph = spawnData != null ? spawnData.PatrolGraph : null;
        if (patrolGraph == null || patrolGraph.Length == 0)
        {
            _cachedPatrolGraph = System.Array.Empty<Vector3>();
            return _cachedPatrolGraph;
        }

        var result = new Vector3[patrolGraph.Length];
        for (int i = 0; i < patrolGraph.Length; i++)
            result[i] = new Vector3(patrolGraph[i].x, 0f, patrolGraph[i].z);
        _cachedPatrolGraph = result;
        return _cachedPatrolGraph;
    }

    // Scene-view visualization (patrol graph, event spawn point radii) now lives on
    // EventSpawnDataPD itself, alongside the data it draws — see that class's own doc comment for why this
    // data moved off EventManagerPD entirely (PredictedIdentityEditor's own Vector2f/Vector3f limitation).
}
