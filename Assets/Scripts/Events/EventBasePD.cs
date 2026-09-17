using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Shared contract every concrete event type implements (2026-08-24) — mirrors EnemyBasePD<TState>/IEnemyPD's
// own "generic interface + shared base-state struct" shape exactly, so EventManagerPD can spawn/track/resolve
// ANY event type generically without knowing its concrete type, same as EnemySpawnerPD does for enemies via
// IEnemyPD. The ONLY guaranteed contract across every event (per explicit design — events themselves will be
// "completely unrelated," doing arbitrary things, possibly spawning their own enemies/hazards): it starts the
// moment EventManagerPD.StartEvent() spawns it (OnEventStart()), cleans itself up when EventManagerPD.
// ResolveEvent() ends it (Cleanup()), and carries a `succeeded` bool defaulting to false — so an event
// resolved via EnemySpawnerPD's own flat eventDuration timeout (before the event's own logic ever set
// succeeded = true) is already correctly "failed" with no extra handling needed anywhere.
//
// Resolve vs. destroy (2026-08-25, safety fix): ResolveEvent() NEVER destroys the event synchronously — see
// EventBaseState.resolved's own doc comment for why. It only calls Cleanup() then MarkResolved(), both pure
// predicted-State mutations; the actual hierarchy.Delete() is deferred to VERIFIED state, in this base
// class's own UpdateView. This is also what makes it safe for a concrete event to call
// EventManagerPD.instance.ResolveEvent() on ITSELF, directly, the instant it succeeds (see
// CaptureThePointPD.Simulate() for the precedent) — ResolveEvent() doesn't destroy anything synchronously
// anymore, so there's no same-tick self-deletion hazard in doing that. EnemySpawnerPD's own later timeout
// call still fires too (it doesn't know the event already resolved itself) — see ResolveEvent()'s own
// idempotency guard for why that's a safe no-op.
public interface IEventPD
{
    bool Succeeded { get; }
    PredictedObjectID ObjectId { get; }
    // Called once by EventManagerPD.StartEvent() immediately after spawning — the concrete event's own
    // "begin doing my actual event-specific thing" hook (spawn hazards/enemies, arm a timer, whatever this
    // particular event needs). playableArea is passed in explicitly (see PlayableAreaPD's own doc comment),
    // and stays on this signature even though every concrete event's own PLACEMENT now goes through
    // EventManagerPD.instance's own hand-authored eventSpawnPoints/spawnAreas/patrolGraph instead
    // (2026-08-27 — see PlayableAreaPD.GetInsetPerimeter's own doc comment for why the old
    // PlayableAreaPD.RandomPoint-based approach was abandoned project-wide) — a future event doing something
    // ELSE with the raw playable-area geometry (not placement) can still use this parameter. Can be null if
    // no PlayableAreaPD exists in the scene yet — same "missing optional cross-scene singleton is a harmless
    // no-op" convention as everywhere else in this codebase.
    // currentTier (2026-08-20) — EnemySpawnerPD's own _currentTierNumber at the moment this event started,
    // same raw tier-number convention as EnemyBasePD.ConfigureSpawn's own tier parameter (1-based). Handed
    // to EVERY event unconditionally, per explicit design — whether a given event does anything with it is
    // entirely up to that event (e.g. DestroyTheTargetPD scaling its target's health off it; a purely
    // cosmetic event ignoring it completely is equally valid). Not itself State-backed — same "read once at
    // OnEventStart time, never needs to be resimulation-safe beyond that single read" treatment
    // CaptureThePointPD's own FixedPlayerCount snapshot already gets.
    void OnEventStart(PlayableAreaPD playableArea, int currentTier);
    // Called once by EventManagerPD.ResolveEvent() — the concrete event's own chance to tear down anything
    // IT spawned (its own hazards/enemies/etc.), independent of whether it succeeded or not. Reading
    // Succeeded is still valid here if a concrete event needs to branch its own cleanup on the outcome. Does
    // NOT destroy the event itself — see this file's own class-level doc comment.
    void Cleanup();
    // Called once by EventManagerPD.ResolveEvent(), right after Cleanup() — marks this event's own predicted
    // State as resolved (EventBaseState.resolved). NOT meant to be overridden per-event (EventBasePD's own
    // implementation is the only one that should ever exist) — exposed on the interface purely because
    // EventManagerPD only holds an IEventPD reference, not the concrete type.
    void MarkResolved();
}

