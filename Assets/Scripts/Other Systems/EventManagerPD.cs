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
}
