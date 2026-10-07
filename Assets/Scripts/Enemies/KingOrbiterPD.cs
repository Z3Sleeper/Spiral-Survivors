using System.Collections.Generic;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Boss. Extends OrbiterGloomPD's grab-and-orbit mechanic into a 5-phase cycle: Moving -> Pull -> Orbit ->
// Knockback -> Shooting -> repeat. Pull grabs 3 separate rings of Pleb-tier enemies (inner/middle/outer),
// each ring's own plebs pulled in ONE AT A TIME with a staggered windup (see the Pulling case's own doc
// comment in HandleActiveState for the full timing model — exactly OrbiterGloomPD.DispatchPull's own shape,
// per-ring instead of a single group), each ring orbiting at its own radius/speed. Orbit chases players with
// the held rings spinning (the only stunnable+interruptible phase). Knockback freezes every held ring in
// place (SetOrbitAngularSpeed(0), doesn't drop them) and radially knocks back nearby players — same shape as
// KingSpikePD's own SpikeBurst, just knockback instead of damage. Shooting then launches each ring's plebs
// (inner first, then middle, then outer, 2026-08-20 — used to be reversed) at one shared random target per
// ring, ALSO one at a time with a
// staggered windup, each individually aimed from its own position (all converging on that shared target) via
// the new generic IEnemyPD.LaunchAlong primitive — released once they travel the full shootingRange
// regardless of what they hit along the way (damage during flight comes from each pleb's own ordinary,
// untouched contact-damage collider, not any hit-detection code here).
//
// DISPATCH across a phase's own 3 rings is one continuous, uninterrupted sequence, for BOTH Pull and
// Shooting (2026-08-18, same rework for both — used to wait for a ring's own last pleb to fully finish
// pulling-in/flying before even selecting the next ring's candidates/target, which made this boss invincible
// (Pull) or take far longer than necessary (Shooting)): the moment a ring's last pleb has its own windup
// dispatched, the NEXT ring's own selection+first windup fires exactly pullSequentialDelay/
// shootSequentialDelay later, no gap, all the way through. RELEASE (Pull: the damage-multiplier switch +
// actually starting to orbit; Shooting: ReleaseRing once flight completes), by contrast, stays fully
// independent per ring in both phases — inner/middle/outer, same order for both, each release
// the instant their OWN last pleb has actually finished, never gated on how far dispatch has already moved
// on to later rings. See EnterPullWindup/AdvanceDispatchRing (Pull) and
// EnterShootWindupOrAdvance/AdvanceShootDispatchRing (Shoot) for the exact mechanics — identical shape,
// mirrored field-for-field.
//
// No size/elite scaling in this script beyond whatever EnemyBasePD already provides by default — hand-tuned
// boss, prefab is a scaled-up copy of the Orbiter prefab (per explicit design).
public class KingOrbiterPD : EnemyBasePD<KingOrbiterPD.State>
{
    // PullWindup/ShootWindup no longer exist as distinct phases (2026-08-17) — each held pleb now gets its
    // OWN windup, staggered by pullSequentialDelay/shootSequentialDelay off Pulling/ShootFlight's own
    // elapsed timer, rather than one shared windup for the whole ring up front. See Pulling's own comment
    // in HandleActiveState for the full timing model (identical shape for ShootFlight, just LaunchAlong
    // instead of OrbitTarget).
    public enum KingOrbiterPhase { Moving, Pulling, Orbit, KnockbackWindup, ShootFlight }

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   phaseId;
        // Dual-purpose, per phase (same convention KingSpikePD's own remoteSequenceTimer already uses):
        // while phaseId == Pulling, elapsed time since the WHOLE Pull Phase began; while phaseId ==
        // ShootFlight, elapsed since the WHOLE Shooting Phase began — BOTH count UP continuously across all
        // 3 rings now (2026-08-18 — used to reset per ring for both). Every other phase (Moving/Orbit/
        // KnockbackWindup) still counts it DOWN as a plain remaining-time countdown. See the Pulling/
        // ShootFlight cases' own doc comments in HandleActiveState for the full continuous-dispatch/
        // independent-release timing model this drives (identical shape for both, just OrbitTarget vs.
        // LaunchAlong and pull order vs. shoot order).
        public float phaseTimer;
        public float attackCooldownTimer;
        // Which ring's own RELEASE (Pull: damage multiplier switch + BeginOrbiting, see EndPullingRing.
        // Shoot: ReleaseRing, see EndShotRing) is next pending — 0=inner,1=middle,2=outer for BOTH Pull and
        // Shoot (2026-08-20, see ShootPhysicalRing). Deliberately INDEPENDENT
        // of pullDispatchRingIndex/shootDispatchRingIndex below (2026-08-18) — dispatch can run ahead of
        // release, since a ring only releases once its own last pleb has actually finished
        // pulling-in/flying, regardless of how far dispatch has already moved on.
        public int pullRingIndex;
        // Which ring DISPATCH (BeginPull/OrbitTarget calls) is currently filling — 2026-08-18, new field.
        // Always >= pullRingIndex. Advances the instant the current ring's own plebs have ALL been
        // dispatched (see AdvanceDispatchRing) — never waits for them to actually finish arriving.
        public int pullDispatchRingIndex;
        // How many of pullDispatchRingIndex's own held plebs have had BeginPull called on them so far —
        // 2026-08-18, new field (used to be pullSequenceIndex's own job when it was scoped per-ring; that
        // field is now the GLOBAL dispatch count across the whole Pull Phase instead — see its own comment).
        // Reset to 0 whenever pullDispatchRingIndex advances (AdvanceDispatchRing).
        public int pullRingLocalIndex;
        // shootRingIndex/shootDispatchRingIndex/shootRingLocalIndex are Shoot's own exact mirror of
        // pullRingIndex/pullDispatchRingIndex/pullRingLocalIndex above (2026-08-18) — same
        // dispatch-vs-release split, just walking shoot order (ShootPhysicalRing: 0=inner,1=middle,2=outer,
        // 2026-08-20 — used to be reversed) instead of pull order. See AdvanceShootDispatchRing/EndShotRing.
        public int shootRingIndex;
        public int shootDispatchRingIndex;
        public int shootRingLocalIndex;
        // Pull: GLOBAL count of plebs dispatched so far across the WHOLE Pull Phase (inner+middle+outer
        // combined, 2026-08-18 — used to be scoped per-ring) — drives dispatch TIMING only; see
        // pullRingLocalIndex above for which pleb/slot within its own ring a given dispatch maps to. Reset
        // to 0 once, at the start of the whole Pull Phase (EnterPullWindup), never per-ring.
        // Shoot: shootSequenceIndex's own exact mirror, GLOBAL across the whole Shooting Phase (2026-08-18 —
        // used to be scoped per-ring) — reset to 0 once, at EnterShootWindupOrAdvance, never per-ring.
        public int pullSequenceIndex;
        public int shootSequenceIndex;
        // Snapshotted per-ring global-start indices (2026-08-20 fix) — see ShootRingGlobalStart's own doc
        // comment for the bug this replaces (recomputing a ring's own start offset by re-summing EARLIER
        // rings' live held-list counts silently undercounts once an earlier ring has already released and
        // cleared its own list, corrupting release-timing math for every ring after it — and prematurely
        // StopOrbiting()-ing plebs still mid-flight, letting them get re-grabbed, e.g. by the independent
        // Persistent Pleb Shot volley, while visibly still in the air). Written ONCE per ring, in
        // AdvanceShootDispatchRing, from a running tally (shootNextGlobalStart) that only ever grows — never
        // re-derived from any other ring's current state. Indexed by SHOOT-ORDER index (0=first dispatched
        // this phase..2=last), matching shootDispatchRingIndex/shootRingIndex's own indexing, NOT physical
        // ring identity. Three plain ints, not a DisposableList — there are always exactly 3 shoot-order
        // rings, never more/fewer.
        public int shootRingGlobalStart0;
        public int shootRingGlobalStart1;
        public int shootRingGlobalStart2;
        // Running tally (2026-08-20) driving shootRingGlobalStart0/1/2 above — reset to 0 once, at
        // EnterShootWindupOrAdvance (start of the whole Shooting Phase), then incremented by each ring's own
        // heldCount the instant that ring is selected for dispatch. Never read directly anywhere else.
        public int shootNextGlobalStart;
        public DisposableList<PredictedObjectID> innerHeld;
        public DisposableList<PredictedObjectID> middleHeld;
        public DisposableList<PredictedObjectID> outerHeld;
        // Last ring's chosen shot target — excluded from the next ring's random pick when possible ("unique
        // from the last target if possible"). Default(PlayerID) on a fresh spawn just means "nothing to
        // exclude yet", same as every other unguarded default-sentinel PlayerID field in this codebase (e.g.
        // EnemyBaseState.killerOwner).
        public PlayerID lastShotTargetId;
        // Flat, dynamically-grown "who's currently being tracked/shown" list for ShootFlight (2026-09-14
        // rework, replaces a pair of fixed max(ring-count)-sized arrays indexed by ring-local slot position,
        // REUSED across rings). The old ring-local-slot shape was WHY a pleb's own indicator had to despawn
        // the instant it was dispatched/fired, before it had even started flying, let alone finished — the
        // NEXT ring's own dispatch could reclaim that exact same slot number while the PREVIOUS ring's pleb
        // was still genuinely mid-flight (dispatch deliberately runs ahead of release for this boss — see
        // HandleActiveState's ShootFlight case doc comment), so a slot's indicator literally could not
        // outlive dispatch without risking two different plebs fighting over one slot. Per explicit design,
        // a shot pleb's own line indicator must stay visible for its ENTIRE movement (windup THROUGH flight),
        // independent of every other pleb — including ones from a DIFFERENT ring active at the same time —
        // so slot identity had to stop being tied to ring position at all. Now: one entry is appended
        // (DisposableList.Add) the instant a pleb's own windup window opens, and removed (swap-with-last) the
        // instant that SPECIFIC pleb's own flight finishes or it becomes invalid — completely decoupled from
        // ring/dispatch bookkeeping, same "grown/shrunk as needed, no fixed slot identity" shape
        // persistentHeld/persistentIndicatorId's own family of lists already uses (see their own doc
        // comments) — this is genuinely the same pattern, just also needing an explicit "which pleb" key
        // (shootTrackedPleb) since, unlike a persistent volley's one-shot-per-target-player rebuild, entries
        // here open/close continuously and never get rebuilt-from-scratch as a batch. See
        // UpdateShootBearingTracking/DespawnFinishedShootIndicators for where these actually get
        // added/updated/removed, and FindTrackedShootIndex for the by-pleb lookup every other access uses.
        //
        // shootTrackedPleb: the actual key — which held pleb (PredictedObjectID) this entry belongs to.
        public DisposableList<PredictedObjectID> shootTrackedPleb;
        // shootTrackedBearing: this pleb's own tracked aim DIRECTION (unit Vector3, not a raw angle — see the
        // 2026-08-24 doc history below for why a Vector3, kept verbatim since it's still exactly why this
        // stays a Vector3 post-rework). Vector3.zero means "not yet seeded" (this entry was JUST added this
        // tick) — the sentinel UpdateShootBearingTracking checks to know when to seed fresh instead of
        // rate-cap-incrementing. Frozen (never written again) once shootTrackedFired[i] is true. Both the
        // windup indicator's own aim (LineIndicatorPD.SetAim) AND the real LaunchAlong direction
        // (FireShotRing) read this SAME tracked value, so what's shown is always exactly what fires.
        //
        // Vector3, NOT a raw degree float (2026-08-24 fix, was a jitter bug confirmed live) — PurrDiction
        // linearly interpolates every plain State field for viewState/UpdateView's own smooth-between-ticks
        // rendering, same as it does for a Vector3 position. A raw ANGLE interpolated that way breaks
        // violently every time it crosses the ±180°/0°-360° wraparound (Mathf.Atan2's own range boundary) —
        // the interpolated value snaps through the OPPOSITE direction for one frame, visible as constant
        // jitter on the windup indicator lines. A unit direction Vector3 has no such discontinuity: two
        // nearby directions (which is all any two adjacent ticks can ever be, since tracking is itself
        // rate-capped) interpolate to a reasonable in-between vector with nothing to snap across. Rate-cap
        // math uses Vector3.RotateTowards instead of Mathf.MoveTowardsAngle — see
        // UpdateShootBearingTracking's own doc comment.
        public DisposableList<Vector3> shootTrackedBearing;
        // shootTrackedIndicator: this entry's own spawned LineIndicatorPD id. default(PredictedObjectID)
        // briefly means "not yet spawned" (the one tick between an entry opening and
        // UpdateShootBearingTracking's own ResolveOrSpawnLineIndicator call resolving it) — hierarchy.
        // GetGameObject() on a default/stale id just returns null, same check already used everywhere else in
        // this file for held-pleb ids.
        public DisposableList<PredictedObjectID> shootTrackedIndicator;
        // shootTrackedFired: true once this pleb's own FireShotRing call has actually dispatched it (launched
        // it flying) — from that point on, UpdateShootBearingTracking stops touching shootTrackedBearing[i]
        // for this entry (aim locked, matches the old "burst-locked" feel every rate-capped tracker in this
        // codebase already has), and DespawnFinishedShootIndicators becomes responsible for removing this
        // entry the instant the underlying pleb's own IsLaunching flips back false (flight complete).
        public DisposableList<bool> shootTrackedFired;
        // shootTrackedWindupStart: the exact state.phaseTimer value this entry's own windup window opened at
        // — lets UpdateShootIndicatorFills (view layer) recompute "how far into MY OWN windup am I" per entry
        // (localElapsed = phaseTimer - windupStart) without needing to know which ring/global-dispatch-index
        // this pleb ever belonged to. Naturally stays fixed once set (an entry's own windup only ever opens
        // once); clamped to 1 by the fill's own Mathf.Clamp01 once localElapsed exceeds shotWindupDuration
        // (post-fire, still tracked but no longer winding up), which reads as "fully charged" for the rest of
        // this entry's life — an intentional, harmless simplification (see the fill display's own doc comment
        // if this ever needs a "recently launched" visual distinction instead).
        public DisposableList<float> shootTrackedWindupStart;

        // Persistent Pleb Shots (2026-08-19) — fires an independent volley at EVERY non-invisible alive
        // player on a flat interval, entirely independent of phaseId above (runs regardless of what the
        // boss's own FSM is currently doing — see Simulate()'s own UpdatePersistentPlebShots tail).
        // persistentHeld/persistentTargetId/persistentBearingDir are parallel lists, one entry per
        // currently-targeted player this volley (rebuilt fresh every StartPersistentVolley call, unlike
        // innerHeld/middleHeld/outerHeld's own fixed-per-tier sizing) — see StartPersistentVolley's own doc
        // comment for the full grab/windup/fire/release flow. Vector3 direction, same 2026-08-24
        // wraparound-jitter fix as shootBearingDir above — see its own doc comment.
        public DisposableList<PredictedObjectID> persistentHeld;
        public DisposableList<PlayerID>          persistentTargetId;
        public DisposableList<Vector3>           persistentBearingDir;
        // Same role as shootTrackedIndicator above, just for the persistent-volley pool — parallel to
        // persistentHeld/persistentTargetId/persistentBearingDir, rebuilt fresh every StartPersistentVolley
        // call (a flat, non-reused list, same shape shootTrackedIndicator's own family uses post-2026-09-14
        // rework — see that field's own doc comment).
        public DisposableList<PredictedObjectID> persistentIndicatorId;
        // Each slot's own assigned target's position, recorded every tick that specific target is actually
        // alive AND visible — frozen (not updated) while dead or invisible (2026-08-22). Lets an in-progress
        // shot keep rate-cap-rotating toward wherever its target was LAST actually alive/seen instead of
        // getting stuck exactly where the bearing happened to be mid-rotation, or following a dead player's
        // transform to wherever it ends up post-death — once a slot's own windup starts, nothing stops it
        // (explicit design: dying/going invisible mid-windup never cancels an in-progress shot, only ever
        // freezes what it's aiming at). See UpdatePersistentBearingTracking's own doc comment. Per-slot
        // (unlike EnemyBasePD's own single lastKnownTargetPosition) because each persistent-volley slot
        // tracks a DIFFERENT specific player, not one shared target.
        public DisposableList<Vector3> persistentLastKnownPosition;
        public float persistentCooldownTimer;
        public bool  persistentVolleyActive;
        // Dual-purpose (same convention as `phaseTimer`/KingSpikePD.remoteSequenceTimer): true once
        // FirePersistentVolley has actually launched this volley's plebs, so persistentVolleyTimer below
        // switches from "elapsed windup time" to "elapsed flight time".
        public bool  persistentShotFired;
        // Counts UP from 0 to shotWindupDuration while winding up (persistentShotFired == false), then reset
        // to 0 and counts UP again to shootingRange/shotSpeed once fired (persistentShotFired == true) — the
        // wait-for-flight-to-land timer before FinishPersistentVolley() releases the held plebs back to normal.
        public float persistentVolleyTimer;

