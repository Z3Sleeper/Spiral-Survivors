using System.Collections.Generic;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Boss. Same core loop as OrbiterGloomPD (2026-10-20 rework, see that class's own doc comment) — spawns its
// own self-owned OrbiterOrbPD hazard orbs instead of grabbing live Pleb enemies — extended into a 5-phase
// cycle with three concurrent rings (inner/middle/outer, each its own radius/speed/count) plus a Knockback
// and Shoot phase layered on top: Moving -> Spawning -> Orbiting -> KnockbackWindup -> ShootFlight -> repeat.
// Spawning spawns every ring's orbs SIMULTANEOUSLY (no staggered per-orb dispatch, unlike the old grab-based
// Pulling phase) and rises them in place over one shared spawnDuration window, same as Gloom. Orbiting chases
// players with all three rings spinning. KnockbackWindup freezes every active orb in place (via
// OrbiterOrbPD.SetKnockbackFrozen — motion-only, no tint/damage change, unlike a genuine stun's SetStunned)
// and radially knocks back nearby players.
// ShootFlight then fires each ring's orbs (inner -> middle -> outer) at one shared random target per ring,
// staggered one at a time via the new OrbiterOrbPD.BeginFlight — this phase's own dispatch/release/aim-
// tracking/line-indicator machinery is otherwise UNCHANGED from the old grab-based version, just firing orbs
// instead of launching held plebs (see each method's own doc comment for what's actually different).
//
// Persistent Orb Shots (independent of phaseId, same as the old Persistent Pleb Shots) spawns one dedicated
// orb per currently-targeted player directly — no more "find a nearby live pleb" search — at a distance
// randomized between persistentSpawnRadiusMultiplierMin/Max times the outer ring's own radius, facing that
// player, then fires it the same way a ShootFlight orb fires.
//
// All of the old generation-check/hijack-guard machinery (innerHeldGeneration/persistentHeldGeneration/etc.,
// and the Die()/OnHiddenStateChanged misprediction-revert reclaim snapshot) is gone — self-owned orbs can't
// be silently reassigned the way an externally-pooled pleb could, so there's nothing left to guard against.
public class KingOrbiterPD : EnemyBasePD<KingOrbiterPD.State>
{
    public enum KingOrbiterPhase { Moving, Spawning, Orbiting, KnockbackWindup, ShootFlight }

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   phaseId;
        // Dual-purpose, per phase (same convention OrbiterGloomPD's own phaseTimer uses): counts UP while
        // Spawning (elapsed since the spawn/windup began, against spawnDuration) and while ShootFlight
        // (elapsed since the whole Shooting Phase began, continuously across all 3 rings — see ShootFlight's
        // own case doc comment in HandleActiveState for the dispatch/release timing model this drives);
        // counts DOWN while Orbiting/KnockbackWindup (remaining time).
        public float phaseTimer;
        public float attackCooldownTimer;
        // Set by OnInterrupted() (only callable during Spawning — see CanBeInterrupted) — read once, at the
        // instant Spawning's own timer naturally completes, to skip straight into KnockbackWindup instead of
        // Orbiting. Deliberately does NOT despawn anything (explicit divergence from OrbiterGloomPD's own
        // OnInterrupted) — Spawning's timer keeps ticking through a resulting stun (unstunnable, same as
        // Gloom), and whatever's already spawned just continues on into Knockback once it closes.
        public bool  interruptSkipOrbiting;

        // Which ring's own RELEASE (EndShotRing) is next pending — 0=inner,1=middle,2=outer (see
        // ShootPhysicalRing). Independent of shootDispatchRingIndex below — dispatch can run ahead of
        // release, since a ring only releases once every orb it's dispatched has actually finished flying.
        public int shootRingIndex;
        // Which ring DISPATCH (FireShotRing calls) is currently filling. Always >= shootRingIndex. Advances
        // the instant the current ring's own last orb has been dispatched (see AdvanceShootDispatchRing) —
        // never waits for them to actually finish flying.
        public int shootDispatchRingIndex;
        // How many of shootDispatchRingIndex's own orbs have had FireShotRing called on them so far — reset
        // to 0 whenever shootDispatchRingIndex advances.
        public int shootRingLocalIndex;
        // GLOBAL count of orbs dispatched so far across the WHOLE Shooting Phase (inner+middle+outer
        // combined) — drives dispatch TIMING only; shootRingLocalIndex above is which orb/slot within its own
        // ring a given dispatch maps to. Reset to 0 once, at EnterShootWindupOrAdvance.
        public int shootSequenceIndex;
        // Snapshotted per-ring global-start indices, in SHOOT-ORDER (0=first dispatched this phase..2=last) —
        // written once per ring, in AdvanceShootDispatchRing, from a running tally (shootNextGlobalStart) that
        // only ever grows. Never re-derived from another ring's live orb-list count (that ring may have
        // already released and cleared its own list by the time a LATER ring needs this).
        public int shootRingGlobalStart0;
        public int shootRingGlobalStart1;
        public int shootRingGlobalStart2;
        public int shootNextGlobalStart;

        // Every orb currently spawned for THIS ring, this attack cycle — no generation-check parallel list
        // needed (see class doc comment) since these are fully owned spawned objects.
        public DisposableList<PredictedObjectID> innerOrbs;
        public DisposableList<PredictedObjectID> middleOrbs;
        public DisposableList<PredictedObjectID> outerOrbs;

        // Last ring's chosen shot target — excluded from the next ring's random pick when possible.
        public PlayerID lastShotTargetId;

        // Flat, dynamically-grown "who's currently being tracked/shown" list for ShootFlight, keyed by orb
        // identity, decoupled from ring/dispatch bookkeeping — a shot orb's own line indicator stays visible
        // for its ENTIRE movement (windup through flight) independent of every other orb, including ones from
        // a different ring active at the same time, so slot identity can't be tied to ring position. One
        // entry is appended the instant an orb's own windup window opens, removed (swap-with-last) the
        // instant that SPECIFIC orb's own flight finishes or it's otherwise gone.
        public DisposableList<PredictedObjectID> shootTrackedOrb;
        // This orb's own tracked aim DIRECTION (unit Vector3, not a raw angle — interpolating a raw angle
        // across its ±180°/0°-360° wraparound snaps through the opposite direction for one frame; a unit
        // direction has no such discontinuity). Vector3.zero means "not yet seeded". Frozen once
        // shootTrackedFired[i] is true — both the windup indicator's aim and the real BeginFlight direction
        // read this SAME value, so what's shown is always exactly what fires.
        public DisposableList<Vector3> shootTrackedBearing;
        public DisposableList<PredictedObjectID> shootTrackedIndicator;
        // True once this orb's own FireShotRing call has actually dispatched it (BeginFlight) — from then on
        // UpdateShootBearingTracking stops touching its bearing, and DespawnFinishedShootIndicators becomes
        // responsible for removing this entry once the orb itself reports IsDespawned.
        public DisposableList<bool> shootTrackedFired;
        // The exact phaseTimer value this entry's own windup window opened at — lets UpdateShootIndicatorFills
        // recompute "how far into MY OWN windup am I" without needing to know which ring/global-dispatch-index
        // this orb ever belonged to.
        public DisposableList<float> shootTrackedWindupStart;

        // Persistent Orb Shots — fires an independent volley at every non-invisible alive player on a flat
        // interval, entirely independent of phaseId (see Simulate()'s own UpdatePersistentOrbShots tail).
        // Parallel lists, one entry per currently-targeted player this volley (rebuilt fresh every
        // StartPersistentVolley call).
        public DisposableList<PredictedObjectID> persistentOrbs;
        public DisposableList<PlayerID>          persistentTargetId;
        public DisposableList<Vector3>           persistentBearingDir;
        public DisposableList<PredictedObjectID> persistentIndicatorId;
        // Each slot's own assigned target's position, recorded every tick that target is actually alive AND
        // visible — frozen otherwise, so an in-progress shot keeps rate-cap-rotating toward wherever its
        // target was last legitimately seen rather than getting stuck mid-rotation or following a dead
        // player's transform wherever it relocates post-death.
        public DisposableList<Vector3> persistentLastKnownPosition;
        public float persistentCooldownTimer;
        public bool  persistentVolleyActive;
        // True once FirePersistentVolley has actually launched this volley's orbs, so persistentVolleyTimer
        // below switches from "elapsed windup time" to "elapsed flight time".
        public bool  persistentShotFired;
        public float persistentVolleyTimer;

        // Runtime-scaled — see EnemyBaseState.scaledMaxHealth's own doc comment for why these live in State
        // rather than as a plain field mutated by OnConfigureSpawn.
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
            innerOrbs.Dispose();
            middleOrbs.Dispose();
            outerOrbs.Dispose();
            shootTrackedOrb.Dispose();
            shootTrackedBearing.Dispose();
            shootTrackedIndicator.Dispose();
            shootTrackedFired.Dispose();
            shootTrackedWindupStart.Dispose();
            persistentOrbs.Dispose();
            persistentTargetId.Dispose();
            persistentBearingDir.Dispose();
            persistentIndicatorId.Dispose();
            persistentLastKnownPosition.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.innerOrbs = innerOrbs.Duplicate();
            copy.middleOrbs = middleOrbs.Duplicate();
            copy.outerOrbs = outerOrbs.Duplicate();
            copy.shootTrackedOrb = shootTrackedOrb.Duplicate();
            copy.shootTrackedBearing = shootTrackedBearing.Duplicate();
            copy.shootTrackedIndicator = shootTrackedIndicator.Duplicate();
            copy.shootTrackedFired = shootTrackedFired.Duplicate();
            copy.shootTrackedWindupStart = shootTrackedWindupStart.Duplicate();
            copy.persistentOrbs = persistentOrbs.Duplicate();
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
    // Each ring orb's own damage while orbiting, locked in once at spawn (same convention
    // EnemyProjectilePD.Launch's own damage param uses — not live-re-read afterward).
    [SerializeField] private float orbitDamageMultiplier    = 1f;
    // Re-baked into each orb at the moment it actually fires (FireShotRing/FirePersistentVolley) — see
    // OrbiterOrbPD.BeginFlight's own damage param doc comment for why this needs its own explicit value
    // instead of reusing whatever Launch() baked in.
    [SerializeField] private float shootingDamageMultiplier = 1f;

    [Header("Spawn - Shared")]
    // The new "windup" — every ring's orbs rise in place over this duration before actually orbiting.
    // Interruptible (see CanBeInterrupted below) — explicit 1s default, same as OrbiterGloomPD.
    [SerializeField] private float spawnDuration = 1f;

    [Header("Spawn - Inner Ring")]
    [SerializeField] private int   innerCount             = 2;
    [SerializeField] private float innerOrbitRadius        = 2f;
    [SerializeField] private float innerOrbitAngularSpeed  = 120f;

    [Header("Spawn - Middle Ring")]
    [SerializeField] private int   middleCount             = 3;
    [SerializeField] private float middleOrbitRadius        = 5.5f;
    [SerializeField] private float middleOrbitAngularSpeed  = 60f;

    [Header("Spawn - Outer Ring")]
    [SerializeField] private int   outerCount             = 4;
    [SerializeField] private float outerOrbitRadius        = 9f;
    [SerializeField] private float outerOrbitAngularSpeed  = 30f;

    [Header("Orbiting")]
    [SerializeField] private float orbitDuration = 10f;

    [Header("Knockback")]
    [SerializeField] private float        knockbackChargeTime = 1.5f;
    [SerializeField] private float        knockbackRadius     = 12f;
    [SerializeField] private float        knockbackForce      = 150f;
    [SerializeField] private GameObject   knockbackIndicator;
    [SerializeField] private WindupFillPD knockbackFill;

    [Header("Shooting")]
    [SerializeField] private float shootingRange      = 40f;
    [SerializeField] private float shotWindupDuration = 1f;
    [SerializeField] private float shotSpeed          = 50f;
    // Gap between each ring's own orb dispatch during ShootFlight — see FireShotRing's own doc comment.
    // Tier1-authored baseline, scaled per tier by shootSequentialDelayTierDivisor below.
    [SerializeField] private float shootSequentialDelay = 0.25f;
    // Max bearing turn rate while an orb is tracking its own shot during ShootFlight — caps how fast a shot
    // orb's tracked aim can swing so circling this boss during its Shoot windup is a real counterplay option.
    [SerializeField] private float rotationRateDegPerSecond = 45f;

    [Header("Persistent Orb Shots")]
    // Fires an independent volley at EVERY non-invisible alive player on a flat interval, entirely
    // independent of this boss's own phaseId FSM — spawns one dedicated orb per qualifying player directly
    // (no live-pleb search), winds it up, then fires it. Inherits shootingDamageMultiplier/shotWindupDuration/
    // shotSpeed/rotationRateDegPerSecond straight from the Shooting header above, same as before.
    [SerializeField] private float persistentShotInterval = 3f;
    // Spawn distance band for a persistent-volley orb, as a multiplier of the OUTER ring's own radius — e.g.
    // 1x-2x means it spawns somewhere between one and two outer-ring-radii away from this boss, facing
    // whichever player it's assigned to.
    [SerializeField] private float persistentSpawnRadiusMultiplierMin = 1f;
    [SerializeField] private float persistentSpawnRadiusMultiplierMax = 2f;

    // Sized to this game's own hard player cap instead of anything tier/ring-derived — see
    // feedback_multiplayer_scale_reminder.
    private const int MaxConcurrentPersistentTargets = 4;

    [Header("Attack Cadence")]
    [SerializeField] private float attackCooldown = 10f;

    [Header("Tier Scaling")]
    [SerializeField] private int   innerCountTierGrowth          = 1;
    [SerializeField] private int   middleCountTierGrowth         = 2;
    [SerializeField] private int   outerCountTierGrowth          = 3;
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;
    // RECIPROCAL formula (delay(tier) = authoredDelay / (1 + (tier-1) * divisor)), not a linear shrink — see
    // OrbiterGloomPD/the original King Orbiter's own identical field comment history for why: count(tier)
    // grows linearly with tier, so a linear delay shrink can only ever cancel that out at one tier, overshooting
    // at every other tier. A reciprocal shrink keeps the whole Shoot Phase's total dispatch time
    // tier-independent.
    [SerializeField] private float shootSequentialDelayTierDivisor = 1f;

    [Header("Orbs")]
    [SerializeField] private OrbiterOrbPD orbPrefab;

    // ShootFlight + Persistent Volley indicator prefab — a generic LineIndicatorPD, shared by both. Spawned
    // via hierarchy.Create(), not Instantiate(), specifically so its own sibling PredictedTransform component
    // actually ticks/smooths its render pose.
    [Header("Indicators")]
    [SerializeField] private GameObject lineIndicatorPrefab;

    private static readonly List<CharacterStatsPD> _shotTargetScratch = new();
    private static readonly List<PlayerID>         _shotTargetIdScratch = new();

    // Plain (not predicted State) snapshot of the current ring's chosen shot target — instantly available on
    // every machine the moment PickShotTarget() decides it. Committed for this ring's own full duration once
    // picked; only ever reassigned by a fresh PickShotTarget() call.
    private CharacterStatsPD _shotTargetSnapshot;
    // Companion to _shotTargetSnapshot — its position, recorded every tick it's actually alive+visible,
    // frozen otherwise.
    private Vector3 _shotTargetLastKnownPosition;

    protected override void LateAwake()
    {
        base.LateAwake();
        currentState.attackCooldownTimer = attackCooldown;
        currentState.persistentCooldownTimer = persistentShotInterval;
    }

    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        currentState.scaledShootSequentialDelay = shootSequentialDelay / (1f + (spawnTier - 1) * shootSequentialDelayTierDivisor);
        currentState.scaledInnerCount  = innerCount  + (spawnTier - 1) * innerCountTierGrowth;
        currentState.scaledMiddleCount = middleCount + (spawnTier - 1) * middleCountTierGrowth;
        currentState.scaledOuterCount  = outerCount  + (spawnTier - 1) * outerCountTierGrowth;
        currentState.scaledRotationRateDegPerSecond = rotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment).
        currentState.scaledAttackCooldown = attackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;

        currentState.scaledPersistentShotInterval = persistentShotInterval * attackCooldownMultiplier;
        currentState.persistentCooldownTimer = currentState.scaledPersistentShotInterval;
    }

    protected override State GetInitialState()
    {
        var s = base.GetInitialState();
        s.scaledShootSequentialDelay = shootSequentialDelay;
        s.scaledInnerCount = innerCount;
        s.scaledMiddleCount = middleCount;
        s.scaledOuterCount = outerCount;
        s.scaledRotationRateDegPerSecond = rotationRateDegPerSecond;
        s.scaledAttackCooldown = attackCooldown;
        s.scaledPersistentShotInterval = persistentShotInterval;
        s.shootTrackedOrb = DisposableList<PredictedObjectID>.Create(0);
        s.shootTrackedBearing = DisposableList<Vector3>.Create(0);
        s.shootTrackedIndicator = DisposableList<PredictedObjectID>.Create(0);
        s.shootTrackedFired = DisposableList<bool>.Create(0);
        s.shootTrackedWindupStart = DisposableList<float>.Create(0);
        s.innerOrbs  = DisposableList<PredictedObjectID>.Create(innerCount);
        s.middleOrbs = DisposableList<PredictedObjectID>.Create(middleCount);
        s.outerOrbs  = DisposableList<PredictedObjectID>.Create(outerCount);
        s.persistentOrbs = DisposableList<PredictedObjectID>.Create(0);
        s.persistentTargetId = DisposableList<PlayerID>.Create(0);
        s.persistentBearingDir = DisposableList<Vector3>.Create(0);
        s.persistentIndicatorId = DisposableList<PredictedObjectID>.Create(0);
        s.persistentLastKnownPosition = DisposableList<Vector3>.Create(0);
        return s;
    }

    // ── Ring helpers ─────────────────────────────────────────────────────────
    private int RingCount(ref State state, int ring) => ring switch { 0 => state.scaledInnerCount, 1 => state.scaledMiddleCount, _ => state.scaledOuterCount };
    private float RingRadius(int ring)       => ring switch { 0 => innerOrbitRadius, 1 => middleOrbitRadius, _ => outerOrbitRadius };
    // Middle ring runs counter to inner/outer — negated here so every caller automatically gets the correctly
    // signed speed without needing to remember to flip it.
    private float RingAngularSpeed(int ring) => ring switch { 0 => innerOrbitAngularSpeed, 1 => -middleOrbitAngularSpeed, _ => outerOrbitAngularSpeed };

    // Shoot order matches Spawn order — inner -> middle -> outer, identity mapping.
    private static int ShootPhysicalRing(int shootRingIndex) => shootRingIndex;

    private static ref DisposableList<PredictedObjectID> RingOrbList(ref State state, int ring)
    {
        switch (ring)
        {
            case 0:  return ref state.innerOrbs;
            case 1:  return ref state.middleOrbs;
            default: return ref state.outerOrbs;
        }
    }

    // Resolves RingOrbList(ring)[i] to its OrbiterOrbPD — no generation check needed (see class doc comment),
    // just the ordinary null/component-missing fallback every self-owned spawned-object resolve uses.
    private bool TryResolveRingOrb(ref State state, int ring, int i, out OrbiterOrbPD orb)
    {
        orb = null;
        var go = hierarchy.GetGameObject(RingOrbList(ref state, ring)[i]);
        if (go == null || !go.TryGetComponent(out orb)) return false;
        return true;
    }

    // Slot 0 = this boss's current facing direction, remaining slots fan out evenly — same formula as
    // OrbiterGloomPD.SlotAngleDeg.
    private float SlotAngleDeg(int index, int count)
    {
        Vector3 fwd = transform.forward;
        float baseAngle = Mathf.Atan2(fwd.z, fwd.x) * Mathf.Rad2Deg;
        return baseAngle + index * (360f / count);
    }

    // ── Active state ─────────────────────────────────────────────────────────
    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        var phase = GetPhase(ref state);
        if (phase == KingOrbiterPhase.Moving)
            state.attackCooldownTimer -= delta;

        switch (phase)
        {
            case KingOrbiterPhase.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f)
                    EnterSpawning(ref state);
                break;

            // Stationary through the whole Spawning sequence, matching OrbiterGloomPD's identical phase.
            case KingOrbiterPhase.Spawning:
            {
                state.phaseTimer += delta * EffectiveAttackSpeed;
                if (state.phaseTimer >= spawnDuration)
                {
                    if (state.interruptSkipOrbiting)
                    {
                        // Interrupted mid-Spawning — skip Orbiting entirely, go straight to Knockback with
                        // whatever already spawned (never despawned, see State.interruptSkipOrbiting's own
                        // doc comment).
                        state.interruptSkipOrbiting = false;
                        EnterKnockbackWindup(ref state);
                    }
                    else
                    {
                        BeginOrbitingAll(ref state);
                        state.phaseId    = (int)KingOrbiterPhase.Orbiting;
                        state.phaseTimer = orbitDuration;
                    }
                }
                break;
            }

            case KingOrbiterPhase.Orbiting:
                if (_target != null) HandleMovement(ref state, delta);
                state.phaseTimer -= delta;
                // Pushed every tick, not edge-triggered — same reasoning OrbiterGloomPD's own identical call
                // already established.
                PushStunnedToAllRings(ref state, IsTrueStunned && isStunnable);
                if (state.phaseTimer <= 0f) EnterKnockbackWindup(ref state);
                break;

            case KingOrbiterPhase.KnockbackWindup:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) FireKnockback(ref state);
                break;

            // Same continuous-dispatch/independent-release shape the old grab-based ShootFlight already used
            // — see AdvanceShootDispatchRing/FireShotRing/EndShotRing for the exact mechanics, unchanged from
            // before except firing orbs instead of launching held plebs.
            case KingOrbiterPhase.ShootFlight:
            {
                state.phaseTimer += delta * EffectiveAttackSpeed;

                UpdateShootBearingTracking(ref state, delta);

                while (state.shootDispatchRingIndex <= 2)
                {
                    int physicalDispatchRing = ShootPhysicalRing(state.shootDispatchRingIndex);
                    int dispatchCount = RingOrbList(ref state, physicalDispatchRing).Count;

                    while (state.shootRingLocalIndex < dispatchCount &&
                           state.phaseTimer >= state.shootSequenceIndex * state.scaledShootSequentialDelay + shotWindupDuration)
                    {
                        FireShotRing(ref state, physicalDispatchRing, state.shootRingLocalIndex);
                        state.shootRingLocalIndex++;
                        state.shootSequenceIndex++;
                    }

                    if (state.shootRingLocalIndex < dispatchCount) break;
                    if (!AdvanceShootDispatchRing(ref state)) break;
                }

                while (state.shootRingIndex <= 2 && state.shootRingIndex <= state.shootDispatchRingIndex)
                {
                    int shootOrderIdx = state.shootRingIndex;
                    int physicalRing = ShootPhysicalRing(shootOrderIdx);
                    var orbs = RingOrbList(ref state, physicalRing);

                    if (orbs.Count == 0)
                    {
                        state.shootRingIndex++;
                        continue;
                    }

                    bool dispatchDoneForThisRing = shootOrderIdx < state.shootDispatchRingIndex
                        || state.shootRingLocalIndex >= orbs.Count;
                    if (!dispatchDoneForThisRing) break;

                    if (!AllFinishedFlying(ref state, physicalRing)) break;

                    EndShotRing(ref state);
                    state.shootRingIndex++;
                }

                if (state.shootRingIndex > 2) FinishShootPhase(ref state);
                break;
            }
        }

        // Only Moving/Orbiting are stunnable; kinematic everywhere else (a stationary boss during any
        // windup/frozen phase is a solid obstacle, not non-interacting) — same shape as before, renamed.
        phase = GetPhase(ref state);
        isStunnable = (phase == KingOrbiterPhase.Moving || phase == KingOrbiterPhase.Orbiting) && !stunResistant;

        if (_rigidbody != null)
        {
            bool shouldBeKinematic = phase != KingOrbiterPhase.Moving && phase != KingOrbiterPhase.Orbiting;
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }
    }

    // ── Spawn ────────────────────────────────────────────────────────────────

    // Begins the WHOLE Spawning Phase — unlike the old grab-based Pulling, every ring spawns its FULL scaled
    // count simultaneously, no staggered dispatch at all (explicit design).
    private void EnterSpawning(ref State state)
    {
        state.phaseId    = (int)KingOrbiterPhase.Spawning;
        state.phaseTimer = 0f;
        state.interruptSkipOrbiting = false;
        SpawnRingOrbs(ref state, 0);
        SpawnRingOrbs(ref state, 1);
        SpawnRingOrbs(ref state, 2);
    }

    // Same shape as OrbiterGloomPD.SpawnOrbs, parameterized per ring's own radius/speed/count — no candidate
    // search, no whiff case, always spawns the ring's full scaled count unconditionally.
    private void SpawnRingOrbs(ref State state, int ring)
    {
        ref var orbs = ref RingOrbList(ref state, ring);
        orbs.Clear();
        if (orbPrefab == null) return;

        int count = RingCount(ref state, ring);
        float radius = RingRadius(ring);
        float angularSpeed = RingAngularSpeed(ring);
        float orbDamage = GetOwnDamage() * orbitDamageMultiplier;
        Vector3 spawnPos = transform.position;

        for (int i = 0; i < count; i++)
        {
            var orbId = hierarchy.Create(orbPrefab.gameObject, spawnPos, Quaternion.identity);
            if (!orbId.HasValue) continue;
            var go = hierarchy.GetGameObject(orbId.Value);
            if (go == null || !go.TryGetComponent(out OrbiterOrbPD orb)) continue;

            go.transform.localScale = Vector3.one * size;
            orb.Launch(ObjectId, SlotAngleDeg(i, count), radius, angularSpeed, spawnDuration, orbDamage, TierAccentMaterial);
            orbs.Add(orbId.Value);
        }
    }

    private void BeginOrbitingAll(ref State state)
    {
        for (int ring = 0; ring < 3; ring++)
        {
            var orbs = RingOrbList(ref state, ring);
            for (int i = 0; i < orbs.Count; i++)
                if (TryResolveRingOrb(ref state, ring, i, out var orb))
                    orb.BeginOrbiting();
        }
    }

    private void PushStunnedToAllRings(ref State state, bool stunned)
    {
        for (int ring = 0; ring < 3; ring++)
        {
            var orbs = RingOrbList(ref state, ring);
            for (int i = 0; i < orbs.Count; i++)
                if (TryResolveRingOrb(ref state, ring, i, out var orb))
                    orb.SetStunned(stunned);
        }
    }

    private void PushKnockbackFrozenToAllRings(ref State state, bool frozen)
    {
        for (int ring = 0; ring < 3; ring++)
        {
            var orbs = RingOrbList(ref state, ring);
            for (int i = 0; i < orbs.Count; i++)
                if (TryResolveRingOrb(ref state, ring, i, out var orb))
                    orb.SetKnockbackFrozen(frozen);
        }
    }

    // ── Knockback ────────────────────────────────────────────────────────────

    // Freezes every active orb across all 3 rings in place via SetKnockbackFrozen — motion-only, no tint, no
    // damage change (orbs stay dangerous the whole time this boss's own attack sequence holds them still —
    // see OrbiterOrbPD.State.knockbackFrozen's own doc comment). Covers BOTH a natural Orbiting-timer expiry
    // AND the interrupted-during-Spawning case for free, since orbs that never started orbiting are already
    // sitting motionless at their spawn slot regardless.
    private void EnterKnockbackWindup(ref State state)
    {
        PushKnockbackFrozenToAllRings(ref state, true);
        state.phaseId    = (int)KingOrbiterPhase.KnockbackWindup;
        state.phaseTimer = knockbackChargeTime;
    }

    // Radial burst — same shape as KingSpikePD.FireSpikeBurst, but knockback instead of damage. No damage
    // dealt by this attack at all. Deliberately does NOT skip IsInvincible — a dashing player is IsInvincible
    // via CharacterStatsPD's committed-invincibility channel, and reaching through that to yank them out of
    // the dash is the entire reason dashes were made interruptible in the first place.
    private static readonly Collider[] _knockbackOverlapResults = new Collider[8];

    private void FireKnockback(ref State state)
    {
        Vector3 origin = transform.position;
        int n = Physics.OverlapSphereNonAlloc(origin, knockbackRadius, _knockbackOverlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _knockbackOverlapResults[i].GetComponent<IFriendlyPD>();
            if (stats == null || stats.IsDead) continue;
            Vector3 toPlayer = stats.transform.position - origin;
            Vector3 dir = toPlayer.sqrMagnitude > 0.0001f ? toPlayer.normalized : Vector3.forward;
            stats.AddKnockbackVelocity(dir * knockbackForce);
        }

        EnterShootWindupOrAdvance(ref state);
    }

    // ── Shooting ─────────────────────────────────────────────────────────────

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

    // Called inline from ShootFlight's own dispatch loop the instant the CURRENT ring has had every one of its
    // own orbs dispatched — advances to the next ring and immediately picks its shared target. An empty ring
    // cascades through within the same tick, same as any other empty ring. Returns false once ring 2 is also
    // fully dispatched.
    private bool AdvanceShootDispatchRing(ref State state)
    {
        state.shootDispatchRingIndex++;
        state.shootRingLocalIndex = 0;
        if (state.shootDispatchRingIndex > 2) return false;

        int ring = ShootPhysicalRing(state.shootDispatchRingIndex);
        int orbCount = RingOrbList(ref state, ring).Count;
        SetShootRingGlobalStart(ref state, state.shootDispatchRingIndex, state.shootNextGlobalStart);
        state.shootNextGlobalStart += orbCount;
        if (orbCount == 0) return true;

        PickShotTarget(ref state);
        // Seeds this newly-opened ring's own bearing + indicator pose immediately (delta 0) rather than
        // waiting for next tick's regular UpdateShootBearingTracking call.
        UpdateShootBearingTracking(ref state, 0f);
        return true;
    }

    private static int FindTrackedShootIndex(ref State state, PredictedObjectID orbId)
    {
        for (int i = 0; i < state.shootTrackedOrb.Count; i++)
            if (state.shootTrackedOrb[i].Equals(orbId)) return i;
        return -1;
    }

    // Swap-with-last removal across all 4 parallel shootTracked* lists — order is never meaningful here.
    private static void RemoveTrackedShootEntryAt(ref State state, int i)
    {
        int last = state.shootTrackedOrb.Count - 1;
        state.shootTrackedOrb[i]            = state.shootTrackedOrb[last];
        state.shootTrackedBearing[i]        = state.shootTrackedBearing[last];
        state.shootTrackedIndicator[i]      = state.shootTrackedIndicator[last];
        state.shootTrackedFired[i]          = state.shootTrackedFired[last];
        state.shootTrackedWindupStart[i]    = state.shootTrackedWindupStart[last];
        state.shootTrackedOrb.RemoveAt(last);
        state.shootTrackedBearing.RemoveAt(last);
        state.shootTrackedIndicator.RemoveAt(last);
        state.shootTrackedFired.RemoveAt(last);
        state.shootTrackedWindupStart.RemoveAt(last);
    }

    // Removes any tracked entry whose own orb has genuinely finished (fired AND its own flight is over —
    // OrbiterOrbPD.IsDespawned) or become invalid (unresolvable) — an orb's own line indicator stays visible
    // for its ENTIRE movement, only disappearing once THAT SPECIFIC orb is actually done, independent of every
    // other orb (including ones from a different ring active at the same time).
    private void DespawnFinishedShootIndicators(ref State state)
    {
        for (int i = state.shootTrackedOrb.Count - 1; i >= 0; i--)
        {
            var go = hierarchy.GetGameObject(state.shootTrackedOrb[i]);
            bool finished = go == null || !go.TryGetComponent(out OrbiterOrbPD orb)
                || (state.shootTrackedFired[i] && orb.IsDespawned);
            if (!finished) continue;

            DespawnLineIndicator(state.shootTrackedIndicator[i]);
            RemoveTrackedShootEntryAt(ref state, i);
        }
    }

    // Opens a tracked entry the instant an orb's own windup window opens, then rate-caps its tracked bearing
    // every subsequent tick via Vector3.RotateTowards, until that orb actually fires, at which point its aim
    // freezes for good. Both FireShotRing (the real launch) and the spawned LineIndicatorPD's own SetAim read
    // this SAME tracked value, so what's shown during the windup is always exactly what fires.
    private void UpdateShootBearingTracking(ref State state, float delta)
    {
        DespawnFinishedShootIndicators(ref state);

        if (state.shootDispatchRingIndex > 2) return;

        if (_shotTargetSnapshot == null && CharacterStatsPD.AnyValidTargetExists)
            PickShotTarget(ref state);

        if (_shotTargetSnapshot != null && !_shotTargetSnapshot.IsDead &&
            (!_shotTargetSnapshot.IsInvisible || _shotTargetSnapshot.IsTaunting))
            _shotTargetLastKnownPosition = _shotTargetSnapshot.transform.position;

        Vector3 targetPos = _shotTargetSnapshot != null ? _shotTargetLastKnownPosition : lastKnownTargetPosition;

        int physicalDispatchRing = ShootPhysicalRing(state.shootDispatchRingIndex);
        var orbs = RingOrbList(ref state, physicalDispatchRing);
        int dispatchCount = orbs.Count;
        int ringGlobalStart = ShootRingGlobalStart(ref state, state.shootDispatchRingIndex);

        for (int i = state.shootRingLocalIndex; i < dispatchCount; i++)
        {
            float localElapsed = state.phaseTimer - (ringGlobalStart + i) * state.scaledShootSequentialDelay;
            if (localElapsed < 0f || localElapsed >= shotWindupDuration) continue;

            var orbId = orbs[i];
            if (!TryResolveRingOrb(ref state, physicalDispatchRing, i, out var orb)) continue;

            int idx = FindTrackedShootIndex(ref state, orbId);
            if (idx < 0)
            {
                idx = state.shootTrackedOrb.Count;
                state.shootTrackedOrb.Add(orbId);
                state.shootTrackedBearing.Add(Vector3.zero);
                state.shootTrackedIndicator.Add(default);
                state.shootTrackedFired.Add(false);
                state.shootTrackedWindupStart.Add(state.phaseTimer - localElapsed);
            }

            if (state.shootTrackedFired[idx]) continue;

            Vector3 toTarget = targetPos - orb.transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) continue;
            Vector3 targetDir = toTarget.normalized;

            Vector3 current = state.shootTrackedBearing[idx];
            state.shootTrackedBearing[idx] = current.sqrMagnitude < 0.0001f
                ? targetDir
                : Vector3.RotateTowards(current, targetDir, state.scaledRotationRateDegPerSecond * Mathf.Deg2Rad * delta * EffectiveAttackSpeed, 0f);

            var indicator = ResolveOrSpawnLineIndicator(state.shootTrackedIndicator[idx], orb.transform.position,
                state.shootTrackedBearing[idx], shootingRange, out var resolvedId);
            state.shootTrackedIndicator[idx] = resolvedId;
            indicator?.SetAim(state.shootTrackedBearing[idx]);
        }
    }

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

    private void FinishShootPhase(ref State state)
    {
        state.attackCooldownTimer = state.scaledAttackCooldown;
        FindRandomTarget();
        state.phaseId = (int)KingOrbiterPhase.Moving;
        _shotTargetSnapshot = null;
    }

    // Picks one random currently-valid player, excluding state.lastShotTargetId when more than one candidate
    // qualifies — falls back to including it anyway if excluding it would leave nobody.
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
            if (p.IsDead || (p.IsInvisible && !p.IsTaunting)) continue;
            if (excludeLast && kvp.Key.Equals(state.lastShotTargetId)) continue;
            _shotTargetScratch.Add(p);
            _shotTargetIdScratch.Add(kvp.Key);
        }
    }

    // Fires exactly orb index i of the given ring, using its own tracked bearing rather than a fresh live read
    // — this is what makes the rate cap actually bite on the real shot, not just the windup indicator's own
    // cosmetic aim.
    private void FireShotRing(ref State state, int ring, int i)
    {
        var orbId = RingOrbList(ref state, ring)[i];
        bool resolved = TryResolveRingOrb(ref state, ring, i, out var orb);

        int idx = FindTrackedShootIndex(ref state, orbId);

        Vector3 dir;
        Vector3 tracked = idx >= 0 ? state.shootTrackedBearing[idx] : Vector3.zero;
        if (tracked.sqrMagnitude >= 0.0001f)
        {
            dir = tracked;
        }
        else
        {
            Vector3 targetPos = _shotTargetSnapshot != null ? _shotTargetLastKnownPosition : lastKnownTargetPosition;
            Vector3 toTarget = resolved ? targetPos - orb.transform.position : Vector3.zero;
            toTarget.y = 0f;
            dir = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;
        }

        if (idx >= 0) state.shootTrackedFired[idx] = true;
        if (!resolved) return;

        // * EffectiveAttackSpeed — bakes this boss's CURRENT attack speed into the launch, once, at the moment
        // it actually fires. Damage re-baked to shootingDamageMultiplier here too — see
        // OrbiterOrbPD.BeginFlight's own doc comment for why this differs from the orbitDamageMultiplier value
        // this same orb was spawned with.
        orb.BeginFlight(dir, shootingRange, shotSpeed * EffectiveAttackSpeed, GetOwnDamage() * shootingDamageMultiplier);
    }

    // A ring only releases once every orb it's dispatched has genuinely finished flying (OrbiterOrbPD.
    // IsDespawned — the orb despawns itself the instant it's traveled its full range).
    private bool AllFinishedFlying(ref State state, int ring)
    {
        var orbs = RingOrbList(ref state, ring);
        for (int i = 0; i < orbs.Count; i++)
        {
            if (!TryResolveRingOrb(ref state, ring, i, out var orb)) continue;
            if (!orb.IsDespawned) return false;
        }
        return true;
    }

    // Every orb in this ring has already self-despawned by the time this is called (see AllFinishedFlying) —
    // nothing left to do but clear this ring's own bookkeeping.
    private void EndShotRing(ref State state)
    {
        int ring = ShootPhysicalRing(state.shootRingIndex);
        RingOrbList(ref state, ring).Clear();
    }

    // ── Death / wipe ─────────────────────────────────────────────────────────

    private void DespawnRing(ref State state, int ring)
    {
        var orbs = RingOrbList(ref state, ring);
        for (int i = 0; i < orbs.Count; i++)
        {
            var go = hierarchy.GetGameObject(orbs[i]);
            if (go != null && go.TryGetComponent(out OrbiterOrbPD orb))
                orb.Despawn();
        }
        orbs.Clear();
    }

    private void DespawnAllRingOrbs(ref State state)
    {
        DespawnRing(ref state, 0);
        DespawnRing(ref state, 1);
        DespawnRing(ref state, 2);
    }

    // Force-despawns every currently-spawned shoot indicator immediately on death, and clears all 5
    // shootTracked* lists.
    private void DespawnAllShootIndicators(ref State state)
    {
        for (int i = 0; i < state.shootTrackedIndicator.Count; i++)
            DespawnLineIndicator(state.shootTrackedIndicator[i]);
        state.shootTrackedOrb.Clear();
        state.shootTrackedBearing.Clear();
        state.shootTrackedIndicator.Clear();
        state.shootTrackedFired.Clear();
        state.shootTrackedWindupStart.Clear();
    }

    // "Immediately release" requires this to happen in the SAME call as B.isDead flipping true — same
    // reasoning as OrbiterGloomPD.Die()'s own identical override. No reclaim-snapshot bookkeeping needed
    // anymore (see class doc comment) — self-owned orbs need no revert-reclaim, same as Gloom.
    protected override void Die()
    {
        base.Die();
        DespawnAllRingOrbs(ref currentState);
        DespawnAllPersistentOrbs(ref currentState);
        DespawnAllShootIndicators(ref currentState);
    }

    protected override void OnSilentDespawn()
    {
        base.OnSilentDespawn();
        DespawnAllRingOrbs(ref currentState);
        DespawnAllPersistentOrbs(ref currentState);
        DespawnAllShootIndicators(ref currentState);
    }

    // Restores knockbackIndicator on a misprediction-revert — same class of bug as LeaperGloomPD/BlitzGloomPD/
    // KingSpikePD's own identical fix. phase never actually left KnockbackWindup (only the separate isDead
    // flag mispredicted), so UpdateView's own _lastVisualState edge-detect never re-fires on its own; this is
    // what re-shows it. No reparenting involved — knockbackIndicator is never unparented, so a plain
    // SetActive(true) is enough. No ring/persistent-orb reclaim needed anymore (see class doc comment).
    protected override void OnHiddenStateChanged(bool hidden)
    {
        base.OnHiddenStateChanged(hidden);
        if (!hidden && knockbackIndicator != null && GetPhase(ref currentState) == KingOrbiterPhase.KnockbackWindup)
            knockbackIndicator.SetActive(true);
    }

    // Only Spawning is interruptible, matching OrbiterGloomPD's own convention (moved here from the old
    // Orbit/Orbiting phase).
    protected override bool CanBeInterrupted => GetPhase(ref currentState) == KingOrbiterPhase.Spawning;

    // Does NOT despawn anything (explicit divergence from OrbiterGloomPD.OnInterrupted) — just flags that
    // Orbiting should be skipped once Spawning's own timer naturally completes. See
    // State.interruptSkipOrbiting's own doc comment for the full mechanism. Persistent Orb Shots are
    // untouched by this — they run entirely independent of phaseId/interrupts.
    protected override void OnInterrupted() => currentState.interruptSkipOrbiting = true;

    // ── Persistent Orb Shots ─────────────────────────────────────────────────

    protected override void Simulate(ref State state, float delta)
    {
        base.Simulate(ref state, delta);

        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (B.isDead || B.wiped) return;

        UpdatePersistentOrbShots(ref state, delta);
    }

    private void UpdatePersistentOrbShots(ref State state, float delta)
    {
        if (!state.persistentVolleyActive)
        {
            state.persistentCooldownTimer -= delta;
            if (state.persistentCooldownTimer > 0f) return;

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
            DespawnFinishedPersistentIndicators(ref state);

            float flightDuration = shootingRange / Mathf.Max(0.0001f, shotSpeed);
            if (state.persistentVolleyTimer >= flightDuration)
                FinishPersistentVolley(ref state);
        }
    }

    // Spawns one dedicated orb per non-invisible alive player (capped at MaxConcurrentPersistentTargets) —
    // no live-pleb search at all (explicit design, replacing the old FindNearestUnheldPleb). Each orb spawns
    // facing its assigned player, at a distance randomized between persistentSpawnRadiusMultiplierMin/Max
    // times the outer ring's own radius, and sits at that FIXED world point for its whole pre-flight life
    // (Launch's own fixedPosition:true — explicit design, 2026-10-21: unlike a ring orb, a persistent-volley
    // orb should never move at all outside of the shot itself, so it does NOT re-track this boss's own live
    // position the way an orbiting orb does) until FirePersistentVolley sends it flying.
    private void StartPersistentVolley(ref State state)
    {
        state.persistentOrbs.Clear();
        state.persistentTargetId.Clear();
        state.persistentBearingDir.Clear();
        state.persistentIndicatorId.Clear();
        state.persistentLastKnownPosition.Clear();
        if (orbPrefab == null) return;

        float minDist = outerOrbitRadius * persistentSpawnRadiusMultiplierMin;
        float maxDist = outerOrbitRadius * persistentSpawnRadiusMultiplierMax;
        float orbDamage = GetOwnDamage() * shootingDamageMultiplier;

        foreach (var kvp in CharacterStatsPD.All)
        {
            if (state.persistentOrbs.Count >= MaxConcurrentPersistentTargets) break;

            var p = kvp.Value;
            if (p.IsDead || (p.IsInvisible && !p.IsTaunting)) continue;

            Vector3 toPlayer = p.transform.position - transform.position;
            toPlayer.y = 0f;
            Vector3 dir = toPlayer.sqrMagnitude > 0.0001f ? toPlayer.normalized : transform.forward;
            float dist = GameRandom.instance.NextFloat(minDist, maxDist);
            Vector3 spawnPos = transform.position + dir * dist;

            var orbId = hierarchy.Create(orbPrefab.gameObject, spawnPos, Quaternion.identity);
            if (!orbId.HasValue) continue;
            var go = hierarchy.GetGameObject(orbId.Value);
            if (go == null || !go.TryGetComponent(out OrbiterOrbPD orb)) continue;

            go.transform.localScale = Vector3.one * size;
            // orbitRadius/angularSpeed/angle are irrelevant for a fixedPosition orb (never read once that
            // flag is set) — passed as 0 rather than threading unused values through.
            orb.Launch(ObjectId, 0f, 0f, 0f, spawnDuration, orbDamage, TierAccentMaterial, fixedPosition: true);

            state.persistentOrbs.Add(orbId.Value);
            state.persistentTargetId.Add(kvp.Key);
            state.persistentBearingDir.Add(dir);
            state.persistentIndicatorId.Add(default);
            state.persistentLastKnownPosition.Add(p.transform.position);
        }

        state.persistentVolleyActive = state.persistentOrbs.Count > 0;
        state.persistentShotFired    = false;
        state.persistentVolleyTimer  = 0f;

        UpdatePersistentBearingTracking(ref state, 0f);
    }

    private bool TryResolvePersistentOrb(ref State state, int i, out OrbiterOrbPD orb)
    {
        orb = null;
        var go = hierarchy.GetGameObject(state.persistentOrbs[i]);
        if (go == null || !go.TryGetComponent(out orb)) return false;
        return true;
    }

    // Rate-capped bearing tracking toward each slot's OWN assigned target player — same shape as
    // UpdateShootBearingTracking above. A target dying or going invisible mid-windup never cancels or stalls
    // this slot, by explicit design — state.persistentLastKnownPosition[i] only refreshes while the target is
    // actually alive+visible, frozen otherwise.
    private void UpdatePersistentBearingTracking(ref State state, float delta)
    {
        for (int i = 0; i < state.persistentOrbs.Count; i++)
        {
            if (!CharacterStatsPD.All.TryGetValue(state.persistentTargetId[i], out var target)) continue;
            if (!TryResolvePersistentOrb(ref state, i, out var orb)) continue;
            Transform go = orb.transform;

            if (!target.IsDead && (!target.IsInvisible || target.IsTaunting))
                state.persistentLastKnownPosition[i] = target.transform.position;

            Vector3 toTarget = state.persistentLastKnownPosition[i] - go.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0001f) continue;
            Vector3 targetDir = toTarget.normalized;

            state.persistentBearingDir[i] = Vector3.RotateTowards(state.persistentBearingDir[i], targetDir,
                state.scaledRotationRateDegPerSecond * Mathf.Deg2Rad * delta * EffectiveAttackSpeed, 0f);

            if (i < state.persistentIndicatorId.Count)
            {
                var indicator = ResolveOrSpawnLineIndicator(state.persistentIndicatorId[i], go.position,
                    state.persistentBearingDir[i], shootingRange, out var resolvedId);
                state.persistentIndicatorId[i] = resolvedId;
                indicator?.SetAim(state.persistentBearingDir[i]);
            }
        }
    }

    // Spawns a new LineIndicatorPD via hierarchy.Create() the first time a slot needs one, or resolves the
    // already-spawned instance otherwise — shared by ShootFlight and Persistent Volley tracking.
    // hierarchy.Create() (not Instantiate()) specifically so the prefab's own sibling PredictedTransform
    // component actually registers/ticks and smooths this object's render pose.
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

    private void DespawnLineIndicator(PredictedObjectID id)
    {
        if (id.Equals(default)) return;
        var go = hierarchy.GetGameObject(id);
        if (go != null && go.TryGetComponent(out LineIndicatorPD indicator)) indicator.Despawn();
    }

    // Reads each slot's own tracked bearing (rate-capped by UpdatePersistentBearingTracking above) — same
    // "indicator and real shot read the identical value" contract FireShotRing's own doc comment establishes.
    private void FirePersistentVolley(ref State state)
    {
        float orbDamage = GetOwnDamage() * shootingDamageMultiplier;
        for (int i = 0; i < state.persistentOrbs.Count; i++)
        {
            if (!TryResolvePersistentOrb(ref state, i, out var orb)) continue;
            orb.BeginFlight(state.persistentBearingDir[i], shootingRange, shotSpeed * EffectiveAttackSpeed, orbDamage);
        }
        state.persistentShotFired   = true;
        state.persistentVolleyTimer = 0f;
    }

    // Per-slot indicator cleanup, checked every tick while winding down post-fire — despawns slot i's own
    // indicator the INSTANT that specific orb's own flight actually finishes (OrbiterOrbPD.IsDespawned) or
    // it's otherwise gone, fully independent of every other slot.
    private void DespawnFinishedPersistentIndicators(ref State state)
    {
        for (int i = 0; i < state.persistentOrbs.Count && i < state.persistentIndicatorId.Count; i++)
        {
            if (state.persistentIndicatorId[i].Equals(default)) continue;

            var go = hierarchy.GetGameObject(state.persistentOrbs[i]);
            bool finished = go == null || !go.TryGetComponent(out OrbiterOrbPD orb) || orb.IsDespawned;
            if (!finished) continue;

            DespawnLineIndicator(state.persistentIndicatorId[i]);
            state.persistentIndicatorId[i] = default;
        }
    }

    private void FinishPersistentVolley(ref State state)
    {
        DespawnAllPersistentOrbs(ref state);
        state.persistentVolleyActive = false;
        state.persistentShotFired    = false;
    }

    // Despawns every currently-active persistent-volley orb + indicator and clears the lists — safe to call
    // even if every orb has already self-despawned from completing its own flight (Despawn() is idempotent).
    // Called both from normal post-flight completion (FinishPersistentVolley) and from Die() (immediate
    // despawn on death, same reasoning as DespawnAllRingOrbs's own Die() call).
    private void DespawnAllPersistentOrbs(ref State state)
    {
        for (int i = 0; i < state.persistentOrbs.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.persistentOrbs[i]);
            if (go != null && go.TryGetComponent(out OrbiterOrbPD orb))
                orb.Despawn();
            if (i < state.persistentIndicatorId.Count)
                DespawnLineIndicator(state.persistentIndicatorId[i]);
        }
        state.persistentOrbs.Clear();
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

        // Death is handled by base.UpdateView() above — without this, a same-frame PredictedHierarchy
        // reconciliation rebuild after a mid-sequence death would re-run the _lastVisualState-gated block
        // below as if entering that phase for the first time, immediately re-enabling an indicator
        // OnDeathStop() just hid.
        if (viewState.b.isDead) return;

        if (knockbackIndicator != null && knockbackFill != null)
            knockbackFill.ShowCircle(knockbackRadius);

        var phase = GetPhase(ref viewState);
        UpdateShootIndicatorFills(viewState, phase);
        // Independent of `phase` — Persistent Orb Shots run regardless of phaseId, and can be visible at the
        // same time as a real Shooting Phase's own indicators.
        UpdatePersistentIndicatorFills(viewState);

        // Live, every frame regardless of the _lastVisualState-gated block below — reads the exact same
        // phaseTimer field that gates the real knockback trigger, so it hits exactly 1 (full) on the identical
        // tick the knockback actually fires, by construction.
        if (knockbackFill != null && phase == KingOrbiterPhase.KnockbackWindup)
            knockbackFill.SetProgress(1f - Mathf.Clamp01(viewState.phaseTimer / knockbackChargeTime));

        if (phase == _lastVisualState) return;
        _lastVisualState = phase;

        if (knockbackIndicator != null) knockbackIndicator.SetActive(phase == KingOrbiterPhase.KnockbackWindup);
        if (knockbackFill != null && phase != KingOrbiterPhase.KnockbackWindup) knockbackFill.Hide();
    }

    // Fill-progress-only pass for the spawned ShootFlight indicators — pose is already fully owned by
    // Simulate (UpdateShootBearingTracking/FireShotRing), and existence itself (a non-default
    // shootTrackedIndicator entry) already means "this orb is currently active."
    private void UpdateShootIndicatorFills(State viewState, KingOrbiterPhase phase)
    {
        if (phase != KingOrbiterPhase.ShootFlight) return;

        for (int i = 0; i < viewState.shootTrackedIndicator.Count; i++)
        {
            var id = viewState.shootTrackedIndicator[i];
            if (id.Equals(default)) continue;

            var go = hierarchy.GetGameObject(id);
            if (go == null || !go.TryGetComponent(out LineIndicatorPD indicator) || indicator.Fill == null) continue;

            // Shrinks the shown span to "collapse in behind" the orb as it actually flies — distance from the
            // indicator's own root (frozen at the orb's launch origin) to the orb's own CURRENT
            // transform.position, a live transform read so this matches whatever's actually rendered exactly.
            var orbGo = hierarchy.GetGameObject(viewState.shootTrackedOrb[i]);
            float traveled = orbGo != null ? Vector3.Distance(indicator.transform.position, orbGo.transform.position) : 0f;
            indicator.SetTraveled(traveled);

            float localElapsed = viewState.phaseTimer - viewState.shootTrackedWindupStart[i];
            indicator.Fill.SetProgress(Mathf.Clamp01(localElapsed / shotWindupDuration));
        }
    }

    // Same role as UpdateShootIndicatorFills above, for the Persistent Volley pool.
    private void UpdatePersistentIndicatorFills(State viewState)
    {
        for (int i = 0; i < viewState.persistentIndicatorId.Count; i++)
        {
            var id = viewState.persistentIndicatorId[i];
            if (id.Equals(default)) continue;

            var go = hierarchy.GetGameObject(id);
            if (go == null || !go.TryGetComponent(out LineIndicatorPD indicator) || indicator.Fill == null) continue;

            var orbGo = hierarchy.GetGameObject(viewState.persistentOrbs[i]);
            float traveled = orbGo != null ? Vector3.Distance(indicator.transform.position, orbGo.transform.position) : 0f;
            indicator.SetTraveled(traveled);

            // Gated on persistentShotFired, not a raw ratio off persistentVolleyTimer alone — that timer is
            // dual-purpose (counts up to shotWindupDuration while winding up, then resets to 0 and counts up
            // again to flight duration once fired). Once fired this always reads as fully filled.
            float progress = viewState.persistentShotFired ? 1f : Mathf.Clamp01(viewState.persistentVolleyTimer / shotWindupDuration);
            indicator.Fill.SetProgress(progress);
        }
    }

    protected override void OnDeathStop()
    {
        // ShootFlight/Persistent Volley indicators need no hiding here — Die()'s own Simulate-side
        // DespawnAllShootIndicators/DespawnAllPersistentOrbs calls already despawned them (this View-context
        // hook runs later, after that same tick's Simulate has already completed).
        if (knockbackIndicator != null) knockbackIndicator.SetActive(false);
        if (knockbackFill != null) knockbackFill.Hide();
    }

    protected override void OnRespawnReset()
    {
        currentState.phaseId             = 0;
        currentState.phaseTimer          = 0f;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        currentState.interruptSkipOrbiting  = false;
        currentState.shootRingIndex         = 0;
        currentState.shootDispatchRingIndex = 0;
        currentState.shootRingLocalIndex    = 0;
        currentState.shootSequenceIndex     = 0;
        currentState.shootNextGlobalStart   = 0;
        currentState.shootRingGlobalStart0  = 0;
        currentState.shootRingGlobalStart1  = 0;
        currentState.shootRingGlobalStart2  = 0;
        currentState.lastShotTargetId    = default;
        currentState.innerOrbs.Clear();
        currentState.middleOrbs.Clear();
        currentState.outerOrbs.Clear();
        // Defensive clear, no despawn attempt — Die()'s own DespawnAllShootIndicators already despawned
        // anything actually spawned (and cleared these same lists) before this pooled reuse could happen.
        currentState.shootTrackedOrb.Clear();
        currentState.shootTrackedBearing.Clear();
        currentState.shootTrackedIndicator.Clear();
        currentState.shootTrackedFired.Clear();
        currentState.shootTrackedWindupStart.Clear();
        currentState.persistentCooldownTimer = currentState.scaledPersistentShotInterval;
        currentState.persistentVolleyActive  = false;
        currentState.persistentShotFired     = false;
        currentState.persistentVolleyTimer   = 0f;
        currentState.persistentOrbs.Clear();
        currentState.persistentTargetId.Clear();
        currentState.persistentBearingDir.Clear();
        currentState.persistentIndicatorId.Clear();
        currentState.persistentLastKnownPosition.Clear();
        OnDeathStop();
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState    = (KingOrbiterPhase)(-1);
        _shotTargetSnapshot = null;
    }
}