public struct EventBaseState : IPredictedData<EventBaseState>, IDuplicate<EventBaseState>
{
    // Defaults to false — see this file's own class-level doc comment for why that's exactly the correct
    // "not yet succeeded" starting point, including for an event resolved via timeout.
    public bool succeeded;
    // Set once by MarkResolved() (called from EventManagerPD.ResolveEvent()) — the one-way signal that this
    // event's own outcome has been fully processed (Cleanup() already ran, any bonus-round grant already
    // happened) and it's now safe to actually destroy it. Deliberately NEVER acted on off the predicted tick
    // that sets it — EventBasePD's own UpdateView only destroys the event once this reads true on VERIFIED
    // state, same "state-critical, only despawn once server-confirmed" convention every other one-shot
    // spawned object in this codebase already uses (RemoteSpikeBurstPD/MortarHazardPD/EnemyProjectilePD),
    // so a mispredicted early "this succeeded" guess can still be safely rolled back before anything is
    // irreversibly destroyed. Separate from `succeeded` (which means "did the event's own win condition
    // trigger") — resolved means "EventManagerPD is done with this event, tear it down," which happens for
    // EITHER outcome (success or the flat-timeout failure case).
    public bool resolved;
    // Counts up from 0 the instant this event starts, paused whenever PausePD.IsPaused — see EventBasePD's
    // own base Simulate() override (a concrete event's own Simulate() MUST call base.Simulate(ref state,
    // delta), same override+base convention as OnEventStart/Cleanup/UpdateView, or this never advances).
    // Currently only drives the event description panel's own "flash for the first few seconds" tell (see
    // EventBasePD.GetDescriptionColor), but generically useful for any future event that wants "time since I
    // started."
    public float elapsedTime;
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

    // Shared, opt-in placement constraint (2026-08-21) — off by default, so an event that doesn't place
    // anything player-sensitive is unaffected. A concrete event's own OnEventStart passes these straight
    // through to EventManagerPD.TryGetEventSpawnPoint/TryGetSpawnAreaPoint's own avoidPlayers/playerOffset
    // params (see those methods' own doc comments for the exact overlap-check shape this drives) alongside
    // whatever zoneRadius/footprint field that event already declares locally — that one stays per-event
    // since its meaning varies (e.g. CaptureThePointPD's own zoneRadius also sizes its visual circle), but
    // "should placement avoid players, and by how much" is the same question for every event, so it lives
    // here once instead of being re-declared per event. wallOffset (the old "how far this event's center
    // should clear the wall" knob) was removed from here 2026-08-27 — hand-authored spawn points/areas
    // already account for wall clearance themselves, there's no margin math left for any event to feed.
    [Header("Placement")]
    [Tooltip("If enabled, a concrete event's own placement (via EventManagerPD's hand-authored spawn points/areas) also avoids spawning within playerOffset of any alive player's own footprint.")]
    [SerializeField] protected bool avoidPlayersOnPlacement = false;
    [Tooltip("Only used when Avoid Players On Placement is enabled — see its own tooltip.")]
    [SerializeField] protected float playerOffset = 0f;

    // Virtual, not abstract (2026-08-25) — shows the shared event description panel (LocalUIManagerPD.
    // ShowEventPanel) generically for every event, so no concrete event needs to know that panel exists at
    // all. A concrete event's own override MUST call base.OnEventStart(playableArea) — same "PD lifecycle
    // methods need override+base" convention UpdateView below already follows — before/after doing its own
    // event-specific setup.
    //
    // Used to also fire a floating Custom status popup ("Stunned!"/"Dodged!"-style callout above every
    // player's own head) reading a per-event startAnnouncement string the instant the event began — removed
    // entirely 2026-09-02, per explicit design, along with the startAnnouncement field itself and
    // EventManagerPD.AnnouncementDurationMultiplier (which had no other consumer). The persistent event
    // description panel (ShowEventPanel, still below) is the only start-of-event feedback now.
    public virtual void OnEventStart(PlayableAreaPD playableArea, int currentTier)
    {
        LocalUIManagerPD.instance?.ShowEventPanel();
    }