        // Runtime-scaled — see EnemyBaseState.scaledMaxHealth's own doc comment for why these live in State
        // rather than as a plain field mutated by OnConfigureSpawn (2026-09-22 follow-up pass).
        public float scaledPullSequentialDelay;
        public float scaledShootSequentialDelay;
        public int   scaledInnerCount;
        public int   scaledMiddleCount;
        public int   scaledOuterCount;
        public float scaledRotationRateDegPerSecond;
        public float scaledAttackCooldown;
        public float scaledPersistentShotInterval;

        public void Dispose()
        {
            b.contactCooldowns.Dispose();
            b.killLaunchHitIds.Dispose();
            innerHeld.Dispose();
            middleHeld.Dispose();
            outerHeld.Dispose();
            shootTrackedPleb.Dispose();
            shootTrackedBearing.Dispose();
            shootTrackedIndicator.Dispose();
            shootTrackedFired.Dispose();
            shootTrackedWindupStart.Dispose();
            persistentHeld.Dispose();
            persistentTargetId.Dispose();
            persistentBearingDir.Dispose();
            persistentIndicatorId.Dispose();
            persistentLastKnownPosition.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.innerHeld = innerHeld.Duplicate();
            copy.middleHeld = middleHeld.Duplicate();
            copy.outerHeld = outerHeld.Duplicate();
            copy.shootTrackedPleb = shootTrackedPleb.Duplicate();
            copy.shootTrackedBearing = shootTrackedBearing.Duplicate();
            copy.shootTrackedIndicator = shootTrackedIndicator.Duplicate();
            copy.shootTrackedFired = shootTrackedFired.Duplicate();
            copy.shootTrackedWindupStart = shootTrackedWindupStart.Duplicate();
            copy.persistentHeld = persistentHeld.Duplicate();
            copy.persistentTargetId = persistentTargetId.Duplicate();
            copy.persistentBearingDir = persistentBearingDir.Duplicate();
            copy.persistentIndicatorId = persistentIndicatorId.Duplicate();
            copy.persistentLastKnownPosition = persistentLastKnownPosition.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    private KingOrbiterPhase GetPhase(ref State s) => (KingOrbiterPhase)s.phaseId;

    [Header("Damage")]
    // Applied the instant a pleb is selected (TryGrabRing, at Pulling entry) — see orbitDamageMultiplier/
    // shootingDamageMultiplier for the other two phases' own overrides.
    [SerializeField] private float pullDamageMultiplier     = 2f;
    // Applied once a ring's own Pulling completes; stays applied through Orbit AND KnockbackWindup (held
    // plebs keep this multiplier while frozen for the knockback, per explicit design).
    [SerializeField] private float orbitDamageMultiplier    = 1f;
    // Applied at each ring's own ShootFlight entry (EnterShootWindupOrAdvance).
    [SerializeField] private float shootingDamageMultiplier = 1f;

    [Header("Pull - Shared")]
    [SerializeField] private float grabRadius     = 25f;
    [SerializeField] private float windupDuration = 1f;
    // Flat units/sec (2026-09-12, was pullDuration — a flat seconds-to-arrive value, meaning every held
    // pleb took the same time regardless of how far it started) — matches shotSpeed's own flat-speed
    // convention instead, so pulling and shooting both read as the same consistent travel speed rather
    // than pulling feeling oddly duration-locked. See IEnemyPD.OrbitTarget's own doc comment for the
    // MoveTowards mechanic this now drives.
    [SerializeField] private float pullSpeed      = 30f;
    // Gap between each held pleb's own OrbitTarget() dispatch during Pulling (2026-08-17 — used to pull a
    // whole ring in one shot) — see BeginPull's own doc comment. Tier1-authored, pristine read-only baseline
    // forever (2026-09-22) — scaled fresh into State.scaledPullSequentialDelay by OnConfigureSpawn() (see
    // that method's own doc comment for the RECIPROCAL formula shape this uses, deliberately not the linear
    // shrink every other duration field on this boss uses, and EnemyBaseState.scaledMaxHealth's own doc
    // comment for why this needs to be in State at all).
    [SerializeField] private float pullSequentialDelay = 0.25f;

    [Header("Pull - Inner Ring")]
    // Tier1-authored, pristine read-only baseline forever — scaled fresh into State.scaledInnerCount.
    [SerializeField] private int   innerCount             = 2;
    [SerializeField] private float innerOrbitRadius        = 2f;
    [SerializeField] private float innerOrbitAngularSpeed  = 120f;

    [Header("Pull - Middle Ring")]
    // Tier1-authored, pristine read-only baseline forever — scaled fresh into State.scaledMiddleCount.
    [SerializeField] private int   middleCount             = 3;
    [SerializeField] private float middleOrbitRadius        = 5.5f;
    [SerializeField] private float middleOrbitAngularSpeed  = 60f;

    [Header("Pull - Outer Ring")]
    // Tier1-authored, pristine read-only baseline forever — scaled fresh into State.scaledOuterCount.
    [SerializeField] private int   outerCount             = 4;
    [SerializeField] private float outerOrbitRadius        = 9f;
    [SerializeField] private float outerOrbitAngularSpeed  = 30f;

    [Header("Orbit")]
    [SerializeField] private float orbitDuration            = 10f;

    [Header("Knockback")]
    // No longer tier-scaled (2026-09-01, removed knockbackChargeTimeTierGrowth) — a flat, tier-independent
    // value now, unlike pullSequentialDelay above, which still shrinks per tier.
    [SerializeField] private float        knockbackChargeTime = 1.5f;
    [SerializeField] private float        knockbackRadius     = 12f;
    [SerializeField] private float        knockbackForce      = 150f;
    [SerializeField] private GameObject   knockbackIndicator;
    [SerializeField] private WindupFillPD knockbackFill;

    [Header("Shooting")]
    [SerializeField] private float shootingRange     = 40f;
    [SerializeField] private float shotWindupDuration = 1f;
    // No longer tier-scaled (2026-09-02, removed shotSpeedTierGrowth) — a flat, tier-independent value now,
    // unlike pullSequentialDelay above, which still shrinks per tier.
    [SerializeField] private float shotSpeed          = 50f;
    // Gap between each held pleb's own LaunchAlong() dispatch during ShootFlight (2026-08-17 — used to
    // launch a whole ring in one shot) — see FireShotRing's own doc comment. Independently tunable from
    // pullSequentialDelay (separate attack, separate feel), per explicit design. Tier1-authored baseline,
    // scaled per tier by shootSequentialDelayTierDivisor below (applied in OnConfigureSpawn(), 2026-09-02) —
    // see that field's own doc comment for why this uses a RECIPROCAL formula instead of the linear-shrink
    // shape every other duration field on this boss uses.
    [SerializeField] private float shootSequentialDelay = 0.25f;
    // Max bearing turn rate while a held pleb is tracking its own shot during ShootFlight (2026-08-21) —
    // copied from ShielderGloomPD's own rotationRateDegPerSecond/rotationRateDegPerSecondTierGrowth (45
    // base, 0.2 tier growth). Caps how fast a held pleb's tracked aim can swing so circling this boss during
    // its Shoot windup is a real counterplay option — see UpdateShootBearingTracking's own doc comment.
    // Pristine read-only baseline forever, same as pullSequentialDelay above — scales into
    // State.scaledRotationRateDegPerSecond.
    [SerializeField] private float rotationRateDegPerSecond = 45f;

    [Header("Persistent Pleb Shots")]
    // Fires an independent volley at EVERY non-invisible alive player on a flat interval, entirely
    // independent of this boss's own phaseId FSM (same tail-in-Simulate() shape as KingSpikePD's own
    // Persistent Remote Spike Bursts) — grabs one nearby (to THAT player, within grabRadius)
    // non-held pleb per qualifying player, winds it up, then launches it. Inherits shootingDamageMultiplier/
    // shotWindupDuration/shotSpeed/rotationRateDegPerSecond (and whichever of those still have tier scaling —
    // shotSpeed no longer does, see its own field comment) straight from the Shooting header's own fields
    // above — deliberately no separate copies of any of those, per explicit
    // design ("inherit the exact same shooting parameters as the real deal"). See StartPersistentVolley's own
    // doc comment for the full grab/windup/fire/release flow.
    [SerializeField] private float persistentShotInterval = 3f;

    // Sized to this game's own hard player cap instead of anything tier/ring-derived — see
    // feedback_multiplayer_scale_reminder.
    private const int MaxConcurrentPersistentTargets = 4;

    [Header("Attack Cadence")]
    [SerializeField] private float attackCooldown = 10f;

    [Header("Tier Scaling")]
    // Duration fields SHRINK per tier (base * (1 - (tier-1)*growth)) — "X% faster" means less time, not more.
    // Rate/count fields GROW per tier — see each field's own default/comment for which shape it uses. Linear
    // per tier off the tier1-authored base in all cases, never compounding/exponential (user's explicit
    // "multiplicative fields multiply base, no exponential tier scaling" rule) — same shape
    // EnemySpawnerPD/ShielderGloomPD's own *TierGrowth fields already establish. pullSequentialDelayTierDivisor/
    // shootSequentialDelayTierDivisor below are the one deliberate exception to this linear shape — see their
    // own doc comment for why.
    [SerializeField] private int   innerCountTierGrowth          = 1;
    [SerializeField] private int   middleCountTierGrowth         = 2;
    [SerializeField] private int   outerCountTierGrowth          = 3;
    // Same linear-per-tier shape as every other *TierGrowth field — see ShielderGloomPD's own identical
    // field for the precedent this was copied from.
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;

    // RECIPROCAL formula (2026-09-02, replaces the original linear pullSequentialDelayTierGrowth), not the
    // linear shrink every other duration field on this boss uses — delay(tier) = authoredDelay / (1 +
    // (tier-1) * divisor), applied in OnConfigureSpawn() below. Deliberate departure: innerCount/middleCount/
    // outerCount all scale LINEARLY with tier (count(tier) = base * tier, since their own *TierGrowth values
    // are set equal to their own base counts — see each field's own default), so the total held count across
    // a whole Pull/Shoot phase also grows linearly with tier. A linear shrink on the per-pleb delay can only
    // ever cancel that out exactly AT ONE tier, overshooting badly at every other tier in between (confirmed
    // via direct calculation — a single linear rate swings the whole phase's total dispatch time by 30-40%+
    // across the tier range no matter which tier it's anchored to). A RECIPROCAL shrink is the exact
    // structural match instead: count(tier) * delay(tier) = (base*tier) * (authoredDelay / (1+(tier-1)*k))
    // reduces to a tier-independent constant exactly when k = 1 (the default) — the whole Pull/Shoot phase's
    // total dispatch time stays IDENTICAL across every tier, not just approximately close. k is still a real
    // tunable (0 = no scaling at all, same "neutral means untouched" convention every other *TierGrowth field
    // uses) in case the count-growth curve above ever gets retuned away from the current "growth == base"
    // shape, which would call for a different k to keep re-matching it.
    [SerializeField] private float pullSequentialDelayTierDivisor = 1f;
    // Identical formula/reasoning to pullSequentialDelayTierDivisor above, its own separate field per the
    // same "independently tunable from pullSequentialDelay" design shootSequentialDelay's own field comment
    // already establishes.
    [SerializeField] private float shootSequentialDelayTierDivisor = 1f;

    // Pull Phase's own indicator pool — plain Instantiate()'d, persistent, SetActive-toggled (2026-08-19
    // shape, unchanged). Pull-phase indicators recompute fresh from already-smooth live transform reads every
    // render frame (see UpdatePullLineIndicators) — never had a jitter problem, so never needed the
    // spawned-object treatment ShootFlight/Persistent Volley got (see lineIndicatorPrefab below). No longer
    // shared with ShootFlight (2026-08-22) — those moved to their own spawn-per-slot LineIndicatorPD system.
    [Header("Indicators")]
    [SerializeField] private Transform pullIndicatorTemplate;

    private Transform[]    _pullLineIndicators;
    private WindupFillPD[] _pullLineFills;

    // ShootFlight + Persistent Volley indicator prefab (2026-08-22) — a generic LineIndicatorPD, shared by
    // both since they're visually/functionally identical (span = shootingRange, same pose math). Spawned via
    // hierarchy.Create() (see UpdateShootBearingTracking/UpdatePersistentBearingTracking), not Instantiate(),
    // specifically so its own sibling PredictedTransform component actually ticks/smooths its render pose —
    // PredictedTransform only registers for objects spawned through PurrNet's hierarchy system. Must be
    // registered in PredictedPrefabs.asset like any other spawned effect.
    [SerializeField] private GameObject lineIndicatorPrefab;

    // Scratch buffers — cleared and reused every call, same zero-alloc convention as OrbiterGloomPD's own
    // _grabCandidates.
    private static readonly List<IEnemyPD>        _grabCandidates     = new();
    private static readonly List<CharacterStatsPD> _shotTargetScratch = new();
    private static readonly List<PlayerID>         _shotTargetIdScratch = new();

    // Plain (not predicted State) snapshot of the current ring's chosen shot target — same
    // KingSpikePD._chargeDirectionSnapshot precedent: instantly available on every machine the moment
    // PickShotTarget() decides it (no network round-trip/interpolation). Committed for this ring's own full
    // duration once picked — dying/going invisible mid-ring never causes a mid-ring re-pick onto someone
    // else (see UpdateShootBearingTracking's own doc comment); only ever reassigned by a fresh
    // PickShotTarget() call (new ring, or retrying while still null because nobody was valid at all).
    private CharacterStatsPD _shotTargetSnapshot;
    // Companion to _shotTargetSnapshot — its position, recorded every tick it's actually alive+visible,
    // frozen otherwise (dead or invisible). See UpdateShootBearingTracking's own doc comment for why: once a
    // ring's own windup starts, nothing stops it — dying/vanishing mid-ring only ever freezes what it's
    // aiming at, never cancels or redirects it elsewhere.
    private Vector3 _shotTargetLastKnownPosition;

    protected override void LateAwake()
    {
        base.LateAwake();
        // Full cooldown on spawn, not KingSpikePD's own half-cooldown grace — per explicit design, this
        // boss's Pull Phase would otherwise fire almost immediately, before the field has had a chance to
        // spawn more than a handful of plebs to actually grab.
        currentState.attackCooldownTimer = attackCooldown;
        // Same full-grace reasoning, and same "avoid the previous cooldown-starts-at-0-for-a-frame bug"
        // requirement explicitly called out for this mechanic — a plain currentState assignment here, BEFORE
        // OnConfigureSpawn scales persistentShotInterval for real, means the very first Simulate() tick this
        // life ever runs already sees a full (if not yet correctly tier/difficulty-scaled) cooldown instead
        // of the struct's own zero-value default, which would otherwise fire a volley on frame one.
        currentState.persistentCooldownTimer = persistentShotInterval;
        // Indicator pool build moved to OnConfigureSpawn() (2026-08-20, same reasoning as
        // OrbiterGloomPD.EnsureIndicatorPoolCapacity) — ring counts are now tier-scaled there, and
        // ConfigureSpawn always runs AFTER LateAwake, so building here would lock the pool to the
        // PRE-scaling counts.
    }

    // Tier1-authored innerCount/middleCount/outerCount, scaled by each field's own *TierGrowth (see the
    // "Tier Scaling" header's own doc comment for the count-grows-linearly formula shape). pullSequentialDelay/
    // shootSequentialDelay use the RECIPROCAL formula instead (see pullSequentialDelayTierDivisor's own doc
    // comment for why) — divides the AUTHORED baseline directly, not the field's own current value, same
    // "read the snapshot, not the live field" convention rotationRateDegPerSecond's own formula right below
    // already uses. knockbackChargeTime/shotSpeed are no longer tier-scaled at all (2026-09-01/09-02, tier
    // growth removed from both — both stay flat regardless of tier). spawnTier is already final by the time
    // this runs (set earlier in the same ConfigureSpawn() call) — ring counts scaled BEFORE
    // EnsureIndicatorPoolCapacity() so the pool always sizes to this life's FINAL counts.
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        currentState.scaledPullSequentialDelay = pullSequentialDelay / (1f + (spawnTier - 1) * pullSequentialDelayTierDivisor);
        currentState.scaledShootSequentialDelay = shootSequentialDelay / (1f + (spawnTier - 1) * shootSequentialDelayTierDivisor);
        currentState.scaledInnerCount  = innerCount  + (spawnTier - 1) * innerCountTierGrowth;
        currentState.scaledMiddleCount = middleCount + (spawnTier - 1) * middleCountTierGrowth;
        currentState.scaledOuterCount  = outerCount  + (spawnTier - 1) * outerCountTierGrowth;
        currentState.scaledRotationRateDegPerSecond = rotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);
        // Sized to the SUM of all 3 rings (2026-09-14, was Mathf.Max) — pull indicators now stay visible
        // through each pleb's ENTIRE movement (windup through actually arriving), not just its own windup, so
        // plebs from MULTIPLE rings can genuinely be active/showing at once (dispatch deliberately runs ahead
        // of release — see UpdatePullLineIndicators's own doc comment). A pool sized to only the largest
        // single ring would run out of indicators the moment more than one ring's worth of plebs are active
        // simultaneously.
        EnsureIndicatorPoolCapacity(pullIndicatorTemplate, ref _pullLineIndicators, ref _pullLineFills,
            currentState.scaledInnerCount + currentState.scaledMiddleCount + currentState.scaledOuterCount);
        // No shoot-side capacity call anymore (2026-09-14) — state.shootTrackedPleb/etc. are now flat,
        // dynamically-grown lists (Add/RemoveAt), not a fixed-size array needing pre-sizing. See their own
        // doc comment on State.

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        currentState.scaledAttackCooldown = attackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;

