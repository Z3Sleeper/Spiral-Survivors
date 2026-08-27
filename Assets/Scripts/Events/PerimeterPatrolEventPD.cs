using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Spawns enemyCount PerimeterPatrolGloomPD instances (1 or many — the field works unmodified for any
// count, no code branch needed for the distinction) and succeeds once every one of them has died. Modeled
// directly on DestroyTheTargetsPD (spawn N, track via a DisposableList<PredictedObjectID>, succeed once
// none are alive, self-resolve, SilentDespawn survivors on timeout) — including its progress-percentage
// snapshot mechanism (see maxHealthSnapshots' own doc comment), just without its spread-SAMPLING-tournament
// placement (DestroyTheTargetsPD picks from several random candidates and keeps whichever is farthest from
// the others already placed — overkill here, since every spawn already lies on the same 1D loop). Instead,
// evenly divides the perimeter's own TRUE ARC LENGTH among enemyCount > 1 spawns, offset from one random
// starting distance — see PlayableAreaPD.GetPointAtArcLength (general-purpose, not specific to this event)
// and OnEventStart's own comment for the exact formula.
//
// The patrol loop itself is EventManagerPD's own hand-authored patrolGraph (2026-08-27 — was
// PlayableAreaPD.GetInsetPerimeter(wallOffset); see PlayableAreaPD.GetInsetPerimeter's own doc comment for
// why that whole procedural-placement approach was abandoned project-wide). PlayableAreaPD.GetLoopLength/
// GetPointAtArcLength are unaffected by this — they were always generic against any Vector3[] loop, so they
// just get fed a different array now.
public class PerimeterPatrolEventPD : EventBasePD<PerimeterPatrolEventPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EventBaseState b;
        public DisposableList<PredictedObjectID> enemyIds;
        // Snapshotted once per enemy (same index as enemyIds), right after ConfigureSpawn/tier-scaling —
        // see this class's own doc comment for why a live MaxHealth re-read per enemy would actually be
        // WRONG here: an enemy that's already died and been deleted would silently drop out of the total
        // denominator, making the overall percentage jump BACKWARD the instant its corpse gets cleaned up.
        // Same mechanism DestroyTheTargetsPD.dummyMaxHealthSnapshots already uses.
        public DisposableList<float> maxHealthSnapshots;
        public void Dispose()
        {
            enemyIds.Dispose();
            maxHealthSnapshots.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.enemyIds = enemyIds.Duplicate();
            copy.maxHealthSnapshots = maxHealthSnapshots.Duplicate();
            return copy;
        }
    }

    protected override ref EventBaseState GetBase(ref State state) => ref state.b;

    protected override State GetInitialState() => new()
    {
        enemyIds = DisposableList<PredictedObjectID>.Create(enemyCount),
        maxHealthSnapshots = DisposableList<float>.Create(enemyCount),
    };

    [Header("Patrol Enemy")]
    [SerializeField] private PerimeterPatrolGloomPD enemyPrefab;
    [SerializeField] private int enemyCount = 1;

    public override void OnEventStart(PlayableAreaPD playableArea, int currentTier)
    {
        // Shows the shared event description panel — see EventBasePD.OnEventStart's own doc comment.
        base.OnEventStart(playableArea, currentTier);

        currentState.enemyIds.Clear();
        currentState.maxHealthSnapshots.Clear();
        currentState.b.succeeded = false;

        if (enemyPrefab == null || EventManagerPD.instance == null) return;

        Vector3[] perimeter = EventManagerPD.instance.GetPatrolGraph();
        if (perimeter.Length == 0) return;

        // One random starting distance along the loop for the first enemy; every other enemy's distance is
        // that same base, offset by an even fraction of the loop's own TOTAL ARC LENGTH (i * totalLength /
        // enemyCount) — so enemyCount enemies land EXACTLY evenly spaced by true walking distance around
        // the whole loop, not just by array-index count (which would drift uneven on a non-square/non-
        // uniformly-vertexed level). GetPointAtArcLength hands back both the interpolated spawn position
        // (generally between two array vertices, not ON one) and the correct next-waypoint index for
        // PerimeterPatrolGloomPD.Launch to walk toward from there — see that method's own doc comment for
        // why spawning exactly on a vertex was never actually required. Reduces to the old single-random-
        // point behavior at enemyCount == 1 (i=0 always offsets by exactly 0 arc length).
        float totalLength = PlayableAreaPD.GetLoopLength(perimeter);
        float startArc = totalLength > 0f ? GameRandom.instance.NextFloat(0f, totalLength) : 0f;
        for (int i = 0; i < enemyCount; i++)
        {
            float arc = startArc + i * totalLength / enemyCount;
            Vector3 spawnPos = PlayableAreaPD.GetPointAtArcLength(perimeter, arc, out int startIndex);

            var id = hierarchy.Create(enemyPrefab.gameObject, spawnPos, Quaternion.identity);
            if (!id.HasValue) continue;

            currentState.enemyIds.Add(id.Value);

            var go = hierarchy.GetGameObject(id.Value);
            if (go == null) { currentState.maxHealthSnapshots.Add(0f); continue; }

            float maxHealth = 0f;
            if (go.TryGetComponent(out IEnemyPD enemy))
            {
                // All four stats scaled — default (opt-out) args, same as DPSCheckPD/DestroyTheTargetsPD.
                EventTargetScalingPD.ApplyTierScaling(enemy, currentTier);
                maxHealth = enemy.MaxHealth;
            }
            currentState.maxHealthSnapshots.Add(maxHealth);

            // startIndex is the waypoint just AHEAD of spawnPos (see GetPointAtArcLength's own doc comment)
            // — the patrol AI walks toward it from wherever it actually spawned, same as it would from any
            // other point mid-edge.
            if (go.TryGetComponent(out PerimeterPatrolGloomPD patrol))
                patrol.Launch(startIndex);
        }
    }

    protected override void Simulate(ref State state, float delta)
    {
        // Ticks elapsedTime (drives the description panel's own flash tell) — required, see EventBasePD.
        // Simulate's own doc comment.
        base.Simulate(ref state, delta);

        if (state.b.succeeded) return; // already resolving/resolved
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.enemyIds.Count == 0) return; // nothing spawned (e.g. enemyPrefab was null) — never succeeds

        // Every entry corresponds to an ACTUALLY-created enemy (only ever added on a successful
        // hierarchy.Create() above), so a null GameObject here can only mean "already died and got cleaned
        // up" — never "never existed." Bails the instant ANY enemy is still alive; only reaches success
        // once every single one is confirmed dead or gone.
        for (int i = 0; i < state.enemyIds.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.enemyIds[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy) && !enemy._isDead) return;
        }

        state.b.succeeded = true;

        // Resolves itself immediately rather than waiting out EnemySpawnerPD's own flat eventDuration
        // timeout — safe to call from here, see CaptureThePointPD.Simulate()'s own identical precedent/doc
        // comment for why (ResolveEvent() only ever mutates predicted State, the actual destroy is deferred
        // to verified state via EventBasePD's own UpdateView).
        if (EventManagerPD.instance != null) EventManagerPD.instance.ResolveEvent();
    }

    // Wipes every enemy that's still alive if the event resolved WITHOUT all of them dying (timeout
    // failure) — see DestroyTheTargetsPD.Cleanup's own identical reasoning. Already internally guarded
    // against an enemy that's already dead/wiped, so calling it unconditionally on every entry is safe for
    // both outcomes.
    public override void Cleanup()
    {
        base.Cleanup();

        for (int i = 0; i < currentState.enemyIds.Count; i++)
        {
            var go = hierarchy.GetGameObject(currentState.enemyIds[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy))
                enemy.SilentDespawn();
        }
    }

    // "Defeat the Patrollers!\nProgress: X%", X to 1 decimal place — same format/precision as
    // DestroyTheTargetsPD's own GetEventDescription. totalMax is the SUM of the snapshotted max-health
    // values (stable for the whole event, immune to an enemy's own deletion shrinking the denominator —
    // see maxHealthSnapshots' own doc comment); totalCurrent only ever counts a STILL-ALIVE enemy's own
    // live CurrentHealth, treating a dead/deleted one as fully depleted (0) — so the overall percentage can
    // only ever climb, never jump backward as individual enemies get cleaned up.
    protected override string GetEventDescription(State viewState)
    {
        float totalCurrent = 0f;
        float totalMax = 0f;

        for (int i = 0; i < viewState.enemyIds.Count; i++)
        {
            totalMax += i < viewState.maxHealthSnapshots.Count ? viewState.maxHealthSnapshots[i] : 0f;

            var go = hierarchy.GetGameObject(viewState.enemyIds[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy) && !enemy._isDead)
                totalCurrent += Mathf.Max(0f, enemy.CurrentHealth);
        }

        float percent = totalMax > 0f ? Mathf.Clamp01(1f - totalCurrent / totalMax) * 100f : 0f;
        return $"Defeat the Patrollers!\nProgress: {percent:F1}%";
    }
}