    // Event-specific sweep-up (tear down anything THIS event spawned) — called once by
    // EventManagerPD.ResolveEvent(), for EITHER outcome. Virtual, not abstract — the base implementation
    // hides the shared event description panel (LocalUIManagerPD.HideEventPanel) generically; a concrete
    // event that overrides this for its own sub-object teardown MUST call base.Cleanup() too, same
    // override+base convention as OnEventStart/UpdateView. Does NOT destroy this event itself — see
    // EventBaseState.resolved's own doc comment.
    public virtual void Cleanup() => LocalUIManagerPD.instance?.HideEventPanel();

    // Called every UpdateView (render-frame) while this event is active — returns whatever EVENT-SPECIFIC
    // text the shared description panel should show right now (see LocalUIManagerPD.SetEventDescription's
    // own doc comment). Mandatory, not optional — every event is expected to explain itself to the player
    // somehow. Takes the live viewState directly (not currentState/verified) since this drives a purely
    // cosmetic, continuously-updating display (e.g. a live charge percentage) — same "smoothly-changing
    // value, safe under viewState" reasoning RemoteSpikeBurstPD's own progress fill already uses. Does NOT
    // need to include a time-remaining line — BuildFullDescription (below) appends a generic "Time
    // Remaining: X seconds" line to whatever this returns, shared across every event.
    protected abstract string GetEventDescription(TState viewState);

    // Ticks elapsedTime — see EventBaseState.elapsedTime's own doc comment. A concrete event's own
    // Simulate() override MUST call base.Simulate(ref state, delta) or elapsedTime (and therefore the
    // description panel's own flash tell) never advances. Own PausePD guard so elapsedTime genuinely halts
    // during a pause regardless of whatever order a concrete event's own guards run in.
    protected override void Simulate(ref TState state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        GetBase(ref state).elapsedTime += delta;
    }

    // Threshold below which the flash's own target color switches from yellow to red (2026-09-02) — see
    // GetDescriptionColor's own doc comment for the full flash shape this feeds into.
    [SerializeField] private float lowTimeWarningThreshold = 30f;

    // Single source of truth for "how much time does this event have left" (2026-09-02, replaces the old
    // DisplayedTimeoutSeconds hardcoded constant) — reads EnemySpawnerPD.EventDuration LIVE instead of
    // carrying a second, separately-authored copy of the same number. EnemySpawnerPD.eventDuration is the
    // ONE real value that actually decides when EnemySpawnerPD calls ResolveEvent() on a still-running event
    // (see that field's own doc comment); this now reads that exact same field, so the display (both the
    // "Time Remaining" text below and GetDescriptionColor's own red-flash threshold) can never drift out of
    // sync with the real timeout the way the old hardcoded copy could. Falls back to 60f only if
    // EnemySpawnerPD.instance is somehow missing (shouldn't normally happen — an event can't be running
    // without the spawner that started it).
    private float GetTimeRemaining(TState viewState)
    {
        float duration = EnemySpawnerPD.instance != null ? EnemySpawnerPD.instance.EventDuration : 60f;
        return Mathf.Max(0f, duration - GetBase(ref viewState).elapsedTime);
    }