        // Same Threat Level 5 scaling, explicitly requested to apply to Boss Persistent Attacks too — re-set
        // the placeholder persistentCooldownTimer LateAwake() already assigned above, using the now-scaled
        // value.
        currentState.scaledPersistentShotInterval = persistentShotInterval * attackCooldownMultiplier;
        currentState.persistentCooldownTimer = currentState.scaledPersistentShotInterval;
    }

    // Lazy + GROWABLE, not build-once (2026-08-20 — same fix OrbiterGloomPD.EnsureIndicatorPoolCapacity
    // already established for the identical problem) — called from OnConfigureSpawn(), after tier scaling
    // has already been applied to innerCount/middleCount/outerCount, so this always sees the FINAL counts
    // for whichever life is currently spawning. Growable specifically because a single pooled instance can
    // be reused across DIFFERENT tiers over its lifetime, so a fixed build-once array could come up short on
    // a LATER, higher-tier life even though it was correctly sized for whichever life built it first. Never
    // shrinks — a later lower-tier life just uses a prefix of the larger arrays.
    private void EnsureIndicatorPoolCapacity(Transform template, ref Transform[] indicators, ref WindupFillPD[] fills, int maxCount)
    {
        if (template == null) return;
        int have = indicators?.Length ?? 0;
        if (have >= maxCount) return;

        var newIndicators = new Transform[maxCount];
        var newFills = new WindupFillPD[maxCount];
        for (int i = 0; i < have; i++)
        {
            newIndicators[i] = indicators[i];
            newFills[i] = fills[i];
        }
        for (int i = have; i < maxCount; i++)
        {
            var clone = Instantiate(template, template.parent);
            // Unparented immediately (2026-08-20, same fix/reasoning as LeaperGloomPD's own _windupIndicator
            // detaching) — UpdatePullLineIndicators already writes this clone's absolute WORLD position/
            // rotation every render frame via SetPositionAndRotation, but
            // while still parented under this boss's own (PredictedTransform-smoothed) hierarchy, a LATER
            // same-frame correction to that parent still visibly drags the child along with it — the write
            // above only matches the parent's pose at the instant it ran, not after. worldPositionStays:true
            // keeps the clone exactly where Instantiate placed it (irrelevant anyway — always SetActive(false)
            // until its position is freshly overwritten the same frame it's shown, so no placement carries
            // over regardless).
            clone.SetParent(null, true);
            clone.gameObject.SetActive(false);
            newIndicators[i] = clone;
            newFills[i] = clone.GetComponentInChildren<WindupFillPD>(true);
        }
        indicators = newIndicators;
        fills      = newFills;
        template.gameObject.SetActive(false);
    }

    protected override State GetInitialState()
    {
        var s = base.GetInitialState();
        s.scaledPullSequentialDelay = pullSequentialDelay;
        s.scaledShootSequentialDelay = shootSequentialDelay;
        s.scaledInnerCount = innerCount;
        s.scaledMiddleCount = middleCount;
        s.scaledOuterCount = outerCount;
        s.scaledRotationRateDegPerSecond = rotationRateDegPerSecond;
        s.scaledAttackCooldown = attackCooldown;
        s.scaledPersistentShotInterval = persistentShotInterval;
        s.shootTrackedPleb = DisposableList<PredictedObjectID>.Create(0);
        s.shootTrackedBearing = DisposableList<Vector3>.Create(0);
        s.shootTrackedIndicator = DisposableList<PredictedObjectID>.Create(0);
        s.shootTrackedFired = DisposableList<bool>.Create(0);
        s.shootTrackedWindupStart = DisposableList<float>.Create(0);
        s.innerHeld  = DisposableList<PredictedObjectID>.Create(innerCount);
        s.middleHeld = DisposableList<PredictedObjectID>.Create(middleCount);
        s.outerHeld  = DisposableList<PredictedObjectID>.Create(outerCount);
        s.persistentHeld = DisposableList<PredictedObjectID>.Create(0);
        s.persistentTargetId = DisposableList<PlayerID>.Create(0);
        s.persistentBearingDir = DisposableList<Vector3>.Create(0);
        s.persistentIndicatorId = DisposableList<PredictedObjectID>.Create(0);
        s.persistentLastKnownPosition = DisposableList<Vector3>.Create(0);
        return s;
    }

    // ── Ring helpers ─────────────────────────────────────────────────────────
    // No boss-specific enrage behavior anymore (2026-08-20) — this boss just uses EnemyBasePD's fully
    // generic Move Speed + Damage enrage system as-is (see that class's own "Enrage" Inspector header),
    // same as every other boss. Held-pleb damage overrides multiply by EnrageDamageMultiplier explicitly
    // wherever they compute damage separately from GetOwnDamage().
    private int RingCount(ref State state, int ring) => ring switch { 0 => state.scaledInnerCount, 1 => state.scaledMiddleCount, _ => state.scaledOuterCount };
    private float RingRadius(int ring)       => ring switch { 0 => innerOrbitRadius, 1 => middleOrbitRadius, _ => outerOrbitRadius };
    // Middle ring runs counter to inner/outer — negated here (not on the Inspector field itself, which stays
    // a plain positive "how fast" value like its siblings) so every caller automatically gets the correctly
    // signed speed without needing to remember to flip it. WrapOrbitAngle's own double-mod handles a negative
    // angularSpeedDegPerSec correctly (wraps into [0,360) either direction), so this is the only change
    // needed — LateSimulate()'s shared orbit math doesn't care about sign otherwise.
    private float RingAngularSpeed(int ring) => ring switch { 0 => innerOrbitAngularSpeed, 1 => -middleOrbitAngularSpeed, _ => outerOrbitAngularSpeed };

    // Shooting goes inner->middle->outer (2026-08-20, same order as Pull — used to be the reverse,
    // outer->middle->inner, per the original design; identity mapping now, same as Pull's own
    // pullDispatchRingIndex-is-already-the-physical-ring-index shape needs no transform either).
    private static int ShootPhysicalRing(int shootRingIndex) => shootRingIndex;

    private static ref DisposableList<PredictedObjectID> RingHeldList(ref State state, int ring)
    {
        switch (ring)
        {
            case 0:  return ref state.innerHeld;
            case 1:  return ref state.middleHeld;
            default: return ref state.outerHeld;
        }
    }

    // Evenly-spaced slot angle for held-index i out of `count` — same formula/purpose as
    // OrbiterGloomPD.SlotAngleDeg.
    private static float SlotAngleDeg(int index, int count) => index * (360f / count);

    // ── Active state ─────────────────────────────────────────────────────────
    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        var phase = GetPhase(ref state);
        // Only ticks while actually in Moving (2026-08-19 — uniform across every multi-phase enemy's own
        // attack-cadence cooldown now, see OrbiterGloomPD's own fix) — frozen through the whole
        // Pulling/Orbit/KnockbackWindup/ShootFlight sequence rather than ticking (and going increasingly
        // negative) throughout it.
        if (phase == KingOrbiterPhase.Moving)
            state.attackCooldownTimer -= delta;

        switch (phase)
        {
            case KingOrbiterPhase.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f)
                {
                    // Turtles up for the WHOLE Pull Phase — one continuous invincibility window spanning
                    // every ring's windup+pulling (not per-ring flickering, per explicit design). Reverted
                    // once the entire Pull Phase resolves — see FinishPullPhase's own doc comment.
                    SetHurtboxDisabled(true);
                    EnterPullWindup(ref state);
                }
                break;

            // phaseTimer counts UP here — elapsed since the WHOLE Pull Phase began (2026-08-18: used to
            // reset per ring; now counts up continuously across all 3 rings, never reset until the phase
            // ends). Two fully independent halves:
            //
            // DISPATCH (pullDispatchRingIndex/pullRingLocalIndex/pullSequenceIndex) walks inner -> middle
            // -> outer as ONE continuous global sequence — held pleb i's own windup window is
            // [globalIndex*pullSequentialDelay, globalIndex*pullSequentialDelay+windupDuration), and its
            // actual OrbitTarget() dispatch (BeginPull) lands the instant that window closes, exactly
            // OrbiterGloomPD's own per-slot timing model. The moment the CURRENT ring's own last pleb is
            // dispatched, dispatch immediately advances into the NEXT ring — selecting its candidates
            // (AdvanceDispatchRing) right then, so that ring's own first dispatch still lands right on the
            // very next scheduled slot, no gap at all between rings. With windupDuration >
            // pullSequentialDelay (the common case) several plebs — even from different rings — can sit
            // mid-windup simultaneously.
            //
            // RELEASE (pullRingIndex) is the damage-multiplier switch + actually starting to orbit
            // (EndPullingRing) — fully decoupled from dispatch progress above: ring R releases the instant
            // EVERY member ITS OWN dispatched-so-far held list has genuinely arrived at its orbit slot
            // (AllArrivedAtOrbitSlot, a real per-pleb IsWaitingToOrbit check — see that method's own doc
            // comment; this used to be a RingGlobalStart(R)+windup+pullDuration TIME ESTIMATE, replaced
            // 2026-08-31, and doubly so once Pulling itself became speed-based 2026-09-12, since a flat
            // speed means different pull distances now genuinely take different real time to arrive —
            // there's no shared duration left to estimate from at all), regardless of how far dispatch has
            // already moved on to later rings. Inner/middle/outer each release independently on their own
            // schedule — never gated on the other two.
            //
            // See BeginPull/EndPullingRing/AdvanceDispatchRing for the exact mechanics, and
            // UpdatePullLineIndicators for how the same per-slot window math drives each indicator independently.
            case KingOrbiterPhase.Pulling:
            {
                // Scaled by EffectiveAttackSpeed (2026-08-31, was plain delta) — the windup/dispatch
                // thresholds below (pullSequentialDelay/windupDuration) stay fixed CONSTANTS; this only
                // changes how fast phaseTimer climbs toward them, so a Slow/difficulty-scaled attack speed
                // correctly stretches or compresses the whole Pull sequence's real-time pacing. Release
                // itself no longer reads phaseTimer at all (AllArrivedAtOrbitSlot, real per-pleb arrival —
                // see this case's own doc comment above), so pullSpeed doesn't need to appear here; it's
                // already scaled by EffectiveAttackSpeed independently, inside LateSimulate()'s own Pulling
                // branch.
                state.phaseTimer += delta * EffectiveAttackSpeed;

                // Dispatch — while loop, not if, at each level: guards against a single tick's delta
                // covering more than one scheduled dispatch (a brief stall), so a pull can never be silently
                // skipped, same convention RogueSpinAbilityPD's own hit-scheduling loop uses. The OUTER while
                // is what lets dispatch cascade straight through an empty ring (0 held) or across a ring
                // boundary within the very same tick, with no wasted frame.
                while (state.pullDispatchRingIndex <= 2)
                {
                    int dispatchCount = RingHeldList(ref state, state.pullDispatchRingIndex).Count;

                    while (state.pullRingLocalIndex < dispatchCount &&
                           state.phaseTimer >= state.pullSequenceIndex * state.scaledPullSequentialDelay + windupDuration)
                    {
                        BeginPull(ref state, state.pullDispatchRingIndex, state.pullRingLocalIndex);
                        state.pullRingLocalIndex++;
                        state.pullSequenceIndex++;
                    }

                    if (state.pullRingLocalIndex < dispatchCount) break; // this ring still has plebs waiting their own turn
                    if (!AdvanceDispatchRing(ref state)) break;          // ring 2 (outer) just finished dispatching too
                }

                // Release — independent of dispatch above; only ever looks at rings dispatch has already
                // reached (pullRingIndex <= pullDispatchRingIndex), since an unreached ring's held list may
                // still hold stale data from a previous life. Gated on AllArrivedAtOrbitSlot (2026-08-31,
                // replaced a phaseTimer-threshold ESTIMATE of when the last-dispatched pleb should have
                // finished pulling in) — see IEnemyPD.IsWaitingToOrbit's own doc comment for why: a derived
                // time threshold and each pleb's own real arrival could only be trusted to agree by careful
                // construction (both scaled by the identical EffectiveAttackSpeed every tick); this instead
                // asks each pleb directly whether it's actually arrived.
                while (state.pullRingIndex <= 2 && state.pullRingIndex <= state.pullDispatchRingIndex)
                {
                    int ring = state.pullRingIndex;
                    var held = RingHeldList(ref state, ring);

                    if (held.Count == 0)
                    {
                        // Whiffed selection (no candidates in range) — nothing to release, advance immediately.
                        state.pullRingIndex++;
                        continue;
                    }

                    if (!AllArrivedAtOrbitSlot(held)) break;

                    EndPullingRing(ref state);
                    state.pullRingIndex++;
                }

                if (state.pullRingIndex > 2) FinishPullPhase(ref state);
                break;
            }

            case KingOrbiterPhase.Orbit:
                if (_target != null) HandleMovement(ref state, delta);
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) EnterKnockbackWindup(ref state);
                break;

            case KingOrbiterPhase.KnockbackWindup:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) FireKnockback(ref state);
                break;

            // Same continuous-dispatch/independent-release shape as Pulling above (2026-08-18 rework —
            // used to wait for a ring's own plebs to fully finish their flight before even picking the
            // next ring's target), just LaunchAlong instead of OrbitTarget — shoot order now matches pull
            // order exactly (inner -> middle -> outer, via ShootPhysicalRing, 2026-08-20). phaseTimer counts up
            // continuously across the WHOLE Shooting Phase now. DISPATCH (shootDispatchRingIndex/
            // shootRingLocalIndex/shootSequenceIndex) advances into the next ring — picking its target,
            // AdvanceShootDispatchRing — the instant the current ring's own last pleb is launched, no gap.
            // RELEASE (shootRingIndex) stays fully independent per ring, same as Pull's own release loop —
            // see EndShotRing/AdvanceShootDispatchRing/ShootRingGlobalStart for the exact mechanics.
            case KingOrbiterPhase.ShootFlight:
            {
                // Scaled by EffectiveAttackSpeed (2026-08-31, was plain delta) — same reasoning as Pulling's
                // own identical change just above.
                state.phaseTimer += delta * EffectiveAttackSpeed;

                // Updates every currently-open slot's own tracked bearing BEFORE dispatch below can consume
                // it this same tick — see UpdateShootBearingTracking's own doc comment.
                UpdateShootBearingTracking(ref state, delta);

                while (state.shootDispatchRingIndex <= 2)
                {
                    int physicalDispatchRing = ShootPhysicalRing(state.shootDispatchRingIndex);
                    int dispatchCount = RingHeldList(ref state, physicalDispatchRing).Count;

                    while (state.shootRingLocalIndex < dispatchCount &&
                           state.phaseTimer >= state.shootSequenceIndex * state.scaledShootSequentialDelay + shotWindupDuration)
                    {
                        FireShotRing(ref state, physicalDispatchRing, state.shootRingLocalIndex);
                        state.shootRingLocalIndex++;
                        state.shootSequenceIndex++;
                    }

                    if (state.shootRingLocalIndex < dispatchCount) break; // this ring still has plebs waiting their own turn
                    if (!AdvanceShootDispatchRing(ref state)) break;      // ring 2 (inner, last in shoot order) just finished too
                }

                // Gated on AllFinishedLaunching (2026-08-31, replaced a shootingRange/shotSpeed flight-time
                // ESTIMATE) — see AllArrivedAtOrbitSlot's own doc comment (Pulling's identical fix, just
                // above) for why: asks each launched pleb directly whether it's actually finished traveling
                // instead of predicting when it should have.
                //
                // ALSO requires dispatchDoneForThisRing (2026-08-31 fix — the "ShootFlight instantly drops
                // every held pleb instead of actually firing them" bug, confirmed live) — unlike
                // AllArrivedAtOrbitSlot's IsWaitingToOrbit (a genuinely distinct enum value a not-yet-
                // dispatched pleb can't be in), IsLaunching/launchActive is FALSE both before a pleb has ever
                // been fired AND after it's finished flying — LaunchAlong's own doc comment confirms this
                // (orbitPhase stays Orbiting/untouched for the whole launch, and launchActive just flips back
                // false once travel completes). So the instant a ring is first selected for dispatch
                // (shootRingIndex <= shootDispatchRingIndex goes true), AllFinishedLaunching alone already
                // read "true" — nobody's launching YET, which looks identical to "everybody already
                // finished" — and released the whole ring before FireShotRing ever ran on it. This closes
                // that gap explicitly: a ring already behind the current dispatch ring (shootOrderIdx <
                // shootDispatchRingIndex) is confirmed fully dispatched by AdvanceShootDispatchRing's own
                // advance condition; the CURRENT dispatch ring only counts once its own local dispatch count
                // has actually caught up to its held count.
                while (state.shootRingIndex <= 2 && state.shootRingIndex <= state.shootDispatchRingIndex)
                {
                    int shootOrderIdx = state.shootRingIndex;
                    int physicalRing = ShootPhysicalRing(shootOrderIdx);
                    var held = RingHeldList(ref state, physicalRing);

                    if (held.Count == 0)
                    {
                        // Whiffed/aborted/never-held ring — nothing to release, advance immediately.
                        state.shootRingIndex++;
                        continue;
                    }

                    bool dispatchDoneForThisRing = shootOrderIdx < state.shootDispatchRingIndex
                        || state.shootRingLocalIndex >= held.Count;
                    if (!dispatchDoneForThisRing) break;

                    if (!AllFinishedLaunching(held)) break;

                    EndShotRing(ref state);
                    state.shootRingIndex++;
                }

                if (state.shootRingIndex > 2) FinishShootPhase(ref state);
                break;
            }
        }

        // Per explicit design: only Moving and Orbit are stunnable; only Orbit is interruptible (see
        // CanBeInterrupted below). Kinematic whenever not one of those two actively-self-moving phases —
        // same "kinematic whenever not actively moving under its own power" convention as KingSpikePD (a
        // stationary boss during any windup/frozen phase is a solid obstacle, not non-interacting).
        phase = GetPhase(ref state);
        isStunnable = (phase == KingOrbiterPhase.Moving || phase == KingOrbiterPhase.Orbit) && !stunResistant;

        if (_rigidbody != null)
        {
            bool shouldBeKinematic = phase != KingOrbiterPhase.Moving && phase != KingOrbiterPhase.Orbit;
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }
    }

    // ── Pull ─────────────────────────────────────────────────────────────────

    // Begins the WHOLE Pull Phase (called once, from HandleActiveState's Moving case only — 2026-08-18, used
    // to also be called recursively per-ring; ring-to-ring advancement now lives entirely in
    // AdvanceDispatchRing instead). Selects ring 0 (inner)'s candidates immediately (matches
    // OrbiterGloomPD.TryStartGrab's own "lock in at Pulling entry, not deferred to each pleb's own
    // individual windup" design — invulnerability needs to be live for the whole sequence, and a targeted
    // pleb shouldn't keep wandering while waiting for its own turn) — an empty inner ring is handled for
    // free by the Pulling case's own dispatch loop cascading straight through it, same as any other empty
    // ring encountered mid-sequence, no special case needed here.
    private void EnterPullWindup(ref State state)
    {
        state.phaseId    = (int)KingOrbiterPhase.Pulling;
        state.phaseTimer = 0f;
        state.pullSequenceIndex     = 0;
        state.pullDispatchRingIndex = 0;
        state.pullRingLocalIndex    = 0;
        state.pullRingIndex         = 0;
        TryGrabRing(ref state, 0);
    }

    // Called inline from the Pulling case's dispatch loop the instant the CURRENT ring
    // (state.pullDispatchRingIndex) has had every one of its own plebs dispatched — advances to the next
    // ring and immediately selects its candidates (TryGrabRing), so THAT ring's own first dispatch can still
    // land right on the very next scheduled slot, no gap at all (see the Pulling case's own doc comment for
    // why this "just in time" selection, rather than selecting every ring upfront, matters: selecting far in
    // advance would freeze/untarget outer-ring plebs many seconds before they're actually touched). An empty
    // ring (TryGrabRing finds nothing) isn't specially handled here — the dispatch loop's own inner condition
    // just evaluates to "nothing to dispatch" and the outer loop calls back in immediately, cascading through
    // any number of consecutive empty rings within the same tick. Returns false once ring 2 (outer) is also
    // fully dispatched — nothing left to advance into.
    private bool AdvanceDispatchRing(ref State state)
    {
        state.pullDispatchRingIndex++;
        state.pullRingLocalIndex = 0;
        if (state.pullDispatchRingIndex > 2) return false;
        TryGrabRing(ref state, state.pullDispatchRingIndex);
        return true;
    }

    // Global dispatch index the given ring's own FIRST pleb occupies — the sum of every earlier ring's own
    // held count. Rings dispatch strictly in order (inner, then middle, then outer), so this is always
    // accurate for any ring dispatch has already reached (see the Pulling case's own
    // "pullRingIndex <= pullDispatchRingIndex" guard, which is what guarantees rings 0..ring-1 have already
    // been selected/populated by the time this is called). Only used by UpdatePullLineIndicators now to
    // time each slot's own pre-pull windup-line display (2026-09-12 — release itself no longer estimates a
    // ring's own arrival time from this at all, see the release loop's own doc comment in
    // HandleActiveState's Pulling case for why).
    private int RingGlobalStart(ref State state, int ring)
    {
        int start = 0;
        for (int r = 0; r < ring; r++)
            start += RingHeldList(ref state, r).Count;
        return start;
    }

    // Exactly OrbiterGloomPD.TryStartGrab's body, generalized per-ring/per-multiplier. IsBeingHeld already
    // excludes plebs claimed by this same boss's own earlier rings (ClaimForOrbit flips it immediately), so
    // no extra cross-ring bookkeeping is needed here.
    private bool TryGrabRing(ref State state, int ring)
    {
        ref var held = ref RingHeldList(ref state, ring);
        held.Clear();
        if (EnemySpawnerPD.instance == null) return false;

        _grabCandidates.Clear();
        foreach (var enemy in EnemySpawnerPD.instance.ActiveEnemies)
        {
            if (enemy.SpawnCategory != EnemySpawnCategory.Pleb) continue;
            if (enemy._isDead || enemy.IsBeingHeld) continue;
            // Excludes a freshly-spawned-this-tick pleb whose own first Simulate() tick hasn't run yet —
            // see IEnemyPD.HasInitialized's own doc comment for the exact race this closes (a real,
            // confirmed-live bug on OrbiterGloomPD: grabbing one before that tick runs let its own
            // fresh-spawn reset silently undo the grab's invincibility/hurtbox-disable). Just means this
            // particular pleb isn't eligible for THIS ring's scan — harmless, it'll qualify normally the
            // next time any ring scans for candidates.
            if (!enemy.HasInitialized) continue;
            if (Vector3.Distance(transform.position, enemy.transform.position) > grabRadius) continue;
            _grabCandidates.Add(enemy);
        }
        if (_grabCandidates.Count == 0) return false;

        // Furthest-within-range first, ObjectId tiebreaker — same determinism reasoning as
        // OrbiterGloomPD.TryStartGrab's own identical sort.
        Vector3 origin = transform.position;
        _grabCandidates.Sort((a, b) =>
        {
            float distA = Vector3.Distance(origin, a.transform.position);
            float distB = Vector3.Distance(origin, b.transform.position);
            int cmp = distB.CompareTo(distA);
            return cmp != 0 ? cmp : a.ObjectId.instanceId.value.CompareTo(b.ObjectId.instanceId.value);
        });

        int grabCount = Mathf.Min(RingCount(ref state, ring), _grabCandidates.Count);
        for (int i = 0; i < grabCount; i++)
        {
            var chosen = _grabCandidates[i];
            held.Add(chosen.ObjectId);
            chosen.SetKinematic(true);
            chosen.SetMovementDisabled(true);
            chosen.SetPhysicsColliderDisabled(true);
            chosen.SetHurtboxDisabled(true);
            // Initial damage override set here is immediately superseded by RefreshHeldDamageOverrides
            // (2026-09-16, called every Simulate tick) — that continuously-self-correcting refresh is the
            // real source of truth for this pleb's own damage from here on; this first set just avoids a
            // one-tick gap between ClaimForOrbit below and RefreshHeldDamageOverrides' own next run. See that
            // method's own doc comment for why this couldn't just stay a one-time set-and-forget here.
            // EnrageDamageMultiplier multiplied in explicitly (2026-08-19) — SetDamageOverride hands the
            // held pleb a raw damage value computed straight off this boss's own `damage` field, entirely
            // bypassing this boss's own GetOwnDamage()/GetDamage(), so it doesn't pick the bonus up for free.
            chosen.SetDamageOverride(true, damage * pullDamageMultiplier * EnrageDamageMultiplier);
            chosen.ClaimForOrbit(ObjectId);
        }
        return true;
    }

    // Fires the actual pull motion for exactly held index i of the given ring (kinematic/invulnerable/etc.
    // already applied back in TryGrabRing) — called by Pulling's own case handler in HandleActiveState the
    // instant that pleb's windup window closes (see its own comment for the full timing model), matches
    // OrbiterGloomPD.DispatchPull's own identical shape, per-ring instead of a single group. ring is passed
    // explicitly now (2026-08-18, used to read state.pullRingIndex directly) — dispatch and release now
    // track separate ring indices (pullDispatchRingIndex vs. pullRingIndex), so this needs the CALLER's own
    // dispatch-side ring, not whichever ring release happens to be waiting on. Order within the ring doesn't
    // matter (per explicit design); the index just walks the ring's held list in whatever order TryGrabRing
    // already sorted it into (furthest-first).
    private void BeginPull(ref State state, int ring, int i)
    {
        var held  = RingHeldList(ref state, ring);
        int count = held.Count;

        var go = hierarchy.GetGameObject(held[i]);
        if (go != null && go.TryGetComponent(out IEnemyPD enemy) && !enemy._isDead)
            enemy.OrbitTarget(ObjectId, transform.position, RingRadius(ring), pullSpeed, RingAngularSpeed(ring), SlotAngleDeg(i, count));
    }

    // Direct-state gate (2026-08-31) for HandleActiveState's own Pulling-case release loop — a ring only
    // releases once every currently-held member has genuinely reached IsWaitingToOrbit (its own Pulling-
    // phase MoveTowards fully arrived at its slot), queried directly rather than estimated from a parallel
    // time formula. A dead member can never reach WaitingToOrbit on its own (LateSimulate's own isDead/
    // wiped early-return means it simply stops moving toward its slot), so it's treated as non-blocking
    // instead of stalling the ring's release forever — same reasoning a missing/unresolvable GameObject
    // gets.
    private bool AllArrivedAtOrbitSlot(DisposableList<PredictedObjectID> held)
    {
        for (int i = 0; i < held.Count; i++)
        {
            var go = hierarchy.GetGameObject(held[i]);
            if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) continue;
            if (!enemy.IsWaitingToOrbit) return false;
        }
        return true;
    }

    // Called once every held pleb in ring state.pullRingIndex has been dispatched (BeginPull) AND has
    // actually arrived at its own orbit slot (see AllArrivedAtOrbitSlot above) — pull -> orbit damage
    // multiplier switch, mirrors OrbiterGloomPD's own Pulling->Holding transition. Caller (HandleActiveState's
    // Pulling case) advances state.pullRingIndex itself afterward — this no longer does that, or the old
    // "advance to next ring/finish" tail, itself
    // (2026-08-18, see AdvanceDispatchRing/FinishPullPhase for where those now live) — dispatch and release
    // advance completely independently now, so there's no single shared "did we just finish the LAST ring"
    // question this method could answer on its own anymore.
    private void EndPullingRing(ref State state)
    {
        int ring = state.pullRingIndex;
        // No longer sets the orbit damage override here (2026-09-16, was ApplyDamageOverrideToRing(...,
        // orbitDamageMultiplier, ...)) — RefreshHeldDamageOverrides already switched this ring's own members
        // over the instant EACH ONE individually finished pulling (reached WaitingToOrbit), which is now
        // strictly earlier than this ring-wide release point. See RefreshHeldDamageOverrides' own doc comment
        // for why: a stationary, already-arrived-but-still-waiting pleb shouldn't keep dealing elevated pull
        // damage just because its ring-mates haven't finished yet.

        // Releases every held pleb in THIS ring into actually orbiting (BeginOrbiting) — each one has been
        // frozen at its own slot (WaitingToOrbit) since finishing its own staggered pull-in; releasing them
        // all in this SAME tick is what keeps their angular spacing exactly even instead of drifting (see
        // EnemyOrbitPhase's own doc comment). Only this ring's own plebs — inner/middle/outer each get
        // released independently, on their own ring's own schedule, not gated behind the other two rings.
        var held = RingHeldList(ref state, ring);
        for (int i = 0; i < held.Count; i++)
        {
            var go = hierarchy.GetGameObject(held[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy))
                enemy.BeginOrbiting();
        }
    }

    // Called once the release loop in HandleActiveState's Pulling case confirms every ring (0..2) has been
    // released (or skipped, if whiffed) — runs the total-held safety check (2026-08-11 gap fix): an
    // entirely-whiffed pull (e.g. a near-empty field) must NOT enter Orbit holding nothing; falls back to
    // Moving at FULL cooldown instead of an immediate retry.
    private void FinishPullPhase(ref State state)
    {
        // The Pull Phase's own continuous invincibility window (see HandleActiveState's Moving case for
        // where it began) ends here, regardless of which branch below this falls into.
        SetHurtboxDisabled(false);

        int totalHeld = state.innerHeld.Count + state.middleHeld.Count + state.outerHeld.Count;
        if (totalHeld <= 0)
        {
            state.phaseId             = (int)KingOrbiterPhase.Moving;
            state.attackCooldownTimer = state.scaledAttackCooldown;
        }
        else
        {
            state.phaseId    = (int)KingOrbiterPhase.Orbit;
            state.phaseTimer = orbitDuration;
        }
    }

    // ── Knockback ────────────────────────────────────────────────────────────

    // Freezes every currently-held ring in place (0 angular speed) without dropping anything —
    // orbitDamageMultiplier stays applied, untouched, per explicit design.
    private void EnterKnockbackWindup(ref State state)
    {
        SetHeldRingsAngularSpeed(ref state, 0f);
        state.phaseId    = (int)KingOrbiterPhase.KnockbackWindup;
        state.phaseTimer = knockbackChargeTime;
    }

    private void SetHeldRingsAngularSpeed(ref State state, float speed)
    {
        ApplyAngularSpeed(state.innerHeld, speed);
        ApplyAngularSpeed(state.middleHeld, speed);
        ApplyAngularSpeed(state.outerHeld, speed);
    }

    private void ApplyAngularSpeed(DisposableList<PredictedObjectID> list, float speed)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var go = hierarchy.GetGameObject(list[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy))
                enemy.SetOrbitAngularSpeed(speed);
        }
    }

    // Radial burst — same shape as KingSpikePD.FireSpikeBurst, but knockback instead of damage. No damage
    // dealt by this attack at all.
    //
    // Deliberately does NOT skip IsInvincible (unlike FireSpikeBurst's own damage pass) — a dashing player is
    // IsInvincible via CharacterStatsPD's committed-invincibility channel, and reaching through that to yank
    // them out of the dash is the entire reason dashes were made interruptible in the first place.
    // PlayerControllerPD.Knockback() already calls Interrupt() before applying velocity/stun, which cancels
    // exactly that committed grant. Side effect: a player invincible for a DIFFERENT reason (Death
    // Prevention, Revive grace, ShieldBreakBurst, etc.) also isn't excluded here — Interrupt() only ever
    // touches the committed-action channel, so those other sources stay untouched/unrevoked, but the
    // knockback velocity/stun itself still lands on them regardless of IsInvincible.
    // See SpearGloomPD._stabOverlapResults's own doc comment for the buffer-size reasoning.
    private static readonly Collider[] _knockbackOverlapResults = new Collider[8];

    private void FireKnockback(ref State state)
    {
        Vector3 origin = transform.position;
        // Physics.OverlapSphereNonAlloc against CombatLayerMasks.FriendlyHurtboxMask (2026-08-30, was a
        // manual FriendlyTargetRegistryPD distance check) — see SpearGloomPD.EnterStab's own doc comment for
        // why enemy AoE attacks now match every player weapon's own collider-query convention.
        int n = Physics.OverlapSphereNonAlloc(origin, knockbackRadius, _knockbackOverlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _knockbackOverlapResults[i].GetComponent<IFriendlyPD>();
            if (stats == null || stats.IsDead) continue;
            Vector3 toPlayer = stats.transform.position - origin;

            // AddKnockbackVelocity (2026-08-30, was GetComponent<PlayerControllerPD>()+Knockback()) —
            // redundant now that stats is IFriendlyPD: CharacterStatsPD.AddKnockbackVelocity already
            // forwards to this exact _controller?.Knockback(impulse) call, so this reaches a future non-
            // player ally too instead of silently no-oping via the null controller check.
            Vector3 dir = toPlayer.sqrMagnitude > 0.0001f ? toPlayer.normalized : Vector3.forward;
            stats.AddKnockbackVelocity(dir * knockbackForce);
        }

        EnterShootWindupOrAdvance(ref state);
    }

    // ── Shooting ─────────────────────────────────────────────────────────────

    // Begins the WHOLE Shooting Phase (called once, from FireKnockback only — 2026-08-18, used to also be
    // called recursively per-ring; ring-to-ring advancement now lives entirely in AdvanceShootDispatchRing
    // instead, same split as Pull's EnterPullWindup/AdvanceDispatchRing). Picks ring 0 (outer, shoot order)'s
    // target immediately via the same -1-then-advance trick AdvanceShootDispatchRing already needs anyway
    // for its own "no valid targets"/"empty ring" handling — no separate special-case logic needed here for
    // ring 0 specifically.
    private void EnterShootWindupOrAdvance(ref State state)
    {
        state.phaseId    = (int)KingOrbiterPhase.ShootFlight;
        state.phaseTimer = 0f;
        state.shootSequenceIndex     = 0;
        state.shootRingLocalIndex    = 0;
        state.shootRingIndex         = 0;
        state.shootDispatchRingIndex = -1;
        state.shootNextGlobalStart   = 0;
        AdvanceShootDispatchRing(ref state);
    }

    // Called inline from the ShootFlight case's dispatch loop the instant the CURRENT ring
    // (state.shootDispatchRingIndex) has had every one of its own held plebs launched — advances to the
    // next ring (shoot order: outer -> middle -> inner) and immediately picks its shared target, so THAT
    // ring's own first launch can still land right on the very next scheduled slot, no gap. An empty ring
    // (nothing held — e.g. its own Pull Phase found nobody) isn't specially handled here — the dispatch
    // loop's own inner condition just evaluates to "nothing to dispatch" and the outer loop calls back in
    // immediately, cascading through any number of consecutive empty rings within the same tick, same as
    // Pull's AdvanceDispatchRing. Returns false once ring 2 (inner, last in shoot order) is also fully
    // dispatched.
    //
    // No longer aborts when no valid targets exist (2026-08-22, was: release every undispatched ring and
    // skip the rest of the whole Shooting Phase) — see PickShotTarget's own doc comment for the fallback
    // this was replaced with instead.
    private bool AdvanceShootDispatchRing(ref State state)
    {
        state.shootDispatchRingIndex++;
        state.shootRingLocalIndex = 0;
        if (state.shootDispatchRingIndex > 2) return false;

        int ring = ShootPhysicalRing(state.shootDispatchRingIndex);
        int heldCount = RingHeldList(ref state, ring).Count;
        // Snapshot THIS ring's own global-start index from the running tally, then advance the tally by this
        // ring's own count — see shootRingGlobalStart0/1/2's own doc comment for why this can never be
        // re-derived later from other rings' live held-list counts.
        SetShootRingGlobalStart(ref state, state.shootDispatchRingIndex, state.shootNextGlobalStart);
        state.shootNextGlobalStart += heldCount;
        // Resets this ring's own slot range to Vector3.zero (2026-08-21) — clears whatever the PREVIOUS ring
        // left in these same physical slot indices, so this ring's own plebs seed their tracked bearing
        // fresh from live direction the moment their own window opens — see UpdateShootBearingTracking's own
        // doc comment. No more per-slot reset needed here (2026-09-14, was: zero out shootBearingDir/despawn
        // shootIndicatorId for this ring's own reused slot range) — state.shootTrackedPleb/etc. are now a
        // flat list keyed by PLEB identity, not ring-local slot position, so a new ring's own plebs simply
        // get their own brand-new entries (UpdateShootBearingTracking's own Add() path) with nothing shared
        // to clear; an earlier ring's still-in-flight plebs keep their own entries completely untouched by
        // this ring advancing.
        if (heldCount == 0) return true;

        // No abort here anymore (2026-08-22) — see PickShotTarget's own doc comment for what happens when
        // there are no valid candidates: leaves _shotTargetSnapshot null rather than picking anything, and
        // UpdateShootBearingTracking falls back to lastKnownTargetPosition instead of the whole ring just
        // sitting frozen/never dispatching. Nothing left to release/skip here — every ring proceeds
        // regardless of whether a real target exists right now.
        PickShotTarget(ref state);
        // Seeds this newly-opened ring's own bearing + indicator pose immediately (delta 0 — RotateTowards'
        // maxRadiansDelta 0 is a no-op against an already-zeroed bearing, which just takes the instant-seed
        // branch instead) rather than waiting for next tick's regular UpdateShootBearingTracking call — see
        // its own doc comment. Without this, a slot's indicator would activate one tick before its pose is
        // ever written, showing whatever stale pose this pooled object was left at by its LAST use.
        UpdateShootBearingTracking(ref state, 0f);
        // EnrageDamageMultiplier multiplied in explicitly — see TryGrabRing's own identical comment.
        ApplyDamageOverrideToRing(ref state, ring, damage * shootingDamageMultiplier * EnrageDamageMultiplier);
        return true;
    }

    // O(n) lookup by pleb identity into state.shootTrackedPleb — n is bounded by however many plebs are
    // CURRENTLY winding-up-or-flying at once (at most innerCount+middleCount+outerCount, a small number),
    // so a linear scan here is cheap; no dictionary needed for a list this size. Returns -1 if this pleb has
    // no currently-open entry (never tracked yet, or already removed).
    private static int FindTrackedShootIndex(ref State state, PredictedObjectID pebId)
    {
        for (int i = 0; i < state.shootTrackedPleb.Count; i++)
            if (state.shootTrackedPleb[i].Equals(pebId)) return i;
        return -1;
    }

    // Swap-with-last removal across all 5 parallel shootTracked* lists — order is never meaningful here (see
    // State.shootTrackedPleb's own doc comment), so this is O(1) per removal instead of shifting every later
    // entry down.
    private static void RemoveTrackedShootEntryAt(ref State state, int i)
    {
        int last = state.shootTrackedPleb.Count - 1;
        state.shootTrackedPleb[i]        = state.shootTrackedPleb[last];
        state.shootTrackedBearing[i]     = state.shootTrackedBearing[last];
        state.shootTrackedIndicator[i]   = state.shootTrackedIndicator[last];
        state.shootTrackedFired[i]       = state.shootTrackedFired[last];
        state.shootTrackedWindupStart[i] = state.shootTrackedWindupStart[last];
        state.shootTrackedPleb.RemoveAt(last);
        state.shootTrackedBearing.RemoveAt(last);
        state.shootTrackedIndicator.RemoveAt(last);
        state.shootTrackedFired.RemoveAt(last);
        state.shootTrackedWindupStart.RemoveAt(last);
    }

    // Removes any tracked entry whose own pleb has genuinely finished (fired AND its own flight is over —
    // enemy.IsLaunching false) or become invalid (dead/unresolvable) — 2026-09-14, per explicit design: a
    // pleb's own line indicator must stay visible for its ENTIRE movement, only disappearing once THAT
    // SPECIFIC pleb is actually done, fully independent of every other pleb (including ones from a different
    // ring active at the same time). Called every ShootFlight tick, before any new entries can open this same
    // tick (see UpdateShootBearingTracking's own call site) — not gated on shootDispatchRingIndex at all,
    // since a still-in-flight pleb from an EARLIER, already-fully-dispatched ring must keep being checked
    // regardless of how far dispatch has moved on.
    private void DespawnFinishedShootIndicators(ref State state)
    {
        for (int i = state.shootTrackedPleb.Count - 1; i >= 0; i--)
        {
            var go = hierarchy.GetGameObject(state.shootTrackedPleb[i]);
            bool finished = go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead
                || (state.shootTrackedFired[i] && !enemy.IsLaunching);
            if (!finished) continue;

            DespawnLineIndicator(state.shootTrackedIndicator[i]);
            RemoveTrackedShootEntryAt(ref state, i);
        }
    }

    // Opens a tracked entry (state.shootTrackedPleb/etc.) the instant a pleb's own windup window opens, then
    // rate-caps its tracked bearing every subsequent tick via Vector3.RotateTowards at
    // rotationRateDegPerSecond (converted to radians/sec — RotateTowards' maxRadiansDelta param), until that
    // pleb actually fires (shootTrackedFired[i] true — see FireShotRing), at which point its aim freezes for
    // good. Called once per tick, before dispatch itself, from HandleActiveState's ShootFlight case
    // (2026-08-21). This is what makes circling this boss (or timing a dash to arrive late in a pleb's own
    // windup) a real counterplay option — an unbounded instant snap (the old behavior, computed fresh at the
    // literal instant of launch) meant no amount of player movement could ever throw off where a shot
    // actually fired. Both FireShotRing (the real launch) and the spawned LineIndicatorPD's own SetAim
    // (called right below, same tick) read this SAME tracked value, so what's shown during the windup is
    // always exactly what fires — never a cosmetic-only lag the real shot ignores.
    //
    // 2026-09-14 rework — only scans the CURRENT dispatch ring's still-winding-up plebs to open/advance
    // entries (same window as before), but entries themselves now live in the flat shootTracked* lists (see
    // their own doc comment) instead of a ring-local-indexed array, so an entry opened here survives fine
    // even once dispatch moves on to a LATER ring — DespawnFinishedShootIndicators (called first, above) is
    // what actually ages entries out, independently of ring/dispatch progress.
    private void UpdateShootBearingTracking(ref State state, float delta)
    {
        DespawnFinishedShootIndicators(ref state);

        if (state.shootDispatchRingIndex > 2) return; // dispatch already fully done, nothing new can open

        // Re-tries every tick while there's no real target locked in — see PickShotTarget's own doc comment.
        // The instant any player becomes valid again, this ring immediately acquires them instead of staying
        // on lastKnownTargetPosition until its next scheduled PickShotTarget call (which might not come
        // until a future ring, well after this one's already finished).
        if (_shotTargetSnapshot == null && CharacterStatsPD.AnyValidTargetExists)
            PickShotTarget(ref state);

        // _shotTargetLastKnownPosition only refreshes while _shotTargetSnapshot is actually alive+visible —
        // frozen otherwise (dead/invisible), so a shot already committed to this target keeps rate-cap-
        // rotating toward wherever they were last legitimately seen rather than either getting stuck
        // mid-rotation or following a dead player's transform to wherever it relocates post-death (see
        // _shotTargetSnapshot's own doc comment). Falls back to the boss's own generic lastKnownTargetPosition
        // only when there's no assigned target AT ALL (see PickShotTarget's own doc comment) — the two
        // fallbacks serve different gaps: this one covers "had a target, they died/vanished mid-ring", that
        // one covers "never had anyone to target in the first place".
        if (_shotTargetSnapshot != null && !_shotTargetSnapshot.IsDead &&
            (!_shotTargetSnapshot.IsInvisible || _shotTargetSnapshot.IsTaunting))
            _shotTargetLastKnownPosition = _shotTargetSnapshot.transform.position;

        Vector3 targetPos = _shotTargetSnapshot != null ? _shotTargetLastKnownPosition : lastKnownTargetPosition;

        int physicalDispatchRing = ShootPhysicalRing(state.shootDispatchRingIndex);
        var held = RingHeldList(ref state, physicalDispatchRing);
        int dispatchCount = held.Count;
        int ringGlobalStart = ShootRingGlobalStart(ref state, state.shootDispatchRingIndex);

        for (int i = state.shootRingLocalIndex; i < dispatchCount; i++)
        {
            float localElapsed = state.phaseTimer - (ringGlobalStart + i) * state.scaledShootSequentialDelay;
            if (localElapsed < 0f || localElapsed >= shotWindupDuration) continue; // not this pleb's window yet, or already past it (about to dispatch this same tick)

            var pebId = held[i];
            var go = hierarchy.GetGameObject(pebId);
            if (go == null) continue;

            int idx = FindTrackedShootIndex(ref state, pebId);
            if (idx < 0)
            {
                // Freshly opened window — append a new entry. windupStart derived from localElapsed so this
                // works correctly even if this tick's delta jumped partway INTO the window rather than
                // landing exactly on its opening instant.
                idx = state.shootTrackedPleb.Count;
                state.shootTrackedPleb.Add(pebId);
                state.shootTrackedBearing.Add(Vector3.zero);
                state.shootTrackedIndicator.Add(default);
                state.shootTrackedFired.Add(false);
                state.shootTrackedWindupStart.Add(state.phaseTimer - localElapsed);
            }

            if (state.shootTrackedFired[idx]) continue; // already fired — aim frozen, lifetime owned by DespawnFinishedShootIndicators

            // Plain live read (2026-08-22 — dropped the per-slot position cache this used to need) —
            // EnemyBasePD.LateSimulate now skips recomputing a held pleb's position entirely once its orbit
            // angular speed hits 0, so this read is already stable without any caching here, same shape as
            // UpdatePersistentBearingTracking's own equivalent read.
            Vector3 toTarget = targetPos - go.transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) continue;
            Vector3 targetDir = toTarget.normalized;

            Vector3 current = state.shootTrackedBearing[idx];
            state.shootTrackedBearing[idx] = current.sqrMagnitude < 0.0001f
                ? targetDir
                // Rate scaled by EffectiveAttackSpeed (2026-08-31, was plain delta) — matches every other
                // tracked-windup enemy's own rate-capped tracking (KingSpikePD/BlitzGloomPD/LeaperGloomPD/
                // MortarHazardPD all got the identical change same pass). Note: unlike those, this boss's own
                // phaseTimer itself does NOT read EffectiveAttackSpeed anywhere (confirmed — King Orbiter has
                // no per-difficulty windup-speed exposure at all), so this specifically restores Slow-
                // consistency for the tracking (a Slow now correctly stiffens re-aim here too, same as
                // everywhere else) rather than "keeping pace with an already-compressed windup" the way it
                // does for the other bosses.
                : Vector3.RotateTowards(current, targetDir, state.scaledRotationRateDegPerSecond * Mathf.Deg2Rad * delta * EffectiveAttackSpeed, 0f);

            // Spawns (first tick this entry exists) or resolves this pleb's own LineIndicatorPD and writes its
            // aim directly here, in Simulate — once per tick, off the same tick-fresh bearing FireShotRing
            // itself reads (2026-08-22 fix, see ResolveOrSpawnLineIndicator's own doc comment for why this
            // needs a real spawned object rather than a pooled Instantiate() clone).
            var indicator = ResolveOrSpawnLineIndicator(state.shootTrackedIndicator[idx], go.transform.position,
                state.shootTrackedBearing[idx], shootingRange, out var resolvedId);
            state.shootTrackedIndicator[idx] = resolvedId;
            indicator?.SetAim(state.shootTrackedBearing[idx]);
        }
    }

    // Global dispatch index the given shoot-order ring's own FIRST pleb occupies. Reads the snapshot taken
    // once in AdvanceShootDispatchRing (shootRingGlobalStart0/1/2) — NOT re-summed from other rings' live
    // held-list counts (2026-08-20 fix, see those fields' own doc comment for the bug that caused: once an
    // EARLIER ring released and cleared its own held list, re-summing here would silently undercount it,
    // corrupting every LATER ring's own release-timing math into releasing early — sometimes before that
    // ring had even finished firing).
    private int ShootRingGlobalStart(ref State state, int shootOrderIndex) => shootOrderIndex switch
    {
        0 => state.shootRingGlobalStart0,
        1 => state.shootRingGlobalStart1,
        2 => state.shootRingGlobalStart2,
        _ => 0,
    };

    private static void SetShootRingGlobalStart(ref State state, int shootOrderIndex, int value)
    {
        switch (shootOrderIndex)
        {
            case 0: state.shootRingGlobalStart0 = value; break;
            case 1: state.shootRingGlobalStart1 = value; break;
            case 2: state.shootRingGlobalStart2 = value; break;
        }
    }

    // Called once the release loop in HandleActiveState's ShootFlight case confirms every ring (0..2, shoot
    // order) has been released (or skipped/aborted) — full attack cycle complete.
    private void FinishShootPhase(ref State state)
    {
        state.attackCooldownTimer = state.scaledAttackCooldown;
        FindRandomTarget();
        state.phaseId = (int)KingOrbiterPhase.Moving;

        // Was only ever cleared on a full respawn/death reset, never at the natural end of an ordinary
        // Shoot Phase — left a stale CharacterStatsPD reference sitting in this plain (non-State) field for
        // the rest of Moving/Pulling/Orbit/KnockbackWindup, until the NEXT Shoot Phase's own PickShotTarget
        // call overwrote it. Harmless on its own (UpdateShootBearingTracking's own null-check also requires
        // this to be set before it does anything), but a real gap if a FUTURE Shoot Phase ever whiffs on every
        // ring (no held plebs anywhere, so PickShotTarget never fires) — this would otherwise let that cycle
        // silently reuse whichever player was targeted several attack cycles ago instead of correctly having
        // no target.
        _shotTargetSnapshot = null;
    }

    // Picks one random currently-valid player, excluding state.lastShotTargetId when more than one candidate
    // qualifies ("unique from the last target if possible") — falls back to including it anyway if excluding
    // it would leave nobody. Stores into state.lastShotTargetId (for the NEXT ring's own exclusion) and the
    // plain _shotTargetSnapshot field UpdateView reads for indicator aim.
    //
    // No candidates at all (2026-08-22, no longer guarded by AnyValidTargetExists at the call site) leaves
    // _shotTargetSnapshot explicitly null rather than untouched — a stale reference from a PREVIOUS ring
    // would otherwise silently keep being read as if still valid. UpdateShootBearingTracking's own null
    // check then falls back to lastKnownTargetPosition (this boss's own EnemyBasePD-inherited "last position
    // any target was actually seen at") instead of the whole ring just sitting frozen with nothing to aim
    // at — and re-calls this method fresh every tick while still null, so the instant any player becomes
    // valid again, every currently-fallback-tracking slot picks them up and smoothly (rate-capped, same as
    // always) swings onto them instead of staying stuck on the fallback forever. Doesn't touch
    // state.lastShotTargetId here — nothing was actually picked, so there's nothing new to exclude next time.
    private void PickShotTarget(ref State state)
    {
        BuildShotCandidates(ref state, excludeLast: true);
        if (_shotTargetScratch.Count == 0) BuildShotCandidates(ref state, excludeLast: false);
        if (_shotTargetScratch.Count == 0)
        {
            _shotTargetSnapshot = null;
            return;
        }

        int idx = GameRandom.instance.Next(_shotTargetScratch.Count);
        _shotTargetSnapshot     = _shotTargetScratch[idx];
        state.lastShotTargetId  = _shotTargetIdScratch[idx];
    }

    private void BuildShotCandidates(ref State state, bool excludeLast)
    {
        _shotTargetScratch.Clear();
        _shotTargetIdScratch.Clear();
        foreach (var kvp in CharacterStatsPD.All)
        {
            var p = kvp.Value;
            // Taunt trumps invisible — same "invisible-but-taunting is still valid" rule as
            // EnemyBasePD.FindClosestTarget/FindRandomTarget.
            if (p.IsDead || (p.IsInvisible && !p.IsTaunting)) continue;
            if (excludeLast && kvp.Key.Equals(state.lastShotTargetId)) continue;
            _shotTargetScratch.Add(p);
            _shotTargetIdScratch.Add(kvp.Key);
        }
    }

    private void ApplyDamageOverrideToRing(ref State state, int ring, float dmg)
    {
        var list = RingHeldList(ref state, ring);
        for (int i = 0; i < list.Count; i++)
        {
            var go = hierarchy.GetGameObject(list[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy))
                enemy.SetDamageOverride(true, dmg);
        }
    }

    // Fires the launch for exactly held index i of the given ring, using its own tracked bearing
    // (state.shootTrackedBearing, looked up by pleb identity — see UpdateShootBearingTracking's own doc
    // comment) rather than a fresh live read (2026-08-21 rework) — this is what makes the rate cap actually
    // bite on the real shot, not just the windup indicator's own cosmetic aim. Falls back to a fresh live
    // direction only if the tracked bearing was somehow never seeded (shouldn't normally happen —
    // UpdateShootBearingTracking runs every tick this pleb's window is open, before dispatch can ever consume
    // it the same tick). Travels the full shootingRange regardless of what it hits along the way. Called by
    // ShootFlight's own case handler in HandleActiveState the instant that pleb's windup window closes (see
    // its own comment for the full timing model) — exactly BeginPull's own sequencing shape, just
    // LaunchAlong instead of OrbitTarget. ring is passed explicitly (2026-08-18, used to read
    // state.shootRingIndex directly) — dispatch and release now track separate ring indices
    // (shootDispatchRingIndex vs. shootRingIndex), so this needs the CALLER's own dispatch-side ring. Order
    // within the ring doesn't matter (per explicit design).
    //
    // No indicator despawn here anymore (2026-09-14, was unconditional-before-the-guard) — per explicit
    // design, this pleb's own indicator must stay visible for its ENTIRE movement, not disappear the instant
    // it's dispatched. Instead this just flags the tracked entry as fired (shootTrackedFired) — from that
    // point on UpdateShootBearingTracking stops touching its bearing (aim locked) and
    // DespawnFinishedShootIndicators (called every tick) removes it the instant enemy.IsLaunching actually
    // flips back false (flight complete) or the pleb becomes invalid — see that method's own doc comment.
    private void FireShotRing(ref State state, int ring, int i)
    {
        var list = RingHeldList(ref state, ring);
        var pebId = list[i];
        var go = hierarchy.GetGameObject(pebId);

        int idx = FindTrackedShootIndex(ref state, pebId);

        Vector3 dir;
        Vector3 tracked = idx >= 0 ? state.shootTrackedBearing[idx] : Vector3.zero;
        if (tracked.sqrMagnitude >= 0.0001f)
        {
            dir = tracked;
        }
        else
        {
            // Same _shotTargetLastKnownPosition/lastKnownTargetPosition fallback shape
            // UpdateShootBearingTracking uses, so this rare "never tracked" edge case doesn't follow a dead
            // target's transform or aim at the boss's own position when there's no real target.
            Vector3 targetPos = _shotTargetSnapshot != null ? _shotTargetLastKnownPosition : lastKnownTargetPosition;
            Vector3 toTarget = go != null ? targetPos - go.transform.position : Vector3.zero;
            toTarget.y = 0f;
            dir = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;
        }

        // Flag fired regardless of whether the launch below actually goes through (e.g. the pleb died this
        // same tick) — DespawnFinishedShootIndicators's own dead/unresolvable check already covers that case
        // unconditionally, independent of this flag.
        if (idx >= 0) state.shootTrackedFired[idx] = true;

        if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) return;

        // * EffectiveAttackSpeed (2026-08-31) — bakes this boss's CURRENT attack speed into the launch, once,
        // at the moment it actually fires (same "snapshot, not live" shape MortarHazardPD's own
        // _windupAttackSpeed uses, for the identical reason: LaunchAlong's own speed is a one-time bake, not
        // re-read live during flight) — a Slow active right as the shot launches now makes it travel slower
        // for its whole flight, matching "held enemies inherit their holder's attack speed" per explicit
        // design. Also what keeps ringReleaseTime's own shootingRange/shotSpeed flight-time estimate
        // (HandleActiveState's ShootFlight case) in sync with the real launch — that threshold is measured in
        // phaseTimer units, which now advance at this same EffectiveAttackSpeed rate, so both sides speed up
        // together.
        enemy.LaunchAlong(dir, shootingRange, shotSpeed * EffectiveAttackSpeed);
    }

    // Direct-state gate (2026-08-31), same shape/reasoning as AllArrivedAtOrbitSlot above (see its own doc
    // comment) — a ring only releases once every currently-held member has genuinely finished its own
    // LaunchAlong flight (IsLaunching false), not once "enough time should have passed" per a
    // shootingRange/shotSpeed estimate. Dead/unresolvable members are non-blocking, same reasoning.
    private bool AllFinishedLaunching(DisposableList<PredictedObjectID> held)
    {
        for (int i = 0; i < held.Count; i++)
        {
            var go = hierarchy.GetGameObject(held[i]);
            if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) continue;
            if (enemy.IsLaunching) return false;
        }
        return true;
    }

    // Called once every held pleb in ring state.shootRingIndex has been dispatched (FireShotRing) AND has
    // actually finished its own flight (see AllFinishedLaunching above) — full release, mirrors
    // OrbiterGloomPD.ReleaseAllHeld's per-pleb body. Caller (HandleActiveState's ShootFlight case) advances
    // state.shootRingIndex itself afterward — this no longer does that, or the old "advance to next
    // ring/finish" tail, itself (2026-08-18, see AdvanceShootDispatchRing/FinishShootPhase for where those
    // now live).
    private void EndShotRing(ref State state)
    {
        int ring = ShootPhysicalRing(state.shootRingIndex);
        ReleaseRing(ref state, ring);
    }

    // ── Release ──────────────────────────────────────────────────────────────

    private void ReleaseRing(ref State state, int ring)
    {
        ref var held = ref RingHeldList(ref state, ring);
        for (int i = 0; i < held.Count; i++)
        {
            var go = hierarchy.GetGameObject(held[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy))
            {
                enemy.StopOrbiting();
                enemy.SetKinematic(false);
                enemy.SetMovementDisabled(false);
                enemy.SetPhysicsColliderDisabled(false);
                enemy.SetHurtboxDisabled(false);
                enemy.SetDamageOverride(false);
            }
        }
        held.Clear();
    }

    private void ReleaseAllHeld(ref State state)
    {
        ReleaseRing(ref state, 0);
        ReleaseRing(ref state, 1);
        ReleaseRing(ref state, 2);
    }

    // "Immediately release" requires this to happen in the SAME call as B.isDead flipping true — see
    // OrbiterGloomPD.Die()'s own identical override/reasoning.
    protected override void Die()
    {
        base.Die();
        ReleaseAllHeld(ref currentState);
        ReleasePersistentHeld(ref currentState); // also despawns every persistent-volley indicator
        DespawnAllShootIndicators(ref currentState);
    }

    // Force-despawns every currently-spawned shoot indicator immediately on death, and clears all 5
    // shootTracked* lists (2026-09-14 — simpler now that they're a flat, non-ring-local list; the old
    // per-ring-slot-vs-array-index mismatch concern this method's doc comment used to describe no longer
    // applies at all, since there's no ring-local slot indexing left anywhere in this system).
    private void DespawnAllShootIndicators(ref State state)
    {
        for (int i = 0; i < state.shootTrackedIndicator.Count; i++)
            DespawnLineIndicator(state.shootTrackedIndicator[i]);
        state.shootTrackedPleb.Clear();
        state.shootTrackedBearing.Clear();
        state.shootTrackedIndicator.Clear();
        state.shootTrackedFired.Clear();
        state.shootTrackedWindupStart.Clear();
    }

    // Only Orbit is interruptible, per explicit design.
    protected override bool CanBeInterrupted => GetPhase(ref currentState) == KingOrbiterPhase.Orbit;

    // Unlike OrbiterGloomPD, interrupting this boss does NOT drop the held plebs — it just fast-forwards
    // straight into the Knockback Phase (same freeze-in-place entry as a natural Orbit timer expiry).
    // Persistent Pleb Shots are untouched by this — they run entirely independent of phaseId/interrupts.
    protected override void OnInterrupted() => EnterKnockbackWindup(ref currentState);

    // ── Persistent Pleb Shots ────────────────────────────────────────────────

    // Entirely independent of HandleActiveState's phaseId FSM — same shape as KingSpikePD's own persistent
    // Remote Spike Bursts. See persistentShotInterval's own doc comment.
    protected override void Simulate(ref State state, float delta)
    {
        base.Simulate(ref state, delta);

        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (B.isDead || B.wiped) return;

        UpdatePersistentPlebShots(ref state, delta);
        RefreshHeldDamageOverrides(ref state);
    }

    // Keeps every currently-held pleb's own contact-damage override correctly matched to its LIVE orbitPhase,
    // every tick, regardless of which King Orbiter phase is currently active (Pull/Shoot/Knockback/etc. — held
    // plebs persist across all of them once captured, this isn't scoped to Pulling only). 2026-09-16 fix —
    // per explicit design: a pleb should STOP dealing pullDamageMultiplier's elevated contact damage the
    // INSTANT its own pull movement ends (reaches WaitingToOrbit), even before the rest of its ring has also
    // arrived and the ring-wide EndPullingRing/BeginOrbiting release actually fires. A stationary,
    // already-arrived-but-still-waiting pleb visibly reads as no more dangerous than an already-orbiting one,
    // so it shouldn't still hit like a fast-moving pulled one just because its ring-mates are slower.
    //
    // Supersedes the old one-time set-and-forget calls (TryGrabRing's own initial pullDamageMultiplier set,
    // EndPullingRing's own ring-wide orbitDamageMultiplier set) — recomputing fresh every tick from each
    // pleb's own live IsPulling means there's no edge-detection/timing state to track or get wrong here, and
    // it self-heals from any future desync for free. Cheap — a small SetDamageOverride write per held pleb,
    // same "recompute fully every tick" shape plenty of other per-tick refreshes in this codebase already use.
    private void RefreshHeldDamageOverrides(ref State state)
    {
        // See OrbiterGloomPD.RefreshHeldDamageOverrides' own identical doc comment for why both IsTrueStunned
        // AND isStunnable are read here — for this boss specifically, isStunnable is already scoped to
        // Moving/Orbit only (see HandleActiveState's own per-tick assignment), so this naturally never freezes
        // held plebs during KnockbackWindup/ShootFlight even if a true stun happens to land mid-sequence.
        bool ownerFrozen = IsTrueStunned && isStunnable;
        for (int ring = 0; ring <= 2; ring++)
        {
            var held = RingHeldList(ref state, ring);
            for (int i = 0; i < held.Count; i++)
            {
                var go = hierarchy.GetGameObject(held[i]);
                if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) continue;
                bool isPulling = enemy.IsPulling;
                float multiplier = isPulling ? pullDamageMultiplier : orbitDamageMultiplier;
                enemy.SetDamageOverride(true, damage * multiplier * EnrageDamageMultiplier);
                enemy.SetOrbitFrozen(ownerFrozen && !isPulling);
            }
        }
    }

    private void UpdatePersistentPlebShots(ref State state, float delta)
    {
        if (!state.persistentVolleyActive)
        {
            state.persistentCooldownTimer -= delta;
            if (state.persistentCooldownTimer > 0f) return;

            // Reset BEFORE the grab attempt, not after a successful one — matches "every N seconds, period"
            // (KingSpikePD's own persistent bursts use the identical shape): a volley that finds zero
            // grabbable plebs this cycle (e.g. nothing currently near any player) still consumes its own
            // interval rather than retrying every tick until one appears.
            state.persistentCooldownTimer = state.scaledPersistentShotInterval;
            StartPersistentVolley(ref state);
            return;
        }

        state.persistentVolleyTimer += delta;

        if (!state.persistentShotFired)
        {
            UpdatePersistentBearingTracking(ref state, delta);
            if (state.persistentVolleyTimer >= shotWindupDuration)
                FirePersistentVolley(ref state);
        }
        else
        {
            // Per-slot despawn (2026-09-14) — see DespawnFinishedPersistentIndicators's own doc comment;
            // runs every tick post-fire so each slot's own indicator disappears the instant THAT pleb's own
            // flight actually finishes, independent of the batch flightDuration estimate below (which still
            // gates the actual RELEASE — StopOrbiting/re-enabling collision/etc — unaffected by this).
            DespawnFinishedPersistentIndicators(ref state);

            float flightDuration = shootingRange / Mathf.Max(0.0001f, shotSpeed);
            if (state.persistentVolleyTimer >= flightDuration)
                FinishPersistentVolley(ref state);
        }
    }

    // Grabs one nearby (to THAT player, within grabRadius — see FindNearestUnheldPleb's own doc comment) non-held pleb per
    // non-invisible alive player, claims it (same kinematic/movement/physics/hurtbox + damage-override +
    // ClaimForOrbit treatment TryGrabRing already uses for the real Pull Phase — Claimed-state plebs are a
    // no-op for EnemyBasePD's own per-tick orbit-position update, and LaunchAlong is independent of
    // orbitPhase entirely, so never actually entering OrbitTarget/BeginOrbiting here is safe), and seeds its
    // initial tracked bearing — same "seed instantly, rate-cap from then on" shape as MortarHazardPD.Launch's
    // own initial-bearing seeding. A player with no grabbable pleb within range this cycle simply gets
    // skipped — no persistent shot for them this volley. Capped at MaxConcurrentPersistentTargets, matching
    // the fixed indicator pool size.
    private void StartPersistentVolley(ref State state)
    {
        state.persistentHeld.Clear();
        state.persistentTargetId.Clear();
        state.persistentBearingDir.Clear();
        state.persistentIndicatorId.Clear();
        state.persistentLastKnownPosition.Clear();
        if (EnemySpawnerPD.instance == null) return;

        foreach (var kvp in CharacterStatsPD.All)
        {
            if (state.persistentHeld.Count >= MaxConcurrentPersistentTargets) break;

            var p = kvp.Value;
            // Same invisible-exclusion shape as KingSpikePD.SpawnPersistentRemoteBurst/
            // KingOrbiterPD.BuildShotCandidates.
            if (p.IsDead || (p.IsInvisible && !p.IsTaunting)) continue;

            var chosen = FindNearestUnheldPleb(p.transform.position);
            if (chosen == null) continue;

            chosen.SetKinematic(true);
            chosen.SetMovementDisabled(true);
            chosen.SetPhysicsColliderDisabled(true);
            chosen.SetHurtboxDisabled(true);
            // EnrageDamageMultiplier multiplied in explicitly — see TryGrabRing's own identical comment.
            chosen.SetDamageOverride(true, damage * shootingDamageMultiplier * EnrageDamageMultiplier);
            chosen.ClaimForOrbit(ObjectId);

            state.persistentHeld.Add(chosen.ObjectId);
            state.persistentTargetId.Add(kvp.Key);

            Vector3 toTarget = p.transform.position - chosen.transform.position;
            toTarget.y = 0f;
            // Vector3, not a raw degree float (2026-08-24 fix) — see State.shootTrackedBearing's own doc
            // comment for the interpolation-wraparound jitter bug this avoids. Vector3.forward fallback (not zero —
            // this list has no "not yet tracking" sentinel meaning; every slot here is already grabbed and
            // committed the instant it's added) for the degenerate on-top-of-target case.
            state.persistentBearingDir.Add(toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.forward);
            state.persistentIndicatorId.Add(default);
            state.persistentLastKnownPosition.Add(p.transform.position);
        }

        state.persistentVolleyActive = state.persistentHeld.Count > 0;
        state.persistentShotFired    = false;
        state.persistentVolleyTimer  = 0f;

        // Seeds every newly-grabbed slot's own indicator pose immediately (delta 0, same reasoning as
        // AdvanceShootDispatchRing's own identical call) — every bearing added above is already a real,
        // non-zero direction (never the RotateTowards branch's own seed sentinel), so this just writes each
        // pose from that already-correct value rather than waiting for next tick's regular
        // UpdatePersistentBearingTracking call.
        UpdatePersistentBearingTracking(ref state, 0f);
    }

    // Closest-first (opposite of TryGrabRing's own furthest-first — different mechanic; "a pleb near THIS
    // player" reads more sensibly as the nearest one), ObjectId tiebreaker for determinism. IsBeingHeld
    // already excludes anything this same volley already claimed for an earlier player in the same
    // StartPersistentVolley pass (ClaimForOrbit flips it immediately, same as TryGrabRing's own cross-ring
    // exclusion). Reuses grabRadius (the Pull Phase's own search radius) rather than a separate tunable — per
    // explicit design, one shared "how far this boss's grabs can reach" knob covers both.
    private IEnemyPD FindNearestUnheldPleb(Vector3 origin)
    {
        IEnemyPD best = null;
        float bestDist = float.MaxValue;
        foreach (var enemy in EnemySpawnerPD.instance.ActiveEnemies)
        {
            if (enemy.SpawnCategory != EnemySpawnCategory.Pleb) continue;
            if (enemy._isDead || enemy.IsBeingHeld) continue;
            if (!enemy.HasInitialized) continue; // see TryGrabRing's own identical guard/doc comment
            float dist = Vector3.Distance(origin, enemy.transform.position);
            if (dist > grabRadius) continue;
            if (best == null || dist < bestDist ||
                (dist == bestDist && enemy.ObjectId.instanceId.value < best.ObjectId.instanceId.value))
            {
                bestDist = dist;
                best = enemy;
            }
        }
        return best;
    }

    // Rate-capped bearing tracking toward each slot's OWN assigned target player — same Vector3.RotateTowards
    // shape as UpdateShootBearingTracking above (see its own doc comment for why this is a Vector3, not a raw
    // degree float), just against a fixed per-slot PlayerID instead of the Shooting Phase's one shared
    // _shotTargetSnapshot.
    //
    // A target dying or going invisible mid-windup (2026-08-22) never cancels or stalls this slot — once a
    // slot's own windup starts, nothing stops it, by explicit design. state.persistentLastKnownPosition[i]
    // only gets refreshed while the target is actually alive+visible; while dead/invisible it stays frozen
    // at wherever they were last legitimately seen, and toTarget/RotateTowards below keep running against
    // THAT every tick regardless — so the shot keeps smoothly rate-cap-rotating toward (and eventually
    // fires at) that frozen point, rather than either getting stuck exactly where the bearing happened to be
    // mid-rotation, or following a dead player's transform to wherever it relocates post-death.
    private void UpdatePersistentBearingTracking(ref State state, float delta)
    {
        for (int i = 0; i < state.persistentHeld.Count; i++)
        {
            // Only a truly-gone target (e.g. disconnected) has nothing at all to fall back to — dead/
            // invisible still have a real last-known position to keep tracking, see doc comment above.
            if (!CharacterStatsPD.All.TryGetValue(state.persistentTargetId[i], out var target)) continue;

            var go = hierarchy.GetGameObject(state.persistentHeld[i]);
            if (go == null) continue;

            if (!target.IsDead && (!target.IsInvisible || target.IsTaunting))
                state.persistentLastKnownPosition[i] = target.transform.position;

            Vector3 toTarget = state.persistentLastKnownPosition[i] - go.transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) continue;
            Vector3 targetDir = toTarget.normalized;

            // Rate scaled by EffectiveAttackSpeed (2026-08-31, was plain delta) — see
            // UpdateShootBearingTracking's own identical 2026-08-31 doc comment for why (Slow-consistency;
            // this boss's own phaseTimer still isn't attack-speed-scaled at all).
            state.persistentBearingDir[i] = Vector3.RotateTowards(state.persistentBearingDir[i], targetDir, state.scaledRotationRateDegPerSecond * Mathf.Deg2Rad * delta * EffectiveAttackSpeed, 0f);

            // Same spawn-or-resolve + Simulate-side aim write as UpdateShootBearingTracking's own — see its
            // doc comment and ResolveOrSpawnLineIndicator's own. Persistent-volley plebs never orbit at all
            // (see FindNearestUnheldPleb/StartPersistentVolley's own "never actually entering OrbitTarget/
            // BeginOrbiting" comment) — stationary for their whole windup, same as ShootFlight's held plebs.
            if (i < state.persistentIndicatorId.Count)
            {
                var indicator = ResolveOrSpawnLineIndicator(state.persistentIndicatorId[i], go.transform.position,
                    state.persistentBearingDir[i], shootingRange, out var resolvedId);
                state.persistentIndicatorId[i] = resolvedId;
                indicator?.SetAim(state.persistentBearingDir[i]);
            }
        }
    }

    // Spawns a new LineIndicatorPD via hierarchy.Create() the first time a slot needs one (currentId still
    // default), or resolves the already-spawned instance otherwise — shared by ShootFlight and Persistent
    // Volley tracking. hierarchy.Create() (not Instantiate()) specifically so the prefab's own sibling
    // PredictedTransform component actually registers/ticks and smooths this object's render pose —
    // PredictedTransform is itself a PredictedIdentity that only ticks for objects spawned through PurrNet's
    // hierarchy system, never for a plain Instantiate() clone (confirmed 2026-08-22 — the leftover
    // PredictedTransform sitting unused on the old pooled indicator template never actually ticked for
    // exactly this reason). Simulate-context only — spawn/despawn decisions here are deterministic, same
    // "no verified-gating needed" reasoning as this boss's own held-pleb ClaimForOrbit/ReleaseRing calls.
    // nearEdge/span only matter on the spawn branch (hierarchy.Create()'s own position param + Launch's
    // one-time pivot sizing) — every existing-branch call is pure rotation from here on, see
    // LineIndicatorPD.SetAim's own doc comment for why nearEdge is never revisited after spawn.
    private LineIndicatorPD ResolveOrSpawnLineIndicator(PredictedObjectID currentId, Vector3 nearEdge, Vector3 aim, float span, out PredictedObjectID resolvedId)
    {
        if (!currentId.Equals(default))
        {
            var existingGo = hierarchy.GetGameObject(currentId);
            if (existingGo != null && existingGo.TryGetComponent(out LineIndicatorPD existing))
            {
                resolvedId = currentId;
                return existing;
            }
        }

        resolvedId = default;
        if (lineIndicatorPrefab == null) return null;
        var newId = hierarchy.Create(lineIndicatorPrefab, nearEdge, Quaternion.LookRotation(aim));
        if (!newId.HasValue) return null;

        var go = hierarchy.GetGameObject(newId.Value);
        if (go == null || !go.TryGetComponent(out LineIndicatorPD spawned)) return null;

        spawned.Launch(span);
        resolvedId = newId.Value;
        return spawned;
    }

    // Despawns a single slot's indicator, if any — shared "fire/abort" cleanup shape for both ShootFlight
    // (FireShotRing) and Persistent Volley (FirePersistentVolley/ReleasePersistentHeld). Caller is always
    // responsible for also resetting its own State field back to default right after calling this — a plain
    // DisposableList<T> indexer can't be passed by ref, so this can't clear the caller's copy itself.
    private void DespawnLineIndicator(PredictedObjectID id)
    {
        if (id.Equals(default)) return;
        var go = hierarchy.GetGameObject(id);
        if (go != null && go.TryGetComponent(out LineIndicatorPD indicator)) indicator.Despawn();
    }

    // Reads each slot's own tracked bearing (rate-capped by UpdatePersistentBearingTracking above) — same
    // "indicator and real shot read the identical value" contract FireShotRing's own doc comment establishes
    // for the real Shooting Phase.
    private void FirePersistentVolley(ref State state)
    {
        for (int i = 0; i < state.persistentHeld.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.persistentHeld[i]);
            if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) continue;

            // * EffectiveAttackSpeed — see FireShotRing's own identical 2026-08-31 doc comment.
            enemy.LaunchAlong(state.persistentBearingDir[i], shootingRange, shotSpeed * EffectiveAttackSpeed);
        }
        // No indicator despawn here anymore (2026-09-14, was unconditional-before-the-guard, same shape as
        // FireShotRing's own old bug) — per explicit design, a slot's own indicator should stay visible for
        // that SPECIFIC pleb's ENTIRE movement, not disappear the instant it launches. See
        // DespawnFinishedPersistentIndicators's own doc comment for where despawn actually happens now.
        state.persistentShotFired   = true;
        state.persistentVolleyTimer = 0f;
    }

    // Per-slot indicator cleanup (2026-09-14), checked every tick while winding down post-fire — despawns
    // slot i's own indicator the INSTANT that specific pleb's own flight actually finishes (enemy.IsLaunching
    // flips back false) or it becomes invalid (dead/unresolvable), fully independent of every other slot.
    // Unlike Shoot's own ring-reused slots (see State.shootTrackedPleb's own doc comment for why THAT needed
    // a full rework), persistentHeld/persistentIndicatorId are already a flat, non-reused list — rebuilt fresh
    // every StartPersistentVolley call, stable indices for this one volley's whole life — so no slot-identity
    // rework is needed here, just moving WHEN each slot's own despawn happens.
    private void DespawnFinishedPersistentIndicators(ref State state)
    {
        for (int i = 0; i < state.persistentHeld.Count && i < state.persistentIndicatorId.Count; i++)
        {
            if (state.persistentIndicatorId[i].Equals(default)) continue;

            var go = hierarchy.GetGameObject(state.persistentHeld[i]);
            bool finished = go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead || !enemy.IsLaunching;
            if (!finished) continue;

            DespawnLineIndicator(state.persistentIndicatorId[i]);
            state.persistentIndicatorId[i] = default;
        }
    }

    private void FinishPersistentVolley(ref State state)
    {
        ReleasePersistentHeld(ref state);
        state.persistentVolleyActive = false;
        state.persistentShotFired    = false;
    }

    // Un-flags every currently-held persistent slot and clears the lists — same 6-call release shape as
    // ReleaseRing's own body. Called both from normal post-flight completion (FinishPersistentVolley) and
    // from Die() (immediate release on death, same reasoning as ReleaseAllHeld's own Die() call). Safe to
    // call with an empty list (e.g. no volley currently active).
    private void ReleasePersistentHeld(ref State state)
    {
        for (int i = 0; i < state.persistentHeld.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.persistentHeld[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy))
            {
                enemy.StopOrbiting();
                enemy.SetKinematic(false);
                enemy.SetMovementDisabled(false);
                enemy.SetPhysicsColliderDisabled(false);
                enemy.SetHurtboxDisabled(false);
                enemy.SetDamageOverride(false);
            }
        }
        // Covers the Die()-mid-windup path — FirePersistentVolley's own despawn never ran for a volley that
        // never got to fire, so this is the only cleanup that path gets. A no-op for the normal
        // post-fire path (FinishPersistentVolley), since every entry is already default by then.
        for (int i = 0; i < state.persistentIndicatorId.Count; i++)
            DespawnLineIndicator(state.persistentIndicatorId[i]);
        state.persistentHeld.Clear();
        state.persistentTargetId.Clear();
        state.persistentBearingDir.Clear();
        state.persistentIndicatorId.Clear();
        state.persistentLastKnownPosition.Clear();
    }

    // ── View ─────────────────────────────────────────────────────────────────

    private KingOrbiterPhase _lastVisualState = (KingOrbiterPhase)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);
        if (!gameObject.activeInHierarchy) return;

        // centerForwardOffset left at its 0 default — knockbackIndicator's own current position (WindupFillPD's
        // own transform) already IS the center.
        if (knockbackIndicator != null && knockbackFill != null)
            knockbackFill.ShowCircle(knockbackRadius);

        var phase = GetPhase(ref viewState);
        UpdatePullLineIndicators(viewState, phase);
        UpdateShootIndicatorFills(viewState, phase);
        // Independent of `phase` above — Persistent Pleb Shots run regardless of phaseId, and can be visible
        // at the same time as a real Shooting Phase's own indicators.
        UpdatePersistentIndicatorFills(viewState);

        // Live, every frame regardless of the _lastVisualState-gated block below — reads the exact same
        // phaseTimer field that gates the real knockback trigger (HandleActiveState's own
        // state.phaseTimer <= 0f check), so it hits exactly 1 (full) on the identical tick the knockback
        // actually fires, by construction. A self-timed StartFill(knockbackChargeTime) here was drifting out
        // of sync with the real trigger (confirmed live, 2026-08-12) — WindupFillPD's own Time.deltaTime loop
        // and the tick-based state timer are two independent clocks that only approximately match, never
        // guaranteed to. Same fix shape as KingSpikePD.chargeWindupFill's own live SetProgress (used there
        // because that windup is attack-speed-scaled/variable — here it's for exact-sync correctness instead).
        if (knockbackFill != null && phase == KingOrbiterPhase.KnockbackWindup)
            knockbackFill.SetProgress(1f - Mathf.Clamp01(viewState.phaseTimer / knockbackChargeTime));

        if (phase == _lastVisualState) return;
        _lastVisualState = phase;

        if (knockbackIndicator != null) knockbackIndicator.SetActive(phase == KingOrbiterPhase.KnockbackWindup);
        if (knockbackFill != null && phase != KingOrbiterPhase.KnockbackWindup) knockbackFill.Hide();
    }

    // Scratch buffers for UpdatePullLineIndicators — this frame's flat list of "currently active pull
    // entries" across ALL 3 rings, rebuilt fresh every call, same zero-alloc convention as _grabCandidates.
    // No per-entry identity is kept between frames (nothing needs to be — every value below is fully
    // recomputed from live positions each call, so which POOL slot happens to represent which pleb from one
    // frame to the next is irrelevant; see UpdatePullLineIndicators's own doc comment).
    private static readonly List<Vector3> _activePullNearEdge     = new();
    private static readonly List<Vector3> _activePullPlebPos      = new();
    private static readonly List<float>   _activePullLocalElapsed = new();

    // Pull Phase's own pool only now (2026-08-22 — ShootFlight moved to spawn-per-slot LineIndicatorPD, see
    // UpdateShootIndicatorFills below). Near edge = a pleb's own orbit slot (radius/SlotAngleDeg offset
    // within ITS OWN ring), far edge = the live pleb position — same math/reasoning as
    // OrbiterGloomPD.UpdateView's own windup indicators; recomputed fresh from already-smooth live transform
    // reads every render frame, no rate-cap/smoothing needed.
    //
    // 2026-09-14 rework — per explicit design, a pleb's own indicator must stay visible for its ENTIRE
    // movement (windup THROUGH the actual pull-in), only hiding once THAT SPECIFIC pleb has genuinely
    // arrived (!IsPulling — see that property's own doc comment for why this isn't just IsWaitingToOrbit) or
    // become invalid, fully independent of every other pleb. This means plebs
    // from MULTIPLE DIFFERENT rings can be active/showing at once (dispatch deliberately runs ahead of
    // release — see HandleActiveState's Pulling case doc comment), so a fixed per-ring "slot i belongs to
    // ring-local position i" mapping (the old shape) no longer works: instead, this walks every real ring
    // (0..2) collecting whichever of its held plebs are currently active into a flat list — "just grab any
    // unused indicator" — and pool indicators 0..N-1 simply display entries 0..N-1 of THAT list, with no
    // fixed ring/pool-index binding at all. SlotAngleDeg(j, ringHeldCount) below still needs each pleb's own
    // RING-LOCAL index j (for where in ITS ring it's actually orbiting to) — that's computed while walking
    // each ring, just never used to pick a pool slot anymore.
    private void UpdatePullLineIndicators(State viewState, KingOrbiterPhase phase)
    {
        if (_pullLineIndicators == null) return;

        _activePullNearEdge.Clear();
        _activePullPlebPos.Clear();
        _activePullLocalElapsed.Clear();

        if (phase == KingOrbiterPhase.Pulling)
        {
            // Every real ring whose OWN dispatch has at least started can have active members — clamped to 2
            // (the real max ring index) regardless of how far dispatch has progressed, including past 3 (see
            // the historical "phantom 4th ring" bug this same clamp originally fixed, 2026-08-21 — RingHeldList
            // has no case for ring 3+, so an unclamped read would silently reuse outerHeld a second time).
            int maxDispatchRing = Mathf.Min(viewState.pullDispatchRingIndex, 2);
            for (int ring = 0; ring <= maxDispatchRing; ring++)
            {
                var held = RingHeldList(ref viewState, ring);
                int heldCount = held.Count;
                if (heldCount == 0) continue;
                int ringGlobalStart = RingGlobalStart(ref viewState, ring);

                for (int j = 0; j < heldCount; j++)
                {
                    float localElapsed = viewState.phaseTimer - (ringGlobalStart + j) * viewState.scaledPullSequentialDelay;
                    if (localElapsed < 0f) continue; // not this pleb's own turn yet

                    var go = hierarchy.GetGameObject(held[j]);
                    // !IsPulling (2026-09-16, was `enemy.IsWaitingToOrbit` — a real bug, see IEnemyPD.IsPulling's
                    // own doc comment) — this pleb's indicator job is done the instant its own orbitPhase moves
                    // PAST Pulling (WaitingToOrbit OR Orbiting), not just while it sits in the specific
                    // WaitingToOrbit window. A ring's own release (EndPullingRing -> BeginOrbiting) can flip a
                    // pleb straight past that check while OTHER rings keep this whole block running, which used
                    // to silently re-show a stale-looking indicator tracking an already-orbiting pleb's live
                    // position for however long those other rings took to finish.
                    if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead || !enemy.IsPulling)
                        continue;

                    float   rad = SlotAngleDeg(j, heldCount) * Mathf.Deg2Rad;
                    Vector3 dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                    Vector3 slotPos = transform.position + dir * RingRadius(ring);

                    _activePullNearEdge.Add(slotPos);
                    _activePullPlebPos.Add(go.transform.position);
                    _activePullLocalElapsed.Add(localElapsed);
                }
            }
        }

        int activeCount = _activePullNearEdge.Count;
        for (int i = 0; i < _pullLineIndicators.Length; i++)
        {
            var indicator = _pullLineIndicators[i];
            if (indicator == null) continue;

            bool slotFilled = i < activeCount;
            var fill = _pullLineFills != null && i < _pullLineFills.Length ? _pullLineFills[i] : null;
            if (indicator.gameObject.activeSelf != slotFilled)
            {
                indicator.gameObject.SetActive(slotFilled);
                if (fill != null && !slotFilled) fill.Hide();
            }
            if (!slotFilled) continue;

            Vector3 slotPos = _activePullNearEdge[i];
            Vector3 plebPos = _activePullPlebPos[i];

            Vector3 toTarget = plebPos - slotPos;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            if (distance <= 0.001f) continue;
            Vector3 nearEdge = slotPos;
            Vector3 aim      = toTarget / distance;
            float   span     = distance;

            // ROTATION MUST BE SET BEFORE SetProgress below — see OrbiterGloomPD.UpdatePullLineIndicators'
            // own identical comment for why (WindupFillPD's outline still reads this object's live
            // transform.forward/.right for its corner math; the fill mesh's own position no longer does).
            indicator.SetPositionAndRotation(nearEdge + aim * (span * 0.5f), Quaternion.LookRotation(aim));

            // centerForwardOffset left at its 0 default — indicator's own position (just set above) already
            // IS the true center.
            if (fill != null)
            {
                fill.ShowRectangle(span);
                // Live, every frame, off this entry's own localElapsed — see
                // feedback_windupfill_self_timed_drift: a self-timed StartFill() runs on its own
                // Time.deltaTime clock, independent of the tick-based phaseTimer that actually gates
                // BeginPull(), and can drift out of sync with it. Naturally clamps at 1 ("fully charged")
                // once localElapsed exceeds windupDuration — i.e. for the whole actual pull-in movement, past
                // the windup itself — matching the same post-fire simplification Shoot/Persistent's own fills
                // use (see UpdateShootIndicatorFills's own doc comment).
                fill.SetProgress(Mathf.Clamp01(_activePullLocalElapsed[i] / windupDuration));
            }
        }
    }

    // Fill-progress-only pass for the spawned ShootFlight indicators (2026-08-22) — pose is already fully
    // owned by Simulate (UpdateShootBearingTracking/FireShotRing), and existence itself (a non-default
    // shootTrackedIndicator entry) already means "this pleb is currently active" — nothing here needs to
    // decide visibility, only read each spawned instance's own WindupFillPD and update its progress.
    //
    // 2026-09-14 rework — iterates the flat shootTracked* lists directly instead of a ring-local-indexed
    // array, and reads each entry's own shootTrackedWindupStart (the exact phaseTimer value ITS OWN window
    // opened at) instead of deriving elapsed time from a ring-global-start + ring-local-index formula — see
    // that field's own doc comment. Once fired, localElapsed keeps growing past shotWindupDuration, so
    // Clamp01 below just keeps reading "fully charged" for the rest of this entry's (now purely
    // flight-duration) life, which is an intentional, harmless simplification.
    private void UpdateShootIndicatorFills(State viewState, KingOrbiterPhase phase)
    {
        if (phase != KingOrbiterPhase.ShootFlight) return;

        for (int i = 0; i < viewState.shootTrackedIndicator.Count; i++)
        {
            var id = viewState.shootTrackedIndicator[i];
            if (id.Equals(default)) continue;

            var go = hierarchy.GetGameObject(id);
            if (go == null || !go.TryGetComponent(out LineIndicatorPD indicator) || indicator.Fill == null) continue;

            // Shrinks the shown span to "collapse in behind" the pleb as it actually flies (2026-09-14) — see
            // LineIndicatorPD.SetTraveled's own doc comment for the full mechanism. Distance from the
            // indicator's own root (frozen at the pleb's launch origin, see LineIndicatorPD's own class doc
            // comment) to the pleb's own CURRENT transform.position — deliberately a live transform read, NOT
            // IEnemyPD.LaunchRemainingDistance (a first attempt, confirmed live to desync: that reads the
            // pleb's own raw, tick-accurate predicted state directly, while what the player actually SEES is
            // the pleb's separately-smoothed/interpolated Visuals position, so the shrink ran visibly ahead of
            // where the pleb appeared to be, syncing back up only as flight neared completion). Reading the
            // pleb's own transform.position instead matches whatever's actually rendered exactly, by
            // construction — same "read a DIFFERENT object's own live position, safe and already proven"
            // pattern King Orbiter/Orbiter's own Pull indicators already use for their identical shrink (see
            // UpdatePullLineIndicators' own plebPos read) — this is not the self-referential live-transform
            // pattern that's unsafe elsewhere in this codebase, since the indicator itself is never parented
            // under the pleb at all. Also fixes the separate "flashes back to full size right as flight ends"
            // bug for free: the old IEnemyPD.IsLaunching-gated fallback reset traveled to 0 the instant flight
            // finished (IsLaunching flips false) but before the entry was actually removed; a live distance
            // read has no such gate — the pleb's own final landed position naturally reads as fully traveled.
            var pebGo = hierarchy.GetGameObject(viewState.shootTrackedPleb[i]);
            float traveled = pebGo != null ? Vector3.Distance(indicator.transform.position, pebGo.transform.position) : 0f;
            indicator.SetTraveled(traveled);

            float localElapsed = viewState.phaseTimer - viewState.shootTrackedWindupStart[i];
            indicator.Fill.SetProgress(Mathf.Clamp01(localElapsed / shotWindupDuration));
        }
    }

    // Same role as UpdateShootIndicatorFills above, for the Persistent Volley pool — persistentIndicatorId
    // entries only exist while winding up (see StartPersistentVolley/FirePersistentVolley), so every non-
    // default entry here is by construction still mid-windup; no extra active-gating needed.
    private void UpdatePersistentIndicatorFills(State viewState)
    {
        for (int i = 0; i < viewState.persistentIndicatorId.Count; i++)
        {
            var id = viewState.persistentIndicatorId[i];
            if (id.Equals(default)) continue;

            var go = hierarchy.GetGameObject(id);
            if (go == null || !go.TryGetComponent(out LineIndicatorPD indicator) || indicator.Fill == null) continue;

            // Same "collapse in behind the pleb" shrink as UpdateShootIndicatorFills — see that method's own
            // 2026-09-14 doc comment for why this reads a live distance to persistentHeld[i]'s own
            // transform.position, not IEnemyPD.LaunchRemainingDistance.
            var pebGo = hierarchy.GetGameObject(viewState.persistentHeld[i]);
            float traveled = pebGo != null ? Vector3.Distance(indicator.transform.position, pebGo.transform.position) : 0f;
            indicator.SetTraveled(traveled);

            // Gated on persistentShotFired (2026-09-14 fix), not a raw ratio off persistentVolleyTimer alone
            // — that timer is dual-purpose (see its own doc comment: counts up to shotWindupDuration while
            // winding up, then RESETS TO 0 and counts up again to flight duration once fired), so reading the
            // raw ratio unconditionally made the fill visibly empty back toward 0 and refill the instant a
            // shot launched, instead of staying fully filled through the whole flight — confirmed live, same
            // "needs some love" indicator-fill class of bug as the one just fixed on Shoot/Pull. Per explicit
            // design, once fired this always reads as fully filled, only resetting when the indicator itself
            // is actually despawned.
            float progress = viewState.persistentShotFired ? 1f : Mathf.Clamp01(viewState.persistentVolleyTimer / shotWindupDuration);
            indicator.Fill.SetProgress(progress);
        }
    }

    protected override void OnDeathStop()
    {
        // ShootFlight/Persistent Volley indicators need no hiding here — Die()'s own Simulate-side
        // DespawnAllShootIndicators/ReleasePersistentHeld calls already despawned them (this View-context
        // hook runs later, after that same tick's Simulate has already completed).
        HideIndicatorPool(_pullLineIndicators, _pullLineFills);
        if (knockbackIndicator != null) knockbackIndicator.SetActive(false);
        if (knockbackFill != null) knockbackFill.Hide();
    }

    private void HideIndicatorPool(Transform[] indicators, WindupFillPD[] fills)
    {
        if (indicators != null)
            foreach (var t in indicators) if (t != null) t.gameObject.SetActive(false);
        if (fills != null)
            foreach (var f in fills) if (f != null) f.Hide();
    }

    protected override void OnRespawnReset()
    {
        currentState.phaseId             = 0;
        currentState.phaseTimer          = 0f;
        // Full cooldown, matching LateAwake()'s own reasoning — same "field needs time to repopulate" logic
        // applies to a respawn (e.g. a difficulty-pacing wipe right before this boss spawns) as to a fresh
        // spawn.
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        currentState.pullRingIndex          = 0;
        currentState.pullDispatchRingIndex  = 0;
        currentState.pullRingLocalIndex     = 0;
        currentState.shootRingIndex         = 0;
        currentState.shootDispatchRingIndex = 0;
        currentState.shootRingLocalIndex    = 0;
        currentState.pullSequenceIndex      = 0;
        currentState.shootSequenceIndex     = 0;
        currentState.shootNextGlobalStart   = 0;
        currentState.shootRingGlobalStart0  = 0;
        currentState.shootRingGlobalStart1  = 0;
        currentState.shootRingGlobalStart2  = 0;
        currentState.lastShotTargetId    = default;
        currentState.innerHeld.Clear();
        currentState.middleHeld.Clear();
        currentState.outerHeld.Clear();
        // Defensive clear, no despawn attempt — Die()'s own DespawnAllShootIndicators already despawned
        // anything actually spawned (and cleared these same lists) before this pooled reuse could happen,
        // same "already released" reasoning as persistentHeld/persistentTargetId/persistentBearingDir below.
        currentState.shootTrackedPleb.Clear();
        currentState.shootTrackedBearing.Clear();
        currentState.shootTrackedIndicator.Clear();
        currentState.shootTrackedFired.Clear();
        currentState.shootTrackedWindupStart.Clear();
        // Full grace, matching LateAwake()'s own reasoning for persistentCooldownTimer. persistentHeld/
        // persistentTargetId/persistentBearingDir/persistentIndicatorId cleared directly (not via
        // ReleasePersistentHeld) — same reasoning as innerHeld/middleHeld/outerHeld above: Die() already
        // released anything actually held before this pooled reuse could happen.
        currentState.persistentCooldownTimer = currentState.scaledPersistentShotInterval;
        currentState.persistentVolleyActive  = false;
        currentState.persistentShotFired     = false;
        currentState.persistentVolleyTimer   = 0f;
        currentState.persistentHeld.Clear();
        currentState.persistentTargetId.Clear();
        currentState.persistentBearingDir.Clear();
        currentState.persistentIndicatorId.Clear();
        currentState.persistentLastKnownPosition.Clear();
        OnDeathStop();
        // In case death/pooling happened mid-windup, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (phaseId resets to Moving above), but resetting explicitly
        // here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState    = (KingOrbiterPhase)(-1);
        _shotTargetSnapshot = null;
    }
}
