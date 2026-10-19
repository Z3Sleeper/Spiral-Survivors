using PurrNet.Prediction;

// Dedicated deterministic RNG stream for the mid-cycle event system (2026-10-16) — same shape/precedent as
// AugmentRandom (Assets/Augments and Level Up Stuff/AugmentRandom.cs), just scoped to event
// selection/placement instead of augment offers. GameRandom is one shared, global, order-sensitive stream
// drawn from by every enemy and every event every tick — EventManagerPD.State.eventSequence's own doc
// comment already covers the first half of this bug class (which event fires, fixed by precomputing the
// whole sequence once at match start, when the stream is still trivially in sync). This closes the SECOND
// half: WHERE an event's own entities spawn is rolled live, at actual trigger time (not precomputable —
// depends on live obstacle/player positions), via PlayableAreaPD.RandomPoint's own rejection-sampling loop.
// That loop draws a VARIABLE number of values per call (keeps resampling until distance/polygon/wall-margin
// constraints are satisfied), which makes it unusually sensitive to any stream-position drift — confirmed
// live: a client saw a DestroyTheTargets Spire spawn in a wrong, later-corrected position, one even jumping
// to the opposite side of the map, consistent with the whole accept/reject sequence cascading differently
// off a shifted starting position. Routing every event-system roll (EventManagerPD's own picks,
// PlayableAreaPD.RandomPoint, PerimeterPatrolEventPD's patrol-start-arc roll) through this dedicated stream
// means nothing outside the event system itself — no enemy cooldown jitter, no player combat roll, nothing —
// can ever shift its draw position.
public class EventRandom : PredictedRandomSystem
{
    public static EventRandom instance { get; private set; }

    protected override void LateAwake()
    {
        base.LateAwake();
        instance = this;
    }
}
