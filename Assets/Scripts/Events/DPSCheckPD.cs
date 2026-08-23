using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// "Destroy the Spire" — spawns a single DummyTargetPD somewhere in the playable area and succeeds the
// instant it dies. See EventBasePD's own class-level doc comment for the shared IEventPD contract this
// implements, and DummyTargetPD's own doc comment for why a dummy is a genuine IEnemyPD under the hood
// (reuses its entire generic damage/health/death pipeline) rather than a separate parallel system.
//
// Placement: wallOffset/zoneRadius/avoidPlayersOnPlacement/playerOffset all feed straight into
// PlayableAreaPD.RandomPoint in one call — "stay clear of walls" and "don't spawn on top of a player" are
// both just zoneRadius-based overlap checks in that SAME rejection-sampling pass now (see RandomPoint's own
// doc comment for the exact shape). avoidPlayersOnPlacement/playerOffset are EventBasePD's own shared,
// opt-in fields (2026-08-21) — not re-declared here.
//
// Tier/Threat-Level/coop scaling: delegated entirely to DummyTargetScalingPD.ApplyTierScaling (2026-08-21,
// pulled out to a shared static utility once DestroyTheTargetsPD needed the identical treatment — see that
// class's own doc comment for the full formula).
//
// Progress: tracks the spawned dummy's own live CurrentHealth/MaxHealth every render frame (GetEventDescription
// takes viewState — see that method's own base doc comment for why verified/live reads are the right call
// here) rather than mirroring health into this event's own predicted State — the dummy already IS the single
// source of truth for its own health, duplicating it here would just be a second value that could drift.
public class DPSCheckPD : EventBasePD<DPSCheckPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EventBaseState b;
        // Default(PredictedObjectID) is the "no dummy spawned yet" sentinel — same unguarded default-sentinel
        // convention every other PredictedObjectID field in this codebase already uses (e.g.
        // EventManagerPD.State.activeEventId).
        public PredictedObjectID dummyId;
        public void Dispose() { }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EventBaseState GetBase(ref State state) => ref state.b;

    [Header("Target")]
    // Typed as DummyTargetPD directly, not a plain GameObject — this event specifically knows it's spawning
    // a dummy target (not an arbitrary IEnemyPD), same "small, event-specific, no need to touch shared
    // generic systems" reasoning CaptureThePointPD's own zoneVisual/chargeFill fields already follow.
    [SerializeField] private DummyTargetPD dummyPrefab;

    [Header("Placement")]
    [SerializeField] private float wallOffset = 10f;
    // The target's own footprint for BOTH of RandomPoint's overlap checks (wall clearance AND, when
    // avoidPlayersOnPlacement is enabled on EventBasePD, player clearance) — per explicit design, 1f.
    [SerializeField] private float zoneRadius = 1f;

    public override void OnEventStart(PlayableAreaPD playableArea, int currentTier)
    {
        // Shows the shared event description panel — see EventBasePD.OnEventStart's own doc comment.
        base.OnEventStart(playableArea, currentTier);

        currentState.dummyId = default;
        currentState.b.succeeded = false;

        if (dummyPrefab == null) return;

        Vector3 spawnPos = transform.position;
        if (playableArea != null)
            spawnPos = playableArea.RandomPoint(wallOffset, zoneRadius, avoidPlayers: avoidPlayersOnPlacement, playerOffset: playerOffset);

        var id = hierarchy.Create(dummyPrefab.gameObject, spawnPos, Quaternion.identity);
        if (!id.HasValue) return;

        currentState.dummyId = id.Value;
        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out IEnemyPD dummy))
            DummyTargetScalingPD.ApplyTierScaling(dummy, currentTier);
    }

    protected override void Simulate(ref State state, float delta)
    {
        // Ticks elapsedTime (drives the description panel's own flash tell) — required, see EventBasePD.
        // Simulate's own doc comment.
        base.Simulate(ref state, delta);

        if (state.b.succeeded) return; // already resolving/resolved
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        var go = hierarchy.GetGameObject(state.dummyId);
        if (go == null || !go.TryGetComponent(out IEnemyPD dummy)) return;
        if (!dummy._isDead) return;

        state.b.succeeded = true;

        // Resolves itself immediately rather than waiting out EnemySpawnerPD's own flat eventDuration
        // timeout — safe to call from here, see CaptureThePointPD.Simulate()'s own identical precedent/doc
        // comment for why (ResolveEvent() only ever mutates predicted State, the actual destroy is deferred
        // to verified state via EventBasePD's own UpdateView).
        if (EventManagerPD.instance != null) EventManagerPD.instance.ResolveEvent();
    }

    // Wipes the dummy cleanly if the event resolved WITHOUT the dummy actually dying (timeout failure) —
    // SilentDespawn is the same difficulty-pacing wipe EnemySpawnerPD uses right before a boss spawns: no XP
    // orbs, no RaiseOnEnemyDeath, just a predicted-instant hide + verified-gated hierarchy.Delete(). Already
    // internally guarded against an enemy that's already dead/wiped (IEnemyPD.SilentDespawn's own doc
    // comment), so calling it unconditionally here is safe and correct for BOTH outcomes — a no-op on the
    // success path (the dummy is already dead by the time Cleanup() runs), the real wipe on the failure path.
    public override void Cleanup()
    {
        base.Cleanup();

        var go = hierarchy.GetGameObject(currentState.dummyId);
        if (go != null && go.TryGetComponent(out IEnemyPD dummy))
            dummy.SilentDespawn();
    }

    // "Destroy the Spire!\nProgress: X%", X to 1 decimal place — same format/precision as CaptureThePointPD's
    // own GetEventDescription. Reads the dummy's own live CurrentHealth/MaxHealth directly (see this class's
    // own doc comment for why nothing is mirrored into predicted State for this) — 0% if the dummy hasn't
    // spawned yet (or was already deleted without succeeding, which shouldn't normally happen), 100% once its
    // health verifiably reaches 0 (TakeDamage already floors CurrentHealth before Die() ever runs, so this
    // naturally reads 100% the same tick _isDead flips true in Simulate() above — no special-cased success
    // branch needed).
    protected override string GetEventDescription(State viewState)
    {
        float percent = 0f;
        var go = hierarchy.GetGameObject(viewState.dummyId);
        if (go != null && go.TryGetComponent(out IEnemyPD dummy) && dummy.MaxHealth > 0f)
            percent = Mathf.Clamp01(1f - dummy.CurrentHealth / dummy.MaxHealth) * 100f;
        else if (viewState.b.succeeded)
            percent = 100f;

        return $"Destroy the Spire!\nProgress: {percent:F1}%";
    }
}
