using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// "Destroy the Spires" — DPSCheckPD's multi-target sibling. Spawns dummyCount DummyTargetPD instances, each
// placed independently within EventManagerPD's own hand-authored spawnAreas (2026-08-27 — was
// PlayableAreaPD.RandomPoint against the whole arena; see PlayableAreaPD.GetInsetPerimeter's own doc comment
// for why that whole procedural-placement approach was abandoned project-wide), and succeeds once EVERY one
// of them has died. See DPSCheckPD's own class-level doc comment for the shared reasoning this mirrors
// (dummy-as-genuine-IEnemyPD, tier/difficulty/coop scaling via EventTargetScalingPD, SilentDespawn-based
// Cleanup) — only the differences worth calling out are covered here.
//
// Spawn-area mode: confineToOneArea (default false — "anywhere across all areas", confirmed explicit design)
// is decided ONCE per event activation via EventManagerPD.BeginSpawnAreaSelection, never per individual
// dummy — see that method's own doc comment. This event never hand-picks a SPECIFIC area itself either way.
//
// Placement spread: each dummy after the first samples spreadSamples independent candidates from
// EventManagerPD.TryGetSpawnAreaPoint and keeps whichever ends up FARTHEST from every dummy THIS event has
// already placed, subject to a hard minSeparationDistance floor layered underneath — see PickSpreadPosition's
// own doc comment for the exact floor-plus-bias shape.
//
// Progress: unlike DPSCheckPD (which reads one dummy's live CurrentHealth/MaxHealth directly, no State
// mirroring needed), each dummy's own MaxHealth gets SNAPSHOTTED once, into predicted State, right after
// ConfigureSpawn — see dummyMaxHealthSnapshots' own doc comment for why a live re-read per dummy would
// actually be WRONG here: a dummy that's already died and been deleted would silently drop out of the total
// denominator, making the overall percentage jump BACKWARD the instant its corpse gets cleaned up.
public class DestroyTheTargetsPD : EventBasePD<DestroyTheTargetsPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EventBaseState b;
        public DisposableList<PredictedObjectID> dummyIds;
        // Snapshotted once per dummy (same index as dummyIds), right after ConfigureSpawn/tier-scaling — see
        // this class's own doc comment for why this can't just be re-read live off each dummy's own
        // MaxHealth every frame.
        public DisposableList<float> dummyMaxHealthSnapshots;
        public void Dispose()
        {
            dummyIds.Dispose();
            dummyMaxHealthSnapshots.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.dummyIds = dummyIds.Duplicate();
            copy.dummyMaxHealthSnapshots = dummyMaxHealthSnapshots.Duplicate();
            return copy;
        }
    }

    protected override ref EventBaseState GetBase(ref State state) => ref state.b;

    protected override State GetInitialState() => new()
    {
        dummyIds = DisposableList<PredictedObjectID>.Create(dummyCount),
        dummyMaxHealthSnapshots = DisposableList<float>.Create(dummyCount),
    };

    [Header("Target")]
    // Plain GameObject (2026-09-02, was DummyTargetPD directly) — DummyTargetPD is now a generic base
    // (DummyTargetPD<TState>, see its own doc comment) with multiple concrete variants
    // (DummyTargetPD/KnockbackDummyTargetPD/any future one), each specializing its own predicted State, so
    // there's no single concrete class every variant IS anymore — same reasoning every real enemy prefab
    // reference in this codebase is already GameObject-typed, not EnemyBasePD-typed. Only ever used for its
    // GameObject (hierarchy.Create() below) and then resolved back to IEnemyPD off the spawned instance,
    // same as before.
    [SerializeField] private GameObject dummyPrefab;
    [SerializeField] private int dummyCount = 5;

    [Header("Placement")]
    [SerializeField] private float zoneRadius = 1f;
    // How many independent candidate points to sample per dummy (after the first) before picking whichever
    // ends up farthest from every ALREADY-PLACED dummy this same event has spawned so far — see
    // PickSpreadPosition's own doc comment.
    [SerializeField] private int spreadSamples = 5;
    // false (confirmed default): every dummy's spawn area is independently re-rolled — a batch can scatter
    // across several different authored spawnAreas. true: one area is picked ONCE for the whole batch, and
    // every dummy lands somewhere inside just that single area. See EventManagerPD.BeginSpawnAreaSelection's
    // own doc comment.
    [SerializeField] private bool confineToOneArea = false;
    // Hard reject floor, layered UNDER the existing spreadSamples max-spread bias above (not a replacement
    // for it) — see PickSpreadPosition's own doc comment for how the two combine. "Obvious space limitation
    // we'll probably never realistically hit" per explicit design — degrades gracefully rather than erroring
    // if a tiny area/large dummyCount combination ever makes it unsatisfiable.
    [SerializeField] private float minSeparationDistance = 2f;

    // Reused across every OnEventStart call (2026-08-21) rather than a fresh List<> each time — same
    // "persistent scratch collection, cleared and reused" convention KingOrbiterPD._grabCandidates already
    // establishes for this exact kind of per-call candidate scratch work.
    private readonly List<Vector3> _placedPositionsScratch = new();

    public override void OnEventStart(PlayableAreaPD playableArea, int currentTier)
    {
        // Shows the shared event description panel — see EventBasePD.OnEventStart's own doc comment.
        base.OnEventStart(playableArea, currentTier);

        currentState.dummyIds.Clear();
        currentState.dummyMaxHealthSnapshots.Clear();
        currentState.b.succeeded = false;
        _placedPositionsScratch.Clear();

        if (dummyPrefab == null) return;

        // Decided ONCE for the whole batch, not per dummy — see this field's own doc comment.
        var selection = EventManagerPD.instance != null
            ? EventManagerPD.instance.BeginSpawnAreaSelection(confineToOneArea)
            : new EventManagerPD.SpawnAreaSelection(false, -1);

        for (int i = 0; i < dummyCount; i++)
        {
            Vector3 spawnPos = EventManagerPD.instance != null ? PickSpreadPosition(selection) : transform.position;
            _placedPositionsScratch.Add(spawnPos);

            var id = hierarchy.Create(dummyPrefab, spawnPos, Quaternion.identity);
            if (!id.HasValue) continue;

            currentState.dummyIds.Add(id.Value);

            float maxHealth = 0f;
            var go = hierarchy.GetGameObject(id.Value);
            if (go != null && go.TryGetComponent(out IEnemyPD dummy))
            {
                EventTargetScalingPD.ApplyTierScaling(dummy, currentTier);
                maxHealth = dummy.MaxHealth;
            }
            currentState.dummyMaxHealthSnapshots.Add(maxHealth);
        }
    }

    // Samples spreadSamples independent EventManagerPD.TryGetSpawnAreaPoint candidates and returns whichever
    // has the GREATEST MINIMUM distance to every dummy already placed this pass (_placedPositionsScratch) —
    // same "tournament bias" shape PlayableAreaPD.RandomPoint's own outwardBiasSamples used to have for
    // centroid avoidance, just biased toward spreading dummies apart from EACH OTHER instead. The very FIRST
    // dummy has nothing to compare against yet — trivially takes whatever the first successful draw is, same
    // as calling this with spreadSamples effectively 1.
    //
    // minSeparationDistance layers a HARD floor underneath that soft bias (2026-08-27, explicit design — "an
    // obvious space limitation we'll work around and probably never realistically hit," a floor plus a
    // preference on top, not a replacement for it): tracks TWO running bests across the sampled candidates —
    // the best candidate that clears the floor (the real answer, returned when one exists) and the best
    // candidate overall regardless of the floor (a graceful fallback for the pathological case where every
    // single sample this pass happens to land under the floor). Falls back to transform.position only if
    // EVERY sample this pass failed outright (e.g. no spawnAreas authored at all).
    private Vector3 PickSpreadPosition(EventManagerPD.SpawnAreaSelection selection)
    {
        float minSepSq = minSeparationDistance * minSeparationDistance;

        Vector3 bestOverall = default;
        bool haveOverall = false;
        float bestOverallMinDistSq = -1f;

        Vector3 bestAboveFloor = default;
        bool haveAboveFloor = false;
        float bestAboveFloorMinDistSq = -1f;

        int samples = _placedPositionsScratch.Count == 0 ? 1 : spreadSamples;
        for (int i = 0; i < samples; i++)
        {
            if (!EventManagerPD.instance.TryGetSpawnAreaPoint(selection, out Vector3 candidate, avoidPlayersOnPlacement, playerOffset, zoneRadius))
                continue;

            float candidateMinDistSq = _placedPositionsScratch.Count == 0 ? float.MaxValue : MinDistanceSqToAny(candidate, _placedPositionsScratch);

            if (!haveOverall || candidateMinDistSq > bestOverallMinDistSq)
            {
                bestOverall = candidate;
                bestOverallMinDistSq = candidateMinDistSq;
                haveOverall = true;
            }

            if (candidateMinDistSq >= minSepSq && (!haveAboveFloor || candidateMinDistSq > bestAboveFloorMinDistSq))
            {
                bestAboveFloor = candidate;
                bestAboveFloorMinDistSq = candidateMinDistSq;
                haveAboveFloor = true;
            }
        }

        if (haveAboveFloor) return bestAboveFloor;
        if (haveOverall) return bestOverall;
        return transform.position;
    }

    private static float MinDistanceSqToAny(Vector3 point, List<Vector3> others)
    {
        float minSq = float.MaxValue;
        for (int i = 0; i < others.Count; i++)
        {
            float distSq = (point - others[i]).sqrMagnitude;
            if (distSq < minSq) minSq = distSq;
        }
        return minSq;
    }

    protected override void Simulate(ref State state, float delta)
    {
        // Ticks elapsedTime (drives the description panel's own flash tell) — required, see EventBasePD.
        // Simulate's own doc comment.
        base.Simulate(ref state, delta);

        if (state.b.succeeded) return; // already resolving/resolved
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.dummyIds.Count == 0) return; // nothing spawned (e.g. dummyPrefab was null) — never succeeds

        // Every entry in dummyIds corresponds to an ACTUALLY-created dummy (only ever added on a successful
        // hierarchy.Create() above), so a null GameObject here can only mean "already died and got cleaned
        // up" — never "never existed." Bails the instant ANY dummy is still alive; only reaches success once
        // every single one is confirmed dead or gone.
        for (int i = 0; i < state.dummyIds.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.dummyIds[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD dummy) && !dummy._isDead) return;
        }

        state.b.succeeded = true;

        // Resolves itself immediately rather than waiting out EnemySpawnerPD's own flat eventDuration
        // timeout — safe to call from here, see CaptureThePointPD.Simulate()'s own identical precedent/doc
        // comment for why (ResolveEvent() only ever mutates predicted State, the actual destroy is deferred
        // to verified state via EventBasePD's own UpdateView).
        if (EventManagerPD.instance != null) EventManagerPD.instance.ResolveEvent();
    }

    // Wipes every dummy that's still alive if the event resolved WITHOUT all of them dying (timeout
    // failure) — see DPSCheckPD.Cleanup's own doc comment for the full SilentDespawn reasoning, identical
    // here just looped over every entry. Already internally guarded against an enemy that's already
    // dead/wiped, so calling it unconditionally on every entry is safe for both outcomes.
    public override void Cleanup()
    {
        base.Cleanup();

        for (int i = 0; i < currentState.dummyIds.Count; i++)
        {
            var go = hierarchy.GetGameObject(currentState.dummyIds[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD dummy))
                dummy.SilentDespawn();
        }
    }

    // "Destroy the Spires!\nProgress: X%", X to 1 decimal place — same format/precision as DPSCheckPD's own
    // GetEventDescription, just summed across every dummy instead of tracking one. totalMax is the SUM of
    // the snapshotted max-health values (stable for the whole event, immune to a dummy's own deletion
    // shrinking the denominator — see this class's own doc comment); totalCurrent only ever counts a STILL-
    // ALIVE dummy's own live CurrentHealth, treating a dead/deleted one as fully depleted (0) — so the
    // overall percentage can only ever climb, never jump backward as individual dummies get cleaned up.
    protected override string GetEventDescription(State viewState)
    {
        float totalCurrent = 0f;
        float totalMax = 0f;

        for (int i = 0; i < viewState.dummyIds.Count; i++)
        {
            totalMax += i < viewState.dummyMaxHealthSnapshots.Count ? viewState.dummyMaxHealthSnapshots[i] : 0f;

            var go = hierarchy.GetGameObject(viewState.dummyIds[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD dummy) && !dummy._isDead)
                totalCurrent += Mathf.Max(0f, dummy.CurrentHealth);
        }

        float percent = totalMax > 0f ? Mathf.Clamp01(1f - totalCurrent / totalMax) * 100f : 0f;
        return $"Destroy the Spires!\nProgress: {percent:F1}%";
    }
}
