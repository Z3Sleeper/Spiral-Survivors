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
    // Extra breathing room beyond zoneRadius when picking where to place the zone — purely aesthetic ("don't
    // want it ever hugging a wall"), separate from zoneRadius itself (which is the mandatory geometric
    // requirement — the zone's own circle must always be fully inside the playable area regardless of this).
    // Per-event, not a PlayableAreaPD-level setting, since different events may want different spacing.
    [SerializeField] private float wallOffset = 5f;
    // Minimum fraction (0-1) of the zone's own circle that must be clear of every PlayableAreaPD inner
    // obstruction for a candidate placement to be valid — see PlayableAreaPD.RandomPoint's own doc comment.
    // Default 0.5 (at least half clear) lets a big zone legitimately overlap a small obstacle instead of
    // being blocked outright by it, while still rejecting a spot that's mostly sitting inside one. Does NOT
    // apply to the outer boundary/walls — those stay a hard requirement regardless (see wallOffset above).
    [SerializeField] private float minUnobstructedFraction = 0.5f;
    // How many independent valid candidates PlayableAreaPD.RandomPoint samples before picking whichever one
    // ends up farthest from the playable area's own center — see that method's own doc comment for the full
    // "tournament bias" reasoning. 1 = no bias (whatever's found first); higher pulls the zone further toward
    // the edges on average, with diminishing returns as it climbs.
    [SerializeField] private int outwardBiasSamples = 5;
    // Radial indicator (a cylinder, same IndicatorMath.ApplyRadialTransform convention every other radius
    // telegraph in this codebase uses) — purely visual, sized once at OnEventStart since zoneRadius never
    // changes mid-event.
    [SerializeField] private Transform zoneVisual;
    // Optional progress readout — reuses WindupFillPD exactly as-is (SetProgress expects 0..1, same as every
    // other live-progress fill in this codebase).
    [SerializeField] private WindupFillPD chargeFill;

    [Header("Charge")]
    [SerializeField] private float baseChargeSeconds = 30f;

    // Resolved via GetComponent, not a serialized drag-in field — same convention BWBlinkAbilityPD's own
    // _predictedTransform uses for the identical reason (see OnEventStart's own doc comment).
    private PredictedTransform _predictedTransform;

    private void Awake() => _predictedTransform = GetComponent<PredictedTransform>();

    // Places the zone via PlayableAreaPD.RandomPoint (null-safe — see IEventPD.OnEventStart's own doc
    // comment; if no PlayableAreaPD exists yet, the zone just stays wherever EventManagerPD spawned it) —
    // wallOffset keeps the zone's own circle a hard distance from any wall, minUnobstructedFraction lets it
    // softly overlap a small inner obstruction (see PlayableAreaPD.RandomPoint's own doc comment for the
    // full outer-vs-inner distinction). Resets all per-life State explicitly (defensive — matches this
    // codebase's "reset on spawn regardless of whether pooling is actually involved" convention) rather than
    // trusting GetInitialState()'s own defaults alone.
    public override void OnEventStart(PlayableAreaPD playableArea, int currentTier)
    {
        // Shows the shared event description panel — see EventBasePD.OnEventStart's own doc comment.
        base.OnEventStart(playableArea, currentTier);

        if (playableArea != null)
        {
            transform.position = playableArea.RandomPoint(wallOffset, zoneRadius, minUnobstructedFraction, outwardBiasSamples: outwardBiasSamples);
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

        if (zoneVisual != null) IndicatorMath.ApplyRadialTransform(zoneVisual, zoneRadius);
        if (chargeFill != null) chargeFill.SetProgress(0f);
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
            chargeFill.SetProgress(viewState.requiredCharge > 0f ? Mathf.Clamp01(viewState.chargeProgress / viewState.requiredCharge) : 0f);
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
