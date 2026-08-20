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
// (outer first, then middle, then inner) at one shared random target per ring, ALSO one at a time with a
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
// independent per ring in both phases — inner/middle/outer (or outer/middle/inner for Shooting) each release
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
        // Shoot: ReleaseRing, see EndShotRing) is next pending — 0=inner,1=middle,2=outer for Pull;
        // 0=outer,1=middle,2=inner (shoot order, see ShootPhysicalRing) for Shoot. Deliberately INDEPENDENT
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
        // dispatch-vs-release split, just walking shoot order (ShootPhysicalRing: 0=outer,1=middle,2=inner)
        // instead of pull order. See AdvanceShootDispatchRing/EndShotRing.
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
        public DisposableList<PredictedObjectID> innerHeld;
        public DisposableList<PredictedObjectID> middleHeld;
        public DisposableList<PredictedObjectID> outerHeld;
        // Last ring's chosen shot target — excluded from the next ring's random pick when possible ("unique
        // from the last target if possible"). Default(PlayerID) on a fresh spawn just means "nothing to
        // exclude yet", same as every other unguarded default-sentinel PlayerID field in this codebase (e.g.
        // EnemyBaseState.killerOwner).
        public PlayerID lastShotTargetId;
        // Per-slot tracked bearing DIRECTION (unit Vector3, not a raw angle) during ShootFlight, one entry
        // per physical indicator slot — same indexing/growth as _lineIndicators/_lineFills (grown alongside
        // them, see EnsureShootBearingCapacity). Vector3.zero means "not currently tracking" (either this
        // slot's own window hasn't opened yet this ring, or dispatch has moved past it) — the sentinel
        // UpdateShootBearingTracking checks to know when to seed fresh instead of incrementing. Both the
        // windup indicator's own aim (UpdateLineIndicators) AND the real LaunchAlong direction
        // (FireShotRing) read this SAME tracked value, so what's shown is always exactly what fires — see
        // UpdateShootBearingTracking's own doc comment for the full rate-cap reasoning.
        //
        // Vector3, NOT a raw degree float (2026-08-24 fix, was a jitter bug confirmed live) — PurrDiction
        // linearly interpolates every plain State field for viewState/UpdateView's own smooth-between-ticks
        // rendering, same as it does for a Vector3 position. A raw ANGLE interpolated that way breaks
        // violently every time it crosses the ±180°/0°-360° wraparound (Mathf.Atan2's own range boundary) —
        // the interpolated value snaps through the OPPOSITE direction for one frame, visible as constant
        // jitter on the windup indicator lines. A unit direction Vector3 has no such discontinuity: two
        // nearby directions (which is all any two adjacent ticks can ever be, since tracking is itself
        // rate-capped) interpolate to a reasonable in-between vector with nothing to snap across. Rate-cap
        // math now uses Vector3.RotateTowards instead of Mathf.MoveTowardsAngle — see
        // UpdateShootBearingTracking's own doc comment.
        public DisposableList<Vector3> shootBearingDir;

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

        public void Dispose()
        {
            b.contactCooldowns.Dispose();
            b.killLaunchHitIds.Dispose();
            innerHeld.Dispose();
            middleHeld.Dispose();
            outerHeld.Dispose();
            shootBearingDir.Dispose();
            persistentHeld.Dispose();
            persistentTargetId.Dispose();
            persistentBearingDir.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.innerHeld = innerHeld.Duplicate();
            copy.middleHeld = middleHeld.Duplicate();
            copy.outerHeld = outerHeld.Duplicate();
            copy.shootBearingDir = shootBearingDir.Duplicate();
            copy.persistentHeld = persistentHeld.Duplicate();
            copy.persistentTargetId = persistentTargetId.Duplicate();
            copy.persistentBearingDir = persistentBearingDir.Duplicate();
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
    [SerializeField] private float pullDuration   = 0.25f;
    // Gap between each held pleb's own OrbitTarget() dispatch during Pulling (2026-08-17 — used to pull a
    // whole ring in one shot) — see BeginPull's own doc comment. Tier1-authored baseline, scaled per tier by
    // pullSequentialDelayTierGrowth below (applied in OnConfigureSpawn()) — SHRINKS per tier ("20% faster"),
    // see that field's own doc comment for the exact formula shape.
    [SerializeField] private float pullSequentialDelay = 0.25f;

    [Header("Pull - Inner Ring")]
    // Tier1-authored baseline, scaled per tier by innerCountTierGrowth below (applied in OnConfigureSpawn()).
    [SerializeField] private int   innerCount             = 2;
    [SerializeField] private float innerOrbitRadius        = 2f;
    [SerializeField] private float innerOrbitAngularSpeed  = 120f;

    [Header("Pull - Middle Ring")]
    // Tier1-authored baseline, scaled per tier by middleCountTierGrowth below (applied in OnConfigureSpawn()).
    [SerializeField] private int   middleCount             = 3;
    [SerializeField] private float middleOrbitRadius        = 5.5f;
    [SerializeField] private float middleOrbitAngularSpeed  = 60f;

    [Header("Pull - Outer Ring")]
    // Tier1-authored baseline, scaled per tier by outerCountTierGrowth below (applied in OnConfigureSpawn()).
    [SerializeField] private int   outerCount             = 4;
    [SerializeField] private float outerOrbitRadius        = 9f;
    [SerializeField] private float outerOrbitAngularSpeed  = 30f;

    [Header("Orbit")]
    [SerializeField] private float orbitDuration            = 10f;

    [Header("Knockback")]
    // Tier1-authored baseline, scaled per tier by knockbackChargeTimeTierGrowth below (applied in
    // OnConfigureSpawn()) — SHRINKS per tier ("20% faster"), same shape as pullSequentialDelay above.
    [SerializeField] private float        knockbackChargeTime = 1.5f;
    [SerializeField] private float        knockbackRadius     = 12f;
    [SerializeField] private float        knockbackForce      = 150f;
    [SerializeField] private GameObject   knockbackIndicator;
    [SerializeField] private WindupFillPD knockbackFill;

    [Header("Shooting")]
    [SerializeField] private float shootingRange     = 40f;
    [SerializeField] private float shotWindupDuration = 1f;
    // Tier1-authored baseline, scaled per tier by shotSpeedTierGrowth below (applied in OnConfigureSpawn())
    // — GROWS per tier ("20% faster", a rate, not a duration — see that field's own doc comment for the
    // exact formula shape, opposite direction from pullSequentialDelay/knockbackChargeTime above).
    [SerializeField] private float shotSpeed          = 50f;
    // Gap between each held pleb's own LaunchAlong() dispatch during ShootFlight (2026-08-17 — used to
    // launch a whole ring in one shot) — see FireShotRing's own doc comment. Independently tunable from
    // pullSequentialDelay (separate attack, separate feel), per explicit design.
    [SerializeField] private float shootSequentialDelay = 0.25f;
    // Max bearing turn rate while a held pleb is tracking its own shot during ShootFlight (2026-08-21) —
    // copied from ShielderGloomPD's own rotationRateDegPerSecond/rotationRateDegPerSecondTierGrowth (45
    // base, 0.2 tier growth). Caps how fast a held pleb's tracked aim can swing so circling this boss during
    // its Shoot windup is a real counterplay option — see UpdateShootBearingTracking's own doc comment.
    [SerializeField] private float rotationRateDegPerSecond = 45f;
    private float _authoredRotationRateDegPerSecond;

    [Header("Persistent Pleb Shots")]
    // Fires an independent volley at EVERY non-invisible alive player on a flat interval, entirely
    // independent of this boss's own phaseId FSM (same tail-in-Simulate() shape as KingSpikePD's own
    // Persistent Remote Spike Bursts) — grabs one nearby (to THAT player, within grabRadius)
    // non-held pleb per qualifying player, winds it up, then launches it. Inherits shootingDamageMultiplier/
    // shotWindupDuration/shotSpeed/rotationRateDegPerSecond (and their existing tier scaling) straight from
    // the Shooting header's own fields above — deliberately no separate copies of any of those, per explicit
    // design ("inherit the exact same shooting parameters as the real deal"). See StartPersistentVolley's own
    // doc comment for the full grab/windup/fire/release flow.
    [SerializeField] private float persistentShotInterval = 3f;
    // Second, dedicated indicator pool (2026-08-19) — deliberately NOT sharing _lineIndicators/_lineFills:
    // unlike Pulling/ShootFlight (which the class-level doc comment already notes are mutually exclusive),
    // a Persistent Pleb Shot volley runs independent of phaseId and can be active AT THE SAME TIME as a real
    // Shooting Phase, so the two pools would otherwise collide over the same slots.
    [SerializeField] private Transform persistentLineIndicatorTemplate;
    // Captured once in OnAwake(), reset back onto persistentShotInterval every pooled reuse in LateAwake(),
    // scaled by attackCooldownMultiplier (Threat Level 5) in OnConfigureSpawn() — same "_authoredX snapshot"
    // pattern as attackCooldown itself.
    private float _authoredPersistentShotInterval;

    private Transform[]    _persistentLineIndicators;
    private WindupFillPD[] _persistentLineFills;

    // Fixed pool size, not lazily grown like _lineIndicators (2026-08-19) — growing an indicator pool means
    // Instantiate(), which has to happen OUTSIDE Simulate(); but StartPersistentVolley (which decides how
    // many targets a given volley actually needs) runs INSIDE Simulate(), including during resimulation
    // replay, where spawning extra GameObjects would be actively harmful. Sized to this game's own hard
    // player cap instead of anything tier/ring-derived — see feedback_multiplayer_scale_reminder.
    private const int MaxConcurrentPersistentTargets = 4;

    [Header("Attack Cadence")]
    [SerializeField] private float attackCooldown = 10f;
    // Captured once in OnAwake(), reset back onto attackCooldown every pooled reuse in LateAwake(), scaled
    // by attackCooldownMultiplier (Threat Level 5) in OnConfigureSpawn() — same "_authoredX snapshot"
    // pattern as the 6 tier-scaled fields below.
    private float _authoredAttackCooldown;

    [Header("Tier Scaling")]
    // Duration fields SHRINK per tier (base * (1 - (tier-1)*growth)) — "X% faster" means less time, not more.
    // Rate/count fields GROW per tier — see each field's own default/comment for which shape it uses. Linear
    // per tier off the tier1-authored base in all cases, never compounding/exponential (user's explicit
    // "multiplicative fields multiply base, no exponential tier scaling" rule) — same shape
    // EnemySpawnerPD/ShielderGloomPD's own *TierGrowth fields already establish.
    [SerializeField] private float pullSequentialDelayTierGrowth = 0.2f;
    [SerializeField] private int   innerCountTierGrowth          = 1;
    [SerializeField] private int   middleCountTierGrowth         = 2;
    [SerializeField] private int   outerCountTierGrowth          = 3;
    [SerializeField] private float knockbackChargeTimeTierGrowth = 0.2f;
    [SerializeField] private float shotSpeedTierGrowth            = 0.2f;
    // Same linear-per-tier shape as every other *TierGrowth field — see ShielderGloomPD's own identical
    // field for the precedent this was copied from.
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;

    // Captured once in OnAwake(), reset back onto these 6 fields every pooled reuse in LateAwake() — same
    // "_authoredX snapshot" pattern established for every other tier-scaled field on Blitz/Dagger/Mortar/
    // Spear/Orbiter/Shielder/Shooter. All 6 are read directly (not mirrored into predicted State) at their
    // own use sites — see OnConfigureSpawn() for where the actual scaling happens.
    private float _authoredPullSequentialDelay;
    private int   _authoredInnerCount;
    private int   _authoredMiddleCount;
    private int   _authoredOuterCount;
    private float _authoredKnockbackChargeTime;
    private float _authoredShotSpeed;

    // Single shared line-indicator pool, cloned to the largest ring's own ENRAGED capacity in LateAwake()
    // (see BuildIndicatorPool) — same Instantiate-N-times convention as OrbiterGloomPD.windupIndicatorTemplate.
    // Reused for BOTH the Pull Phase's per-ring windup and the Shooting Phase's per-ring aim lines — Pulling
    // and ShootFlight are mutually exclusive phases (never active at the same time), so one pool safely
    // covers both; only the near/far-edge math and fill duration differ per phase (see UpdateLineIndicators).
    [Header("Indicators")]
    [SerializeField] private Transform lineIndicatorTemplate;

    private Transform[]    _lineIndicators;
    private WindupFillPD[] _lineFills;

    // Scratch buffers — cleared and reused every call, same zero-alloc convention as OrbiterGloomPD's own
    // _grabCandidates.
    private static readonly List<IEnemyPD>        _grabCandidates     = new();
    private static readonly List<CharacterStatsPD> _shotTargetScratch = new();
    private static readonly List<PlayerID>         _shotTargetIdScratch = new();

    // Plain (not predicted State) snapshot of the current ring's chosen shot target — same
    // KingSpikePD._chargeDirectionSnapshot precedent: instantly available on every machine the moment
    // PickShotTarget() decides it (no network round-trip/interpolation), used only for UpdateView's own
    // indicator aim. The actual launch direction (FireShotRing) reads live CharacterStatsPD state directly,
    // not this snapshot.
    private CharacterStatsPD _shotTargetSnapshot;

    protected override void OnAwake()
    {
        base.OnAwake();
        _authoredPullSequentialDelay = pullSequentialDelay;
        _authoredInnerCount          = innerCount;
        _authoredMiddleCount         = middleCount;
        _authoredOuterCount          = outerCount;
        _authoredKnockbackChargeTime = knockbackChargeTime;
        _authoredShotSpeed           = shotSpeed;
        _authoredAttackCooldown      = attackCooldown;
        _authoredRotationRateDegPerSecond = rotationRateDegPerSecond;
        _authoredPersistentShotInterval = persistentShotInterval;
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        // Reset to the tier1-authored baseline before OnConfigureSpawn (which always runs AFTER LateAwake(),
        // once hierarchy.Create() returns) scales these fresh for this life — see
        // _authoredPullSequentialDelay's own doc comment.
        pullSequentialDelay = _authoredPullSequentialDelay;
        innerCount          = _authoredInnerCount;
        middleCount         = _authoredMiddleCount;
        outerCount          = _authoredOuterCount;
        knockbackChargeTime = _authoredKnockbackChargeTime;
        shotSpeed           = _authoredShotSpeed;
        attackCooldown      = _authoredAttackCooldown;
        rotationRateDegPerSecond = _authoredRotationRateDegPerSecond;
        persistentShotInterval = _authoredPersistentShotInterval;
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

    // Tier1-authored innerCount/middleCount/outerCount/pullSequentialDelay/knockbackChargeTime/shotSpeed,
    // scaled by each field's own *TierGrowth (see the "Tier Scaling" header's own doc comment for the
    // duration-shrinks/rate-grows formula shapes). spawnTier is already final by the time this runs (set
    // earlier in the same ConfigureSpawn() call) — ring counts scaled BEFORE EnsureIndicatorPoolCapacity()
    // so the pool always sizes to this life's FINAL counts.
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        pullSequentialDelay *= 1f - (spawnTier - 1) * pullSequentialDelayTierGrowth;
        innerCount          += (spawnTier - 1) * innerCountTierGrowth;
        middleCount         += (spawnTier - 1) * middleCountTierGrowth;
        outerCount          += (spawnTier - 1) * outerCountTierGrowth;
        knockbackChargeTime *= 1f - (spawnTier - 1) * knockbackChargeTimeTierGrowth;
        shotSpeed            *= 1f + (spawnTier - 1) * shotSpeedTierGrowth;
        rotationRateDegPerSecond = _authoredRotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);
        EnsureIndicatorPoolCapacity(lineIndicatorTemplate, ref _lineIndicators, ref _lineFills,
            Mathf.Max(innerCount, Mathf.Max(middleCount, outerCount)));
        EnsureShootBearingCapacity(ref currentState, _lineIndicators?.Length ?? 0);
        EnsureIndicatorPoolCapacity(persistentLineIndicatorTemplate, ref _persistentLineIndicators, ref _persistentLineFills,
            MaxConcurrentPersistentTargets);

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        attackCooldown = _authoredAttackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = attackCooldown;

        // Same Threat Level 5 scaling, explicitly requested to apply to Boss Persistent Attacks too — re-set
        // the placeholder persistentCooldownTimer LateAwake() already assigned above, using the now-scaled
        // value.
        persistentShotInterval = _authoredPersistentShotInterval * attackCooldownMultiplier;
        currentState.persistentCooldownTimer = persistentShotInterval;
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
            clone.gameObject.SetActive(false);
            newIndicators[i] = clone;
            newFills[i] = clone.GetComponentInChildren<WindupFillPD>(true);
        }
        indicators = newIndicators;
        fills      = newFills;
        template.gameObject.SetActive(false);
    }

    // Grows state.shootBearingDir to match _lineIndicators' own capacity (2026-08-21) — same "lazy, never
    // shrinks, a later higher-tier life needs more slots than an earlier one built" reasoning as
    // EnsureIndicatorPoolCapacity above, just for the predicted-State-backed bearing list instead of the
    // plain indicator GameObjects. New entries seed to Vector3.zero — UpdateShootBearingTracking's own "not
    // currently tracking" sentinel, see State.shootBearingDir's own doc comment.
    private void EnsureShootBearingCapacity(ref State state, int maxCount)
    {
        while (state.shootBearingDir.Count < maxCount)
            state.shootBearingDir.Add(Vector3.zero);
    }

    protected override State GetInitialState()
    {
        var s = base.GetInitialState();
        s.shootBearingDir = DisposableList<Vector3>.Create(0);
        s.innerHeld  = DisposableList<PredictedObjectID>.Create(innerCount);
        s.middleHeld = DisposableList<PredictedObjectID>.Create(middleCount);
        s.outerHeld  = DisposableList<PredictedObjectID>.Create(outerCount);
        s.persistentHeld = DisposableList<PredictedObjectID>.Create(0);
        s.persistentTargetId = DisposableList<PlayerID>.Create(0);
        s.persistentBearingDir = DisposableList<Vector3>.Create(0);
        return s;
    }

    // ── Ring helpers ─────────────────────────────────────────────────────────
    // No boss-specific enrage behavior anymore (2026-08-20) — this boss just uses EnemyBasePD's fully
    // generic Move Speed + Damage enrage system as-is (see that class's own "Enrage" Inspector header),
    // same as every other boss. Held-pleb damage overrides multiply by EnrageDamageMultiplier explicitly
    // wherever they compute damage separately from GetOwnDamage().
    private int RingCount(int ring) => ring switch { 0 => innerCount, 1 => middleCount, _ => outerCount };
    private float RingRadius(int ring)       => ring switch { 0 => innerOrbitRadius, 1 => middleOrbitRadius, _ => outerOrbitRadius };
    // Middle ring runs counter to inner/outer — negated here (not on the Inspector field itself, which stays
    // a plain positive "how fast" value like its siblings) so every caller automatically gets the correctly
    // signed speed without needing to remember to flip it. WrapOrbitAngle's own double-mod handles a negative
    // angularSpeedDegPerSec correctly (wraps into [0,360) either direction), so this is the only change
    // needed — LateSimulate()'s shared orbit math doesn't care about sign otherwise.
    private float RingAngularSpeed(int ring) => ring switch { 0 => innerOrbitAngularSpeed, 1 => -middleOrbitAngularSpeed, _ => outerOrbitAngularSpeed };

    // Shooting goes outer->middle->inner (opposite order from pull's inner->middle->outer, per design).
    private static int ShootPhysicalRing(int shootRingIndex) => 2 - shootRingIndex;

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
            // ITS OWN last dispatched pleb has had time to finish pulling in (RingGlobalStart(R) + that
            // ring's own count - 1 slots' worth of windup, then pullDuration on top), regardless of how far
            // dispatch has already moved on to later rings. Inner/middle/outer each release independently on
            // their own schedule — never gated on the other two.
            //
            // See BeginPull/EndPullingRing/AdvanceDispatchRing for the exact mechanics, and
            // UpdateLineIndicators for how the same per-slot window math drives each indicator independently.
            case KingOrbiterPhase.Pulling:
            {
                state.phaseTimer += delta;

                // Dispatch — while loop, not if, at each level: guards against a single tick's delta
                // covering more than one scheduled dispatch (a brief stall), so a pull can never be silently
                // skipped, same convention RogueSpinAbilityPD's own hit-scheduling loop uses. The OUTER while
                // is what lets dispatch cascade straight through an empty ring (0 held) or across a ring
                // boundary within the very same tick, with no wasted frame.
                while (state.pullDispatchRingIndex <= 2)
                {
                    int dispatchCount = RingHeldList(ref state, state.pullDispatchRingIndex).Count;

                    while (state.pullRingLocalIndex < dispatchCount &&
                           state.phaseTimer >= state.pullSequenceIndex * pullSequentialDelay + windupDuration)
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
                // still hold stale data from a previous life.
                while (state.pullRingIndex <= 2 && state.pullRingIndex <= state.pullDispatchRingIndex)
                {
                    int ring = state.pullRingIndex;
                    int ringCount = RingHeldList(ref state, ring).Count;

                    if (ringCount == 0)
                    {
                        // Whiffed selection (no candidates in range) — nothing to release, advance immediately.
                        state.pullRingIndex++;
                        continue;
                    }

                    int ringLastGlobalIndex = RingGlobalStart(ref state, ring) + ringCount - 1;
                    float ringReleaseTime = ringLastGlobalIndex * pullSequentialDelay + windupDuration + pullDuration;
                    if (state.phaseTimer < ringReleaseTime) break;

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
            // next ring's target), just LaunchAlong instead of OrbitTarget, and shoot ORDER instead of
            // pull order (outer -> middle -> inner, via ShootPhysicalRing). phaseTimer counts up
            // continuously across the WHOLE Shooting Phase now. DISPATCH (shootDispatchRingIndex/
            // shootRingLocalIndex/shootSequenceIndex) advances into the next ring — picking its target,
            // AdvanceShootDispatchRing — the instant the current ring's own last pleb is launched, no gap.
            // RELEASE (shootRingIndex) stays fully independent per ring, same as Pull's own release loop —
            // see EndShotRing/AdvanceShootDispatchRing/ShootRingGlobalStart for the exact mechanics.
            case KingOrbiterPhase.ShootFlight:
            {
                state.phaseTimer += delta;

                // Updates every currently-open slot's own tracked bearing BEFORE dispatch below can consume
                // it this same tick — see UpdateShootBearingTracking's own doc comment.
                UpdateShootBearingTracking(ref state, delta);

                while (state.shootDispatchRingIndex <= 2)
                {
                    int physicalDispatchRing = ShootPhysicalRing(state.shootDispatchRingIndex);
                    int dispatchCount = RingHeldList(ref state, physicalDispatchRing).Count;

                    while (state.shootRingLocalIndex < dispatchCount &&
                           state.phaseTimer >= state.shootSequenceIndex * shootSequentialDelay + shotWindupDuration)
                    {
                        FireShotRing(ref state, physicalDispatchRing, state.shootRingLocalIndex);
                        state.shootRingLocalIndex++;
                        state.shootSequenceIndex++;
                    }

                    if (state.shootRingLocalIndex < dispatchCount) break; // this ring still has plebs waiting their own turn
                    if (!AdvanceShootDispatchRing(ref state)) break;      // ring 2 (inner, last in shoot order) just finished too
                }

                while (state.shootRingIndex <= 2 && state.shootRingIndex <= state.shootDispatchRingIndex)
                {
                    int shootOrderIdx = state.shootRingIndex;
                    int physicalRing = ShootPhysicalRing(shootOrderIdx);
                    int ringCount = RingHeldList(ref state, physicalRing).Count;

                    if (ringCount == 0)
                    {
                        // Whiffed/aborted/never-held ring — nothing to release, advance immediately.
                        state.shootRingIndex++;
                        continue;
                    }

                    int ringLastGlobalIndex = ShootRingGlobalStart(ref state, shootOrderIdx) + ringCount - 1;
                    float ringReleaseTime = ringLastGlobalIndex * shootSequentialDelay + shotWindupDuration
                        + shootingRange / Mathf.Max(0.0001f, shotSpeed);
                    if (state.phaseTimer < ringReleaseTime) break;

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
    // been selected/populated by the time this is called). Used to compute exactly when a given ring's own
    // LAST pleb had its windup dispatched, and therefore when its own pullDuration finishes — see the
    // release loop in HandleActiveState's Pulling case.
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

        int grabCount = Mathf.Min(RingCount(ring), _grabCandidates.Count);
        for (int i = 0; i < grabCount; i++)
        {
            var chosen = _grabCandidates[i];
            held.Add(chosen.ObjectId);
            chosen.SetKinematic(true);
            chosen.SetMovementDisabled(true);
            chosen.SetPhysicsColliderDisabled(true);
            chosen.SetHurtboxDisabled(true);
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
            enemy.OrbitTarget(ObjectId, transform.position, RingRadius(ring), pullDuration, RingAngularSpeed(ring), SlotAngleDeg(i, count));
    }

    // Called once every held pleb in ring state.pullRingIndex has been dispatched (BeginPull) AND the last
    // one's own pullDuration has elapsed — pull -> orbit damage multiplier switch, mirrors OrbiterGloomPD's
    // own Pulling->Holding transition. Caller (HandleActiveState's Pulling case) advances state.pullRingIndex
    // itself afterward — this no longer does that, or the old "advance to next ring/finish" tail, itself
    // (2026-08-18, see AdvanceDispatchRing/FinishPullPhase for where those now live) — dispatch and release
    // advance completely independently now, so there's no single shared "did we just finish the LAST ring"
    // question this method could answer on its own anymore.
    private void EndPullingRing(ref State state)
    {
        int ring = state.pullRingIndex;
        // EnrageDamageMultiplier multiplied in explicitly — see TryGrabRing's own identical comment.
        ApplyDamageOverrideToRing(ref state, ring, damage * orbitDamageMultiplier * EnrageDamageMultiplier);

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
            state.attackCooldownTimer = attackCooldown;
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
    private void FireKnockback(ref State state)
    {
        Vector3 origin = transform.position;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead) continue;
            Vector3 toPlayer = stats.transform.position - origin;
            if (toPlayer.magnitude > knockbackRadius) continue;

            var controller = stats.GetComponent<PlayerControllerPD>();
            if (controller == null) continue;
            Vector3 dir = toPlayer.sqrMagnitude > 0.0001f ? toPlayer.normalized : Vector3.forward;
            controller.Knockback(dir * knockbackForce);
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
    // dispatched, OR the phase aborts because no valid targets remain anywhere.
    //
    // Abort ("if no valid targets remain, release held spikies and skip the rest of this phase" — read as
    // the whole Shooting Phase, not just this ring) only ever releases rings that HAVEN'T been dispatched
    // into yet (this one, plus every remaining one) — safe, since none of them have launched anything yet to
    // interrupt mid-flight. A ring that's ALREADY dispatched (possibly still flying from an earlier tick)
    // is left completely alone and keeps releasing on its own normal schedule via the release loop, same as
    // any other ring — the abort only stops FUTURE ring selection, it doesn't reach backward.
    private bool AdvanceShootDispatchRing(ref State state)
    {
        state.shootDispatchRingIndex++;
        state.shootRingLocalIndex = 0;
        if (state.shootDispatchRingIndex > 2) return false;

        int ring = ShootPhysicalRing(state.shootDispatchRingIndex);
        int heldCount = RingHeldList(ref state, ring).Count;
        // Resets this ring's own slot range to Vector3.zero (2026-08-21) — clears whatever the PREVIOUS ring
        // left in these same physical slot indices, so this ring's own plebs seed their tracked bearing
        // fresh from live direction the moment their own window opens, not a stale leftover direction. See
        // UpdateShootBearingTracking's own doc comment.
        for (int i = 0; i < heldCount && i < state.shootBearingDir.Count; i++)
            state.shootBearingDir[i] = Vector3.zero;
        if (heldCount == 0) return true;

        if (!CharacterStatsPD.AnyValidTargetExists)
        {
            for (int r = state.shootDispatchRingIndex; r <= 2; r++)
                ReleaseRing(ref state, ShootPhysicalRing(r));
            state.shootDispatchRingIndex = 3;
            return false;
        }

        PickShotTarget(ref state);
        // EnrageDamageMultiplier multiplied in explicitly — see TryGrabRing's own identical comment.
        ApplyDamageOverrideToRing(ref state, ring, damage * shootingDamageMultiplier * EnrageDamageMultiplier);
        return true;
    }

    // Updates the tracked bearing (state.shootBearingDir) of every pleb currently inside its OWN windup
    // window in the CURRENT dispatch ring — called once per tick, before dispatch itself, from
    // HandleActiveState's ShootFlight case (2026-08-21). Seeds a slot fresh from the LIVE target direction
    // the instant its own window opens (Vector3.zero sentinel — see State.shootBearingDir's own doc comment),
    // then rate-caps every subsequent tick via Vector3.RotateTowards at rotationRateDegPerSecond (converted
    // to radians/sec — RotateTowards' maxRadiansDelta param). This is what makes circling this boss (or
    // timing a dash to arrive late in a pleb's own windup) a real counterplay option — an unbounded instant
    // snap (the old behavior, computed fresh at the literal instant of launch) meant no amount of player
    // movement could ever throw off where a shot actually fired. Both FireShotRing (the real launch) and
    // UpdateLineIndicators (the windup indicator's own aim) read this SAME tracked value, so what's shown
    // during the windup is always exactly what fires — never a cosmetic-only lag that the real shot ignores.
    //
    // Vector3.RotateTowards, not Mathf.MoveTowardsAngle (2026-08-24 fix) — see State.shootBearingDir's own
    // doc comment for the interpolation-jitter bug this replaced (a raw degree float interpolates violently
    // across its own wraparound boundary once handed to viewState for rendering; a unit direction Vector3
    // doesn't have that discontinuity). maxMagnitudeDelta: 0f keeps the vector's length fixed at whatever it
    // already was (always 1, since every value that ever gets stored here — the seed included — is already
    // normalized), only rotating its direction.
    private void UpdateShootBearingTracking(ref State state, float delta)
    {
        if (state.shootDispatchRingIndex > 2) return; // dispatch already fully done, nothing pending to track
        if (_shotTargetSnapshot == null) return;

        int physicalDispatchRing = ShootPhysicalRing(state.shootDispatchRingIndex);
        var held = RingHeldList(ref state, physicalDispatchRing);
        int dispatchCount = held.Count;
        int ringGlobalStart = ShootRingGlobalStart(ref state, state.shootDispatchRingIndex);

        for (int i = state.shootRingLocalIndex; i < dispatchCount && i < state.shootBearingDir.Count; i++)
        {
            float localElapsed = state.phaseTimer - (ringGlobalStart + i) * shootSequentialDelay;
            if (localElapsed < 0f || localElapsed >= shotWindupDuration) continue; // not this slot's window yet, or already past it (about to dispatch this same tick)

            var go = hierarchy.GetGameObject(held[i]);
            if (go == null) continue;

            Vector3 toTarget = _shotTargetSnapshot.transform.position - go.transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) continue;
            Vector3 targetDir = toTarget.normalized;

            Vector3 current = state.shootBearingDir[i];
            state.shootBearingDir[i] = current.sqrMagnitude < 0.0001f
                ? targetDir
                : Vector3.RotateTowards(current, targetDir, rotationRateDegPerSecond * Mathf.Deg2Rad * delta, 0f);
        }
    }

    // Global dispatch index the given shoot-order ring's own FIRST pleb occupies — ShootRingGlobalStart's
    // own Pull-side equivalent (RingGlobalStart), just walking shoot order (ShootPhysicalRing) instead of
    // pull order. See RingGlobalStart's own doc comment for the full reasoning.
    private int ShootRingGlobalStart(ref State state, int shootOrderIndex)
    {
        int start = 0;
        for (int r = 0; r < shootOrderIndex; r++)
            start += RingHeldList(ref state, ShootPhysicalRing(r)).Count;
        return start;
    }

    // Called once the release loop in HandleActiveState's ShootFlight case confirms every ring (0..2, shoot
    // order) has been released (or skipped/aborted) — full attack cycle complete.
    private void FinishShootPhase(ref State state)
    {
        state.attackCooldownTimer = attackCooldown;
        FindRandomTarget();
        state.phaseId = (int)KingOrbiterPhase.Moving;

        // Was only ever cleared on a full respawn/death reset, never at the natural end of an ordinary
        // Shoot Phase — left a stale CharacterStatsPD reference sitting in this plain (non-State) field for
        // the rest of Moving/Pulling/Orbit/KnockbackWindup, until the NEXT Shoot Phase's own PickShotTarget
        // call overwrote it. Harmless on its own (UpdateLineIndicators' shotActive gate also requires
        // phase == ShootFlight), but a real gap if a FUTURE Shoot Phase ever whiffs on every ring (no held
        // plebs anywhere, so PickShotTarget never fires) — this would otherwise let that cycle silently reuse
        // whichever player was targeted several attack cycles ago instead of correctly having no target.
        _shotTargetSnapshot = null;
    }

    // Picks one random currently-valid player, excluding state.lastShotTargetId when more than one candidate
    // qualifies ("unique from the last target if possible") — falls back to including it anyway if excluding
    // it would leave nobody. Stores into state.lastShotTargetId (for the NEXT ring's own exclusion) and the
    // plain _shotTargetSnapshot field UpdateView reads for indicator aim.
    private void PickShotTarget(ref State state)
    {
        BuildShotCandidates(ref state, excludeLast: true);
        if (_shotTargetScratch.Count == 0) BuildShotCandidates(ref state, excludeLast: false);
        if (_shotTargetScratch.Count == 0) return; // guarded by AnyValidTargetExists at the call site

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
    // (state.shootBearingDir[i] — see UpdateShootBearingTracking's own doc comment) rather than a fresh live
    // read (2026-08-21 rework) — this is what makes the rate cap actually bite on the real shot, not just
    // the windup indicator's own cosmetic aim. Falls back to a fresh live direction only if the tracked
    // bearing was somehow never seeded (shouldn't normally happen — UpdateShootBearingTracking runs every
    // tick this slot's window is open, before dispatch can ever consume it the same tick). Travels the full
    // shootingRange regardless of what it hits along the way. Called by ShootFlight's own case handler in
    // HandleActiveState the instant that pleb's windup window closes (see its own comment for the full
    // timing model) — exactly BeginPull's own sequencing shape, just LaunchAlong instead of OrbitTarget.
    // ring is passed explicitly (2026-08-18, used to read state.shootRingIndex directly) — dispatch and
    // release now track separate ring indices (shootDispatchRingIndex vs. shootRingIndex), so this needs the
    // CALLER's own dispatch-side ring. Order within the ring doesn't matter (per explicit design).
    private void FireShotRing(ref State state, int ring, int i)
    {
        var list = RingHeldList(ref state, ring);
        var go = hierarchy.GetGameObject(list[i]);
        if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) return;

        Vector3 dir;
        Vector3 tracked = i < state.shootBearingDir.Count ? state.shootBearingDir[i] : Vector3.zero;
        if (tracked.sqrMagnitude >= 0.0001f)
        {
            dir = tracked;
        }
        else
        {
            Vector3 targetPos = _shotTargetSnapshot != null ? _shotTargetSnapshot.transform.position : transform.position;
            Vector3 toTarget = targetPos - go.transform.position;
            toTarget.y = 0f;
            dir = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;
        }

        enemy.LaunchAlong(dir, shootingRange, shotSpeed);
    }

    // Called once every held pleb in ring state.shootRingIndex has been dispatched (FireShotRing) AND the
    // last one's own flight time has elapsed — full release, mirrors OrbiterGloomPD.ReleaseAllHeld's
    // per-pleb body. Caller (HandleActiveState's ShootFlight case) advances state.shootRingIndex itself
    // afterward — this no longer does that, or the old "advance to next ring/finish" tail, itself
    // (2026-08-18, see AdvanceShootDispatchRing/FinishShootPhase for where those now live).
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
        ReleasePersistentHeld(ref currentState);
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
            state.persistentCooldownTimer = persistentShotInterval;
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
            // Vector3, not a raw degree float (2026-08-24 fix) — see State.shootBearingDir's own doc comment
            // for the interpolation-wraparound jitter bug this avoids. Vector3.forward fallback (not zero —
            // this list has no "not yet tracking" sentinel meaning; every slot here is already grabbed and
            // committed the instant it's added) for the degenerate on-top-of-target case.
            state.persistentBearingDir.Add(toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.forward);
        }

        state.persistentVolleyActive = state.persistentHeld.Count > 0;
        state.persistentShotFired    = false;
        state.persistentVolleyTimer  = 0f;
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
    // _shotTargetSnapshot. A target that dies/vanishes mid-windup just stops updating that slot's bearing —
    // it still fires along its last tracked direction rather than aborting.
    private void UpdatePersistentBearingTracking(ref State state, float delta)
    {
        for (int i = 0; i < state.persistentHeld.Count; i++)
        {
            if (!CharacterStatsPD.All.TryGetValue(state.persistentTargetId[i], out var target)) continue;
            if (target.IsDead || (target.IsInvisible && !target.IsTaunting)) continue;

            var go = hierarchy.GetGameObject(state.persistentHeld[i]);
            if (go == null) continue;

            Vector3 toTarget = target.transform.position - go.transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) continue;
            Vector3 targetDir = toTarget.normalized;

            state.persistentBearingDir[i] = Vector3.RotateTowards(state.persistentBearingDir[i], targetDir, rotationRateDegPerSecond * Mathf.Deg2Rad * delta, 0f);
        }
    }

    // Reads each slot's own tracked bearing (rate-capped by UpdatePersistentBearingTracking above) — same
    // "indicator and real shot read the identical value" contract FireShotRing's own doc comment establishes
    // for the real Shooting Phase (see UpdatePersistentLineIndicators below).
    private void FirePersistentVolley(ref State state)
    {
        for (int i = 0; i < state.persistentHeld.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.persistentHeld[i]);
            if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) continue;

            enemy.LaunchAlong(state.persistentBearingDir[i], shootingRange, shotSpeed);
        }
        state.persistentShotFired   = true;
        state.persistentVolleyTimer = 0f;
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
        state.persistentHeld.Clear();
        state.persistentTargetId.Clear();
        state.persistentBearingDir.Clear();
    }

    // ── View ─────────────────────────────────────────────────────────────────

    private KingOrbiterPhase _lastVisualState = (KingOrbiterPhase)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);
        if (!gameObject.activeInHierarchy) return;

        if (knockbackIndicator != null) IndicatorMath.ApplyRadialTransform(knockbackIndicator.transform, knockbackRadius);

        var phase = GetPhase(ref viewState);
        UpdateLineIndicators(viewState, phase);
        // Independent of `phase`/`active` above — Persistent Pleb Shots run regardless of phaseId, and can
        // be visible at the same time as a real Shooting Phase's own indicators (separate pool, see
        // persistentLineIndicatorTemplate's own doc comment).
        UpdatePersistentLineIndicators(viewState);

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
        if (knockbackFill != null && phase != KingOrbiterPhase.KnockbackWindup) knockbackFill.ResetFill();
    }

    // Shared pool for both windups (see _lineIndicators' own doc comment — Pulling/ShootFlight never
    // overlap). Pull: near edge = this ring's orbit slot (radius/SlotAngleDeg offset), far edge = the live
    // pleb position — same math/reasoning as OrbiterGloomPD.UpdateView's own windup indicators. Shoot: near
    // edge = each pleb's own LIVE position (not a slot — they're not being pulled anywhere), far edge = that
    // position + a FIXED shootingRange span in the aimed direction (unlike the pull shape's live-distance
    // span) — individually aimed per pleb, all converging on the one shared _shotTargetSnapshot.
    //
    // Per-slot windup window, not phase-level (2026-08-17 — used to be one shared windup for the whole
    // ring) — slot i shows its own indicator only while viewState.phaseTimer (elapsed since Pulling/
    // ShootFlight began — see each phase's own comment in HandleActiveState) falls within ITS OWN
    // [i*delay, i*delay+fillDuration) window, independent of every other slot's own window (they overlap).
    private void UpdateLineIndicators(State viewState, KingOrbiterPhase phase)
    {
        if (_lineIndicators == null) return;

        // Dispatch (pullDispatchRingIndex/shootDispatchRingIndex) can fully finish — reaching 3, one past the
        // last real ring index 2 — several ticks before Release actually catches up and ends the phase (see
        // this method's own class-level doc comment: dispatch always runs ahead of release). RingHeldList has
        // no `case 3`, so ring=3 (Pull) silently fell through to its `default` branch and reused outerHeld a
        // SECOND time, and ShootPhysicalRing(3) == -1 hit that exact same `default` branch for Shoot — so
        // during that gap this used to compute a phantom extra "ring" off the real outer ring's already-held
        // plebs, replaying a fake staggered wind-up sequence. That's the confirmed (2026-08-21, reproduces in
        // solo — not a misprediction artifact) root cause of the reported "flash of indicators right after
        // Pull/right as Orbit starts" bug. Fix: only treat a phase as actively dispatching while its own
        // dispatch ring index is still in the real [0,2] range.
        bool pullActive = phase == KingOrbiterPhase.Pulling && viewState.pullDispatchRingIndex <= 2;
        bool shotActive = phase == KingOrbiterPhase.ShootFlight && viewState.shootDispatchRingIndex <= 2 && _shotTargetSnapshot != null;
        bool active     = pullActive || shotActive;

        // Both track DISPATCH progress (pullDispatchRingIndex/shootDispatchRingIndex), not release progress
        // (pullRingIndex/shootRingIndex) — 2026-08-18, these can now differ (dispatch runs ahead of
        // release) for both phases. ringGlobalStart converts a slot's own LOCAL index (i, matching its
        // position in `held`) into the GLOBAL dispatch index phaseTimer is actually measured against, since
        // phaseTimer no longer resets per ring for EITHER phase (see the Pulling/ShootFlight cases' own doc
        // comments in HandleActiveState).
        int     shootDispatchOrderIdx = viewState.shootDispatchRingIndex;
        int     ring          = pullActive ? viewState.pullDispatchRingIndex : ShootPhysicalRing(shootDispatchOrderIdx);
        var     held          = active ? RingHeldList(ref viewState, ring) : default;
        int     heldCount     = active ? held.Count : 0;
        Vector3 shotTargetPos = shotActive ? _shotTargetSnapshot.transform.position : default;
        float   fillDuration  = pullActive ? windupDuration : shotWindupDuration;
        float   slotDelay     = pullActive ? pullSequentialDelay : shootSequentialDelay;
        int     ringGlobalStart = pullActive ? RingGlobalStart(ref viewState, ring)
            : shotActive ? ShootRingGlobalStart(ref viewState, shootDispatchOrderIdx) : 0;

        for (int i = 0; i < _lineIndicators.Length; i++)
        {
            var indicator = _lineIndicators[i];
            if (indicator == null) continue;

            float localElapsed = viewState.phaseTimer - (ringGlobalStart + i) * slotDelay;
            bool slotFilled = active && i < heldCount && localElapsed >= 0f && localElapsed < fillDuration;
            Vector3 plebPos = default;
            if (slotFilled)
            {
                var go = hierarchy.GetGameObject(held[i]);
                slotFilled = go != null;
                if (slotFilled) plebPos = go.transform.position;
            }

            var fill = _lineFills != null && i < _lineFills.Length ? _lineFills[i] : null;
            // Captured BEFORE the SetActive toggle below — see the ShootFlight branch's own render-smoothing
            // comment for why this matters (a freshly-activated indicator must snap instantly, not smooth in
            // from whatever stale rotation it was left at by its LAST use).
            bool justActivated = slotFilled && !indicator.gameObject.activeSelf;
            if (indicator.gameObject.activeSelf != slotFilled)
            {
                indicator.gameObject.SetActive(slotFilled);
                if (fill != null && !slotFilled) fill.ResetFill();
            }
            if (!slotFilled) continue;

            // Live, every frame, off this slot's own localElapsed — see feedback_windupfill_self_timed_drift:
            // a self-timed StartFill() runs on its own Time.deltaTime clock, independent of the tick-based
            // phaseTimer that actually gates BeginPull()/FireShotRing(), and can drift out of sync with it.
            if (fill != null)
                fill.SetProgress(Mathf.Clamp01(localElapsed / fillDuration));

            Vector3 nearEdge, aim;
            float span;
            if (pullActive)
            {
                float   rad = SlotAngleDeg(i, heldCount) * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                Vector3 slotPos = transform.position + dir * RingRadius(ring);

                Vector3 toTarget = plebPos - slotPos;
                toTarget.y = 0f;
                float distance = toTarget.magnitude;
                if (distance <= 0.001f) continue;
                nearEdge = slotPos;
                aim      = toTarget / distance;
                span     = distance;
            }
            else
            {
                // Reads the SAME tracked bearing FireShotRing's own real launch reads (2026-08-21 —
                // UpdateShootBearingTracking, see its own doc comment) rather than recomputing a fresh live
                // direction here — so what this indicator shows is always exactly what fires, never a
                // cosmetic-only preview the rate cap doesn't actually apply to. Falls back to a fresh live
                // direction only if the tracked bearing was somehow never seeded yet (matches FireShotRing's
                // own identical fallback).
                nearEdge = plebPos;
                Vector3 tracked = i < viewState.shootBearingDir.Count ? viewState.shootBearingDir[i] : Vector3.zero;
                if (tracked.sqrMagnitude >= 0.0001f)
                {
                    aim = tracked;
                }
                else
                {
                    Vector3 toTarget = shotTargetPos - plebPos;
                    toTarget.y = 0f;
                    aim = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;
                }

                // Render-frame smoothing (2026-08-24 fix) — `aim` above came straight out of viewState's own
                // shootBearingDir list. Unlike a plain top-level State field (which PurrDiction smoothly
                // interpolates every render frame — see e.g. RemoteSpikeBurstPD's own chargeTimer comment),
                // this pooled indicator is a freestanding Transform, not a PredictedIdentity of its own — it
                // has no PredictedTransform/LocalGraphicsSmoother component riding the underlying value, so
                // whatever cadence the raw DisposableList value actually updates at (this codebase's other
                // per-slot list-backed tracking — KingOrbiterPD's own bearing lists — isn't confirmed to get
                // the same smooth per-render-frame treatment plain fields do) shows up directly as visible
                // stepping on a long indicator line, where even a small angular step sweeps a large
                // screen-space distance at the far end. Blending the indicator's own CURRENTLY-DISPLAYED
                // direction toward `aim` here, fast enough to be visually indistinguishable from instant but
                // smooth enough to never show a discrete jump, fixes that regardless of the underlying cause
                // — same role Unity's own transform interpolation plays for every OTHER indicator in this
                // codebase (King Spike's charge/spike-burst indicators inherit it for free by being parented
                // under an already-smoothed body; this boss's own Pull-phase indicators above recompute fresh
                // from already-smooth live transform reads every frame instead of a raw State value at all).
                // justActivated skips the blend entirely on the tick the indicator turns on — smoothing FROM
                // whatever rotation it was left at by its LAST use would show a wrong, unrelated sweep instead
                // of the fix this is meant to be.
                const float indicatorSmoothDegPerSecond = 720f;
                aim = justActivated
                    ? aim
                    : Vector3.RotateTowards(indicator.forward, aim, indicatorSmoothDegPerSecond * Mathf.Deg2Rad * Time.deltaTime, 0f);

                span = shootingRange;
            }

            indicator.SetPositionAndRotation(nearEdge + aim * (span * 0.5f), Quaternion.LookRotation(aim));
            var scale = indicator.localScale;
            scale.z = span * 0.1f;
            indicator.localScale = scale;
        }
    }

    // Own dedicated pool (see persistentLineIndicatorTemplate's own doc comment) — active only during a
    // volley's own windup (persistentVolleyActive && !persistentShotFired); hides once fired (the flight
    // itself has no indicator, matching the real Shooting Phase's own "indicator only during windup" shape).
    // Near edge = each grabbed pleb's own live position, far edge = a fixed shootingRange span along its
    // tracked bearing — same math/Vector3-not-degrees reasoning as UpdateLineIndicators' own ShootFlight
    // branch, just reading persistentBearingDir instead of shootBearingDir (no zero-vector fallback needed
    // in practice here — StartPersistentVolley always seeds a real direction the instant a slot is
    // populated, never leaves it unset while held — the sqrMagnitude check below is defensive only).
    private void UpdatePersistentLineIndicators(State viewState)
    {
        if (_persistentLineIndicators == null) return;

        bool windingUp = viewState.persistentVolleyActive && !viewState.persistentShotFired;

        for (int i = 0; i < _persistentLineIndicators.Length; i++)
        {
            var indicator = _persistentLineIndicators[i];
            if (indicator == null) continue;

            bool slotFilled = windingUp && i < viewState.persistentHeld.Count;
            Vector3 plebPos = default;
            if (slotFilled)
            {
                var go = hierarchy.GetGameObject(viewState.persistentHeld[i]);
                slotFilled = go != null;
                if (slotFilled) plebPos = go.transform.position;
            }

            var fill = _persistentLineFills != null && i < _persistentLineFills.Length ? _persistentLineFills[i] : null;
            // Captured BEFORE the SetActive toggle — see UpdateLineIndicators' own render-smoothing comment
            // for why a freshly-activated indicator must snap instantly rather than smooth in from a stale
            // rotation.
            bool justActivated = slotFilled && !indicator.gameObject.activeSelf;
            if (indicator.gameObject.activeSelf != slotFilled)
            {
                indicator.gameObject.SetActive(slotFilled);
                if (fill != null && !slotFilled) fill.ResetFill();
            }
            if (!slotFilled) continue;

            if (fill != null)
                fill.SetProgress(Mathf.Clamp01(viewState.persistentVolleyTimer / shotWindupDuration));

            Vector3 aim = i < viewState.persistentBearingDir.Count ? viewState.persistentBearingDir[i] : Vector3.forward;
            if (aim.sqrMagnitude < 0.0001f) aim = Vector3.forward; // defensive only — StartPersistentVolley never seeds zero

            // Render-frame smoothing, same fix/reasoning as UpdateLineIndicators' own ShootFlight branch.
            const float indicatorSmoothDegPerSecond = 720f;
            aim = justActivated
                ? aim
                : Vector3.RotateTowards(indicator.forward, aim, indicatorSmoothDegPerSecond * Mathf.Deg2Rad * Time.deltaTime, 0f);

            float span = shootingRange;

            indicator.SetPositionAndRotation(plebPos + aim * (span * 0.5f), Quaternion.LookRotation(aim));
            var scale = indicator.localScale;
            scale.z = span * 0.1f;
            indicator.localScale = scale;
        }
    }

    protected override void OnDeathStop()
    {
        HideIndicatorPool(_lineIndicators, _lineFills);
        HideIndicatorPool(_persistentLineIndicators, _persistentLineFills);
        if (knockbackIndicator != null) knockbackIndicator.SetActive(false);
        if (knockbackFill != null) knockbackFill.ResetFill();
    }

    private void HideIndicatorPool(Transform[] indicators, WindupFillPD[] fills)
    {
        if (indicators != null)
            foreach (var t in indicators) if (t != null) t.gameObject.SetActive(false);
        if (fills != null)
            foreach (var f in fills) if (f != null) f.ResetFill();
    }

    protected override void OnRespawnReset()
    {
        currentState.phaseId             = 0;
        currentState.phaseTimer          = 0f;
        // Full cooldown, matching LateAwake()'s own reasoning — same "field needs time to repopulate" logic
        // applies to a respawn (e.g. a difficulty-pacing wipe right before this boss spawns) as to a fresh
        // spawn.
        currentState.attackCooldownTimer = attackCooldown;
        currentState.pullRingIndex          = 0;
        currentState.pullDispatchRingIndex  = 0;
        currentState.pullRingLocalIndex     = 0;
        currentState.shootRingIndex         = 0;
        currentState.shootDispatchRingIndex = 0;
        currentState.shootRingLocalIndex    = 0;
        currentState.pullSequenceIndex      = 0;
        currentState.shootSequenceIndex     = 0;
        currentState.lastShotTargetId    = default;
        currentState.innerHeld.Clear();
        currentState.middleHeld.Clear();
        currentState.outerHeld.Clear();
        // Full grace, matching LateAwake()'s own reasoning for persistentCooldownTimer. persistentHeld/
        // persistentTargetId/persistentBearingDir cleared directly (not via ReleasePersistentHeld) — same
        // reasoning as innerHeld/middleHeld/outerHeld above: Die() already released anything actually held
        // before this pooled reuse could happen.
        currentState.persistentCooldownTimer = persistentShotInterval;
        currentState.persistentVolleyActive  = false;
        currentState.persistentShotFired     = false;
        currentState.persistentVolleyTimer   = 0f;
        currentState.persistentHeld.Clear();
        currentState.persistentTargetId.Clear();
        currentState.persistentBearingDir.Clear();
        OnDeathStop();
        // In case death/pooling happened mid-windup, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (phaseId resets to Moving above), but resetting explicitly
        // here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState    = (KingOrbiterPhase)(-1);
        _shotTargetSnapshot = null;
    }
}
