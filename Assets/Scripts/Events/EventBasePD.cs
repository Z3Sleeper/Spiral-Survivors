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
    // particular event needs). playableArea is passed in explicitly (see PlayableAreaPD's own doc comment)
    // rather than read off a global singleton inline — an event that needs to place something (a zone, a
    // spawn point, whatever) calls playableArea.RandomPoint(...) itself, with whatever margin makes sense
    // for ITS OWN geometry (e.g. its own zone radius) — EventManagerPD deliberately doesn't need to know
    // that, it just hands the area over. Can be null if no PlayableAreaPD exists in the scene yet — same
    // "missing optional cross-scene singleton is a harmless no-op" convention as everywhere else in this
    // codebase; an event that cares about placement should null-check before using it.
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

    // Message OnEventStart's own base implementation announces to every player via a white Custom status
    // popup the instant this event begins — e.g. "Capture the Point!". Inspector-editable per event PREFAB
    // (2026-08-26 — was a mandatory abstract C# method every event had to override; moved to a shared field
    // here instead specifically so this can be tuned without touching code). Leave blank to skip the start
    // announcement entirely for a given event. Static text, not live State — this only ever fires once, at
    // the very start.
    [Header("Event Feedback")]
    [Tooltip("Shown to every player via a white status popup the instant this event starts. Leave blank to skip it.")]
    [SerializeField] private string startAnnouncement;

    // Shared, opt-in placement constraint (2026-08-21) — off by default, so an event that doesn't call
    // PlayableAreaPD.RandomPoint at all (or doesn't care) is unaffected. A concrete event's own OnEventStart
    // passes these straight through to RandomPoint's own avoidPlayers/playerOffset params (see that method's
    // own doc comment for the exact "zoneRadius + playerOffset" overlap check this drives) alongside
    // whatever zoneRadius/wallOffset fields that event already declares locally — those stay per-event since
    // their meaning varies (e.g. CaptureThePointPD's own zoneRadius also sizes its visual circle), but
    // "should placement avoid players, and by how much" is the same question for every event, so it lives
    // here once instead of being re-declared per event.
    [Header("Placement")]
    [Tooltip("If enabled, a concrete event's own placement (via PlayableAreaPD.RandomPoint) also avoids spawning within playerOffset of any alive player's own zoneRadius footprint.")]
    [SerializeField] protected bool avoidPlayersOnPlacement = false;
    [Tooltip("Only used when Avoid Players On Placement is enabled — see its own tooltip.")]
    [SerializeField] protected float playerOffset = 0f;

    // Multiplies the announcement popup's own display duration on top of DamageNumber's shared, normal-status
    // baseline (see CharacterStatsPD.NotifyStatusPopup/StatusPopupData's own doc comments for the full
    // threading) — 2x so this one-time "event is starting" callout lingers noticeably longer than a routine
    // Stun/Slow/Dodge popup, per explicit design (2026-08-19).
    private const float AnnouncementDurationMultiplier = 2f;

    // Virtual, not abstract (2026-08-25) — shows the shared event description panel (LocalUIManagerPD.
    // ShowEventPanel) generically for every event, so no concrete event needs to know that panel exists at
    // all. A concrete event's own override MUST call base.OnEventStart(playableArea) — same "PD lifecycle
    // methods need override+base" convention UpdateView below already follows — before/after doing its own
    // event-specific setup.
    //
    // Also fires a Custom status popup (the floating "Stunned!"/"Dodged!"-style callout above a player's own
    // head, NOT the persistent description panel) on EVERY player, white by default, reading startAnnouncement
    // above for the message — a one-time, attention-grabbing "this is starting NOW" the persistent description
    // panel alone doesn't provide. CharacterStatsPD.NotifyStatusPopup is already resimulation-safe (relayed
    // through its own PredictedEvent, same as every other Simulate()-context status-popup caller in this
    // codebase — e.g. CharacterStatsPD.TakeDamage/Slow/etc.), so this is safe to call directly from here
    // despite OnEventStart itself running from Simulate()-context.
    public virtual void OnEventStart(PlayableAreaPD playableArea, int currentTier)
    {
        LocalUIManagerPD.instance?.ShowEventPanel();

        if (string.IsNullOrEmpty(startAnnouncement)) return;
        foreach (var stats in CharacterStatsPD.All.Values)
            stats.NotifyStatusPopup(startAnnouncement, Color.white, AnnouncementDurationMultiplier);
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

    // "Flash" for the event's ENTIRE lifetime (2026-08-20 — used to settle on white after FlashDuration=5s;
    // now just keeps going the whole time) — a white/yellow triangle wave with a 1-second period (white at
    // 0s, yellow at 0.5s, white at 1s, yellow at 1.5s, ...). Driven off elapsedTime (State-backed,
    // resimulation-safe, pause-aware), not Time.time — same reasoning every other cosmetic-but-state-driven
    // value in this codebase uses. Shared across every event (per explicit design), same as
    // GetEventDescription — no concrete event needs to override this.
    private Color GetDescriptionColor(TState viewState)
    {
        float elapsed = GetBase(ref viewState).elapsedTime;

        float phase = elapsed % 1f;
        float t = phase < 0.5f ? phase / 0.5f : (1f - phase) / 0.5f;
        return Color.Lerp(Color.white, Color.yellow, t);
    }

    // Flat 60s, matching EnemySpawnerPD's own default eventDuration (the actual, real timeout that decides
    // when EnemySpawnerPD calls ResolveEvent() on a still-running event) — see this field's own comment for
    // the coupling that creates. Deliberately NOT threaded through from EnemySpawnerPD via OnEventStart —
    // per explicit design, this only needs to LOOK right, counting down off elapsedTime (already
    // State-backed/resimulation-safe) is enough to naturally match EnemySpawnerPD as long as the two values
    // agree. If EnemySpawnerPD's own eventDuration is ever retuned away from 60, update this constant too —
    // nothing enforces them staying in sync automatically.
    private const float DisplayedTimeoutSeconds = 60f;

    // "Time Remaining: X seconds" (no decimal precision — whole seconds, ceiling-rounded so it shows the
    // full DisplayedTimeoutSeconds for that first partial second rather than immediately reading one short,
    // same Mathf.CeilToInt convention PausePD's own countdown display already uses) — appended below
    // whatever GetEventDescription(viewState) returns, generically, for every event, since every event
    // shares this same EnemySpawnerPD-driven timeout window. No concrete event needs to build this itself.
    private string BuildFullDescription(TState viewState)
    {
        float remaining = Mathf.Max(0f, DisplayedTimeoutSeconds - GetBase(ref viewState).elapsedTime);
        return $"{GetEventDescription(viewState)}\nTime Remaining: {Mathf.CeilToInt(remaining)} seconds";
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
