using PurrNet.Packing;
using PurrNet.Prediction;

// Shared contract every concrete event type implements (2026-08-24) — mirrors EnemyBasePD<TState>/IEnemyPD's
// own "generic interface + shared base-state struct" shape exactly, so EventManagerPD can spawn/track/resolve
// ANY event type generically without knowing its concrete type, same as EnemySpawnerPD does for enemies via
// IEnemyPD. The ONLY guaranteed contract across every event (per explicit design — events themselves will be
// "completely unrelated," doing arbitrary things, possibly spawning their own enemies/hazards): it starts the
// moment EventManagerPD.StartEvent() spawns it (OnEventStart()), cleans itself up when EventManagerPD.
// ResolveEvent() ends it (OnEventResolve()), and carries a `succeeded` bool defaulting to false — so an event
// resolved early (EnemySpawnerPD's own flat eventDuration timeout, before the event's own logic ever set
// succeeded = true) is already correctly "failed" with no extra handling needed anywhere.
public interface IEventPD
{
    bool Succeeded { get; }
    PredictedObjectID ObjectId { get; }
    // Called once by EventManagerPD.StartEvent() immediately after spawning — the concrete event's own
    // "begin doing my actual event-specific thing" hook (spawn hazards/enemies, arm a timer, whatever this
    // particular event needs).
    void OnEventStart();
    // Called once by EventManagerPD.ResolveEvent() right before it despawns this event — the concrete
    // event's own chance to tear down anything IT spawned (its own hazards/enemies/etc.), independent of
    // whether it succeeded or not. Reading Succeeded is still valid here if a concrete event needs to branch
    // its own cleanup on the outcome.
    void OnEventResolve();
}

public struct EventBaseState : IPredictedData<EventBaseState>, IDuplicate<EventBaseState>
{
    // Defaults to false — see this file's own class-level doc comment for why that's exactly the correct
    // "not yet succeeded" starting point, including for an event resolved early via timeout.
    public bool succeeded;
    public void Dispose() { }
    public EventBaseState Duplicate() => this;
}

public abstract class EventBasePD<TState> : PredictedIdentity<TState>, IEventPD
    where TState : struct, IPredictedData<TState>
{
    protected abstract ref EventBaseState GetBase(ref TState state);

    // Shorthand for accessing base fields on currentState — same convention as EnemyBasePD.B.
    protected ref EventBaseState B => ref GetBase(ref currentState);

    public bool Succeeded => B.succeeded;
    // Same "trivial passthrough" shape as IEnemyPD.ObjectId's own implementation on EnemyBasePD.
    public PredictedObjectID ObjectId => id.objectId;

    public abstract void OnEventStart();
    public virtual void OnEventResolve() { }
}
