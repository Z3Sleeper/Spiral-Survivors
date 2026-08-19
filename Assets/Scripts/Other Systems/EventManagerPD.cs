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
    // independently once spawned.
    protected override void Simulate(ref State state, float delta) { }

    // Called by EnemySpawnerPD once per Tier Cycle, at the halfway point of its pre-boss Quarters window —
    // see EnemySpawnerPD's own "Mid-cycle event trigger" doc comment. Picks a random event prefab and spawns
    // it, then calls its own OnEventStart() hook. hierarchy.Create() (not plain Instantiate()) — deterministic
    // and idempotent under replay, same reasoning every other Simulate()-context spawn in this codebase uses.
    public void StartEvent()
    {
        if (eventPrefabs == null || eventPrefabs.Length == 0) return;

        var prefab = eventPrefabs[GameRandom.instance.Next(eventPrefabs.Length)];
        var id = hierarchy.Create(prefab, transform.position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go == null || !go.TryGetComponent(out IEventPD ev)) return;

        currentState.activeEventId = id.Value;
        ev.OnEventStart();
    }

    // Called by EnemySpawnerPD a flat, hardcoded 1 minute (eventDuration) after StartEvent() — see the same
    // doc comment. Reads the active event's own Succeeded flag (defaults false — see EventBasePD's own doc
    // comment for why that's already correct for an early/timed-out resolve), triggers the pseudo-level-up
    // bonus augment round on success, lets the event clean up anything it spawned via OnEventResolve(), then
    // despawns the event itself.
    public void ResolveEvent()
    {
        var go = hierarchy.GetGameObject(currentState.activeEventId);
        if (go != null && go.TryGetComponent(out IEventPD ev))
        {
            if (ev.Succeeded && AugmentManagerPD.instance != null)
                AugmentManagerPD.instance.TriggerBonusAugmentRound();

            ev.OnEventResolve();
            hierarchy.Delete(go);
        }

        currentState.activeEventId = default;
    }
}
