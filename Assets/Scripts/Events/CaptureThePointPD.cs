using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// First real mid-cycle event (2026-08-25) — see EventBasePD's own doc comment for the shared IEventPD
// contract this implements. A circular zone appears somewhere in the playable area; players standing inside
// it charge it up; fully charging it before EnemySpawnerPD's own flat eventDuration timeout succeeds — this
// resolves ITSELF the instant that happens (see Simulate()'s own ResolveEvent() call), rather than sitting
// fully-charged until the timeout eventually catches up. Otherwise the timeout resolves it as a failure with
// B.succeeded still at its default false.
//
// Charge math, explicit design: requiredCharge = baseChargeSeconds * FixedPlayerCount (30s solo, +30s per
// player in the match), charge accumulates at 1 unit/sec PER PLAYER CURRENTLY STANDING IN THE ZONE. This is
// what makes "multiple players in the zone compounds their charge speeds" genuinely COUNTERBALANCE the
// per-player requirement increase, not just soften it: a full team standing together always finishes in
// exactly baseChargeSeconds (30s) regardless of player count (N players * baseChargeSeconds required, N
// players charging at once = N units/sec, cancels out) — only PARTIAL participation (fewer players in the
// zone than are in the match) takes proportionally longer.
public class CaptureThePointPD : EventBasePD<CaptureThePointPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EventBaseState b;
        public float chargeProgress;
        // Set once in OnEventStart (FixedPlayerCount is stable for the whole match) — not recomputed every
        // tick, same "resolve once at the moment that matters" shape as everything else event-specific here.
        public float requiredCharge;
        public void Dispose() { }
        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EventBaseState GetBase(ref State state) => ref state.b;

    [Header("Zone")]
    [SerializeField] private float zoneRadius = 5f;
    // Both the zone's boundary outline AND its fill-progress readout (2026-09-14, replaces a separate
    // zoneVisual cylinder transform scaled via IndicatorMath.ApplyRadialTransform) — WindupFillPD's own
    // Radial outline (LineRenderer) now draws the boundary itself, same as every other circular AoE in this
    // codebase (RemoteSpikeBurstPD/KnockbackDummyTargetPD), so a separate zoneVisual is redundant. Stationary
    // for this whole event's life (zoneRadius never changes mid-event, and this object's own position is
    // fixed the instant OnEventStart places it) — centerForwardOffset stays at its 0 default everywhere this
    // is called, since chargeFill's own current position already IS the zone's center.
    [SerializeField] private WindupFillPD chargeFill;

    [Header("Charge")]
    [SerializeField] private float baseChargeSeconds = 30f;

    // Resolved via GetComponent, not a serialized drag-in field — same convention BWBlinkAbilityPD's own
    // _predictedTransform uses for the identical reason (see OnEventStart's own doc comment).
    private PredictedTransform _predictedTransform;

    private void Awake() => _predictedTransform = GetComponent<PredictedTransform>();

    // Places the zone via a verbatim pick from EventManagerPD's own hand-authored eventSpawnPoints
    // (2026-08-27 — was PlayableAreaPD.RandomPoint; see PlayableAreaPD.GetInsetPerimeter's own doc comment
    // for why that whole procedural-placement approach was abandoned project-wide). Deliberately does NOT
    // pass avoidPlayers — confirmed intentional, not the pre-existing RandomPoint call's omission carried
    // forward by accident: this zone has no physical body/collider, so spawning on top of a player is
    // harmless. Resets all per-life State explicitly (defensive — matches this codebase's "reset on spawn
    // regardless of whether pooling is actually involved" convention) rather than trusting GetInitialState()'s
    // own defaults alone.
    public override void OnEventStart(PlayableAreaPD playableArea, int currentTier)
    {
        // Shows the shared event description panel — see EventBasePD.OnEventStart's own doc comment.
        base.OnEventStart(playableArea, currentTier);

        if (EventManagerPD.instance != null && EventManagerPD.instance.TryGetEventSpawnPoint(out var p))
        {
            transform.position = p;
            // Required after any direct position assignment on an object with PredictedTransform — per
            // PurrNet's own guidance (https://purrnet.dev/docs/client-side-prediction/built-in-components/
            // predicted-transform), same fix BWBlinkAbilityPD.Activate() already applies for its own instant
            // teleport. Without this, the smoothed Visuals child would visibly SLIDE from wherever this event
            // was originally created to its actual zone position over the next several frames, instead of
            // appearing there instantly — PredictedTransform's own correction-smoothing has no way to tell
            // "legitimate one-time placement" apart from "normal misprediction correction" otherwise.
            _predictedTransform?.ResetInterpolation();
        }

        int playerCount = GameManagerPD.instance != null ? GameManagerPD.instance.FixedPlayerCount : 1;
        if (playerCount <= 0) playerCount = 1;

        currentState.chargeProgress = 0f;
        currentState.requiredCharge = baseChargeSeconds * playerCount;
        currentState.b.succeeded = false;

        if (chargeFill != null)
        {
            // ShowCircle BEFORE this first SetProgress call, not after (2026-09-14 fix) — WindupFillPD's
            // circle outline points are computed ONCE and cached the first time RecomputeOutline() runs
            // (see its own doc comment), using whatever _radius was set at that moment. SetProgress() itself
            // triggers that first RecomputeOutline() call, so calling it before ShowCircle() had ever set a
            // real radius permanently poisoned the outline cache with an all-zero circle for this object's
            // entire life (confirmed live — the exact "outline never appears, all positions read 0,0,0" bug
            // this fixes). ShowCircle() itself is cheap/side-effect-free (just stores _radius), so calling it
            // here is safe even though UpdateView's own per-frame call (see its own doc comment) re-asserts
            // the same radius every frame afterward regardless.
            chargeFill.ShowCircle(zoneRadius);
            chargeFill.SetProgress(0f);
        }
    }

    protected override void Simulate(ref State state, float delta)
    {
        // Ticks elapsedTime (drives the description panel's own flash tell) — required, see EventBasePD.
        // Simulate's own doc comment. Called unconditionally, before this method's own early-returns below,
        // so elapsedTime keeps advancing even once succeeded (it's "time since start," not tied to charge
        // progress) — base.Simulate() has its own independent PausePD guard either way.
        base.Simulate(ref state, delta);

        if (state.b.succeeded) return; // already resolving/resolved — nothing left to accumulate
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        int playersInZone = 0;
        float radiusSq = zoneRadius * zoneRadius;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead) continue;

            // No invisible/taunting check, deliberately — capturing a point is about physical presence in a
            // space, not enemy targeting eligibility, so a vanished Rogue standing in the zone still counts.
            Vector3 toPlayer = stats.transform.position - transform.position;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude <= radiusSq) playersInZone++;
        }

        if (playersInZone <= 0) return;

        state.chargeProgress += playersInZone * delta;
        if (state.chargeProgress >= state.requiredCharge)
        {
            state.chargeProgress = state.requiredCharge;
            state.b.succeeded = true;

            // Resolves itself immediately rather than waiting out EnemySpawnerPD's own flat eventDuration
            // timeout — safe to call from here (unlike calling hierarchy.Delete on itself would be), since
            // ResolveEvent() only ever mutates predicted State (Cleanup()+MarkResolved()); the actual destroy
            // is deferred to verified state via EventBasePD's own UpdateView. EnemySpawnerPD's own later
            // timeout call still fires too — see EventManagerPD.ResolveEvent()'s own idempotency doc comment
            // for why that's a safe no-op (activeEventId is already cleared by then).
            if (EventManagerPD.instance != null) EventManagerPD.instance.ResolveEvent();
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Required — this is what actually runs EventBasePD's own deferred, verified-gated self-destruction
        // once MarkResolved() has propagated. See EventBasePD.UpdateView's own doc comment.
        base.UpdateView(viewState, verified);

        if (chargeFill != null)
        {
            // ShowCircle every frame, not just once at OnEventStart (2026-09-14 fix — matches
            // RemoteSpikeBurstPD/KnockbackDummyTargetPD's own identical convention for a stationary circular
            // AoE) — WindupFillPD's post-refactor architecture (see its own class doc comment) now owns its
            // shape internally via _radius, only ever set by ShowCircle, and actively computes fillMesh's own
            // localScale FROM that each call. This call used to be missing entirely here, so _radius sat at
            // its unset default of 0 forever — every ApplyFill computed diameter = 0 * 2 * t = 0, collapsing
            // the whole zone fill to nothing regardless of progress. centerForwardOffset stays at its 0
            // default — chargeFill's own current position (set once via this root's own transform placement
            // in OnEventStart) already IS the center, this zone never tracks an independent target the way
            // Leaper's own live-tracked circle does.
            chargeFill.ShowCircle(zoneRadius);
            chargeFill.SetProgress(viewState.requiredCharge > 0f ? Mathf.Clamp01(viewState.chargeProgress / viewState.requiredCharge) : 0f);
        }
    }

    // "Capture the Point!\nProgress X%", X to 1 decimal place — per explicit design.
    protected override string GetEventDescription(State viewState)
    {
        float percent = viewState.requiredCharge > 0f
            ? Mathf.Clamp01(viewState.chargeProgress / viewState.requiredCharge) * 100f
            : 0f;
        return $"Capture the Point!\nProgress {percent:F1}%";
    }
}