    // "Flash" for the event's ENTIRE lifetime (2026-08-20 — used to settle on white after FlashDuration=5s;
    // now just keeps going the whole time) — a white/[yellow or red] triangle wave with a 1-second period
    // (white at 0s, target color at 0.5s, white at 1s, target color at 1.5s, ...). Driven off elapsedTime
    // (State-backed, resimulation-safe, pause-aware), not Time.time — same reasoning every other cosmetic-
    // but-state-driven value in this codebase uses. Shared across every event (per explicit design), same as
    // GetEventDescription — no concrete event needs to override this.
    //
    // Target color swaps from yellow to red once GetTimeRemaining drops to/below lowTimeWarningThreshold
    // (2026-09-02, "the event is about to time out" tell) — same triangle-wave shape and period throughout,
    // only the color it flashes TOWARD changes, so this never introduces a new flash rhythm to learn, just a
    // more urgent-reading one near the end.
    //
    // A companion black-to-white BACKGROUND flash (same wave, same phase) was tried alongside this
    // (2026-08-28) to make an event's start harder to miss, but immediately walked back the same day — it
    // read as an epilepsy-risk flash in practice, not just "noticeable." Do not re-add a full-panel color
    // flash for this purpose. The floating status-popup callout this comment used to point to as "the
    // direction that was kept" was itself removed entirely 2026-09-02 (see OnEventStart's own doc comment)
    // — the persistent description panel this fill drives is the only start-of-event feedback left.
    private Color GetDescriptionColor(TState viewState)
    {
        float elapsed = GetBase(ref viewState).elapsedTime;

        float phase = elapsed % 1f;
        float t = phase < 0.5f ? phase / 0.5f : (1f - phase) / 0.5f;
        Color target = GetTimeRemaining(viewState) <= lowTimeWarningThreshold ? Color.red : Color.yellow;
        return Color.Lerp(Color.white, target, t);
    }

    // "Time Remaining: X seconds" (no decimal precision — whole seconds, ceiling-rounded so it shows the
    // full duration for that first partial second rather than immediately reading one short, same
    // Mathf.CeilToInt convention PausePD's own countdown display already uses) — appended below whatever
    // GetEventDescription(viewState) returns, generically, for every event, since every event shares this
    // same EnemySpawnerPD-driven timeout window. No concrete event needs to build this itself.
    private string BuildFullDescription(TState viewState)
    {
        return $"{GetEventDescription(viewState)}\nTime Remaining: {Mathf.CeilToInt(GetTimeRemaining(viewState))} seconds";
    }

    // NOT virtual — a fixed, generic "mark resolved" primitive every event shares identically; no concrete
    // event needs to customize this. See EventBaseState.resolved's own doc comment for the full deferred-
    // destruction reasoning this exists for.
    public void MarkResolved() => B.resolved = true;

    // Deferred, VERIFIED-state-gated self-destruction — same "state-critical, only despawn once
    // server-confirmed" convention as RemoteSpikeBurstPD/MortarHazardPD/EnemyProjectilePD's own one-shot
    // deletion. A concrete event that overrides UpdateView for its own visuals MUST call
    // base.UpdateView(viewState, verified) — same "PD lifecycle methods need override+base" convention every
    // other override in this codebase already follows — or this check (and therefore this event's own
    // eventual teardown) silently never runs.
    protected override void UpdateView(TState viewState, TState? verified)
    {
        LocalUIManagerPD.instance?.SetEventDescription(BuildFullDescription(viewState));
        LocalUIManagerPD.instance?.SetEventDescriptionColor(GetDescriptionColor(viewState));

        if (!verified.HasValue) return;
        var v = verified.Value;
        if (GetBase(ref v).resolved) DestroyEvent();
    }

    // No dedicated event-success SFX (2026-08-26, removed) — TriggerBonusAugmentRound() already sets
    // AugmentManagerPD.isPresenting true, which fires that class's own pre-existing levelUpSfx. A separate
    // EventManagerPD.successSfx was layered on top of that at the same moment, which just meant the SAME clip
    // played twice back to back — confirmed live. Relying on the existing levelUpSfx alone was the actual
    // intent all along; if event success ever needs a genuinely DIFFERENT sound from a real level-up, that's
    // the moment to reintroduce a dedicated trigger here, not before.

    // The actual, final hierarchy.Delete() — deliberately never called synchronously from ResolveEvent()
    // itself (which only ever runs Cleanup()+MarkResolved(), both pure predicted-State mutations), so a
    // mispredicted early "this succeeded" guess can still be safely rolled back before anything is
    // irreversibly destroyed. Only ever reached once MarkResolved()'s own write has propagated all the way to
    // VERIFIED state (see UpdateView above) — this is the "DestroyEvent, called after Cleanup, that actually
    // destroys the event" step, just deferred rather than synchronous, per explicit safety design.
    private void DestroyEvent() => hierarchy.Delete(this);
}
