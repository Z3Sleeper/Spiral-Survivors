using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// The one and only boss. Ported from _OldSystemArchive/Enemies/KingSpike.cs + BossBase.cs, with the old
// coroutine-based bomb sequence (StartCoroutine/WaitForSeconds — not viable in PD, not deterministic or
// resimulation-safe) redesigned as tick-based timers instead. Charging reuses EnemyBasePD's generic dash
// primitives (StartDashToPoint/HandleDash) directly — no custom movement code needed for it.
//
// Enrage (2026-08-20): no boss-specific enrage behavior anymore — this boss just uses EnemyBasePD's fully
// generic Move Speed + Damage enrage system as-is (see that class's own "Enrage" Inspector header), same as
// every other boss. GetOwnDamage() below multiplies by EnrageDamageMultiplier explicitly since it overrides
// the base damage hook entirely.
public class KingSpikePD : EnemyBasePD<KingSpikePD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int     stateId;              // cast to/from KingSpikeState, same convention as SpearGloomPD
        public float   stateTimer;
        public float   attackCooldownTimer;
        public Vector3 chargeDirection;      // locked in at ChargeWindup entry, reused through Charging
        public int     chargesRemaining;
        // 0..1 how far through the fixed charge corridor the body has actually traveled, written
        // deterministically every Simulate tick while Charging — see HandleActiveState's own doc comment.
        // Drives the charge windup indicator's own "collapse in behind the body" shrink in UpdateView, exact
        // same shape as BlitzGloomPD.State.blitzTravelProgress (see its own doc comment) — this boss's own
        // Charge is "basically the exact same shape as Blitz's blitz" per explicit design. Reset to 0 at
        // EnterCharging (fresh charge, fresh progress); meaningless outside Charging.
        public float   chargeTravelProgress;
        // Set by OnInterrupted() when a Charge OR the post-charge reposition dash gets cancelled — Moving's
        // own case checks both this and postChargeDashPending below before its normal cooldown-trigger logic
        // (see HandleActiveState) and, once no longer crowd-controlled, fires straight into Spike Burst
        // instead of a fresh Charge/dash attempt. See OnInterrupted()'s own doc comment for why this holding
        // step exists instead of transitioning straight into Spike Burst, and for which of these two flags
        // gets set for which interrupted state.
        public bool    spikeBurstPending;
        // Set by OnInterrupted() when a Charge (not the reposition dash itself) gets cancelled — same
        // Moving-holding-step shape as spikeBurstPending above, just routing into EnterPostChargeDash instead
        // of straight to Spike Burst, since an interrupted CHARGE still hasn't closed distance on anyone yet.
        public bool    postChargeDashPending;
        public bool    remoteSequenceActive;
        public int     remoteBurstsSpawned;
        // Dual-purpose: counts down to the next remote burst wave while remoteBurstsSpawned < remoteBurstCount;
        // once the last wave is spawned, counts down that wave's own charge time instead, and expiring THEN
        // arms attackCooldownTimer. This is how the cooldown ends up starting only once the last remote
        // burst has finished charging, without KingSpikePD needing to observe the spawned hazards directly.
        public float   remoteSequenceTimer;
        // Persistent Remote Spike Bursts (2026-08-19) — fires a burst at EVERY non-invisible alive player on
        // a flat interval, entirely independent of stateId/the remote-sequence-above's own attack cycle — see
        // Simulate()'s own UpdatePersistentRemoteBursts tail.
        public float   persistentBurstCooldownTimer;
        // Runtime-scaled — see EnemyBaseState.scaledMaxHealth's own doc comment for why these live in State
        // rather than as a plain field mutated by OnConfigureSpawn (2026-09-22 follow-up pass).
        public int   scaledChargesPerAttack;
        public int   scaledRemoteBurstCount;
        public float scaledRemoteBurstInterval;
        public float scaledRotationRateDegPerSecond;
        public float scaledAttackCooldown;
        public float scaledPersistentBurstInterval;
        // Every currently-active (not yet naturally expired) RemoteSpikeBurstPD this boss has spawned, from
        // EITHER the attack-cycle wave (SpawnRemoteBurstWave) or the independent persistent timer
        // (SpawnPersistentRemoteBurst) — both funnel through the shared SpawnRemoteBurstFor, which is where
        // entries get added. Pruned every tick in Simulate() (see its own doc comment) as each tracked burst
        // naturally finishes, so this never grows unbounded. Added 2026-10-16 so Die() can instantly Cancel()
        // every still-active burst instead of the previous "lives fully independently of its firer forever"
        // behavior — see RemoteSpikeBurstPD's own class doc comment for the design change this is part of.
        public DisposableList<PredictedObjectID> activeRemoteBursts;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); activeRemoteBursts.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.activeRemoteBursts = activeRemoteBursts.Duplicate();
            return copy;
        }
    }

    public enum KingSpikeState { Moving, ChargeWindup, Charging, BetweenCharges, PostChargeDash, SpikeBurstWindup }

    private KingSpikeState GetSpikeState(ref State s) => (KingSpikeState)s.stateId;
    private void SetSpikeState(ref State s, KingSpikeState st) => s.stateId = (int)st;

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    // activeRemoteBursts needs its own explicit allocation — unlike b.contactCooldowns/killLaunchHitIds
    // (handled generically by EnemyBasePD's own lazy first-tick init), this field is specific to this
    // subclass's own State, so nothing else initializes it. GetInitialState() is "the one thing reliably
    // re-run on every reuse" (see EnemyBasePD's own doc comment on why pooled reuse can't rely on
    // OnRemovedFromPool) — same reasoning/pattern OrbiterGloomPD.GetInitialState() already uses for its own
    // subclass-specific heldPlebs.
    protected override State GetInitialState()
    {
        var s = base.GetInitialState();
        s.activeRemoteBursts = DisposableList<PredictedObjectID>.Create(4);
        return s;
    }

    // Deliberately INDEPENDENT of CanContactDamage (which this class never overrides, so it stays the base
    // default `true` — contact damage is always possible, in every state) — this boss's Charge is its actual
    // attack; simply touching it while it's Moving/BetweenCharges/etc. is ambient body-touch damage, same as
    // any other enemy's. Only a hit landed while actually Charging should count as a real (non-passive) hit.
    protected override bool ContactDamageIsPassive => GetSpikeState(ref currentState) != KingSpikeState.Charging;

    // Overrides GetOwnDamage(), not GetDamage() itself — GetDamage() is the fixed base-class wrapper that
    // checks held-state first (see its own doc comment); overriding this hook instead means this charge
    // multiplier automatically composes correctly if this boss were ever grabbable in the future, without
    // needing its own held-check re-added here. EnrageDamageMultiplier multiplied in explicitly (2026-08-19)
    // — this override REPLACES the base GetOwnDamage() entirely rather than calling base(), so it doesn't
    // inherit that composition for free the way a non-overriding enemy does.
    protected override float GetOwnDamage() =>
        (KingSpikeState)currentState.stateId == KingSpikeState.Charging ? B.scaledDamage * chargeDamageMultiplier * EnrageDamageMultiplier : B.scaledDamage * EnrageDamageMultiplier;

    [Header("Damage")]
    [SerializeField] private float chargeDamageMultiplier      = 2.5f;
    [SerializeField] private float spikeBurstDamageMultiplier  = 4f;
    [SerializeField] private float remoteBurstDamageMultiplier = 2.5f;

    [Header("Charge")]
    [SerializeField] private float chargeWindupDuration = 0.5f;
    [SerializeField] private float chargeSpeedMultiplier = 3.5f;
    // EffectiveMoveSpeed, not baseMoveSpeed (2026-08-13) — see BlitzGloomPD.EnterBlitz's own comment. This
    // is the whole reason the Boss enrage Move Speed stacking exists in the first place: without this,
    // Charge (King Spike's actual threat) never got any faster no matter how long the fight dragged on.
    private float ChargeSpeed => EffectiveMoveSpeed * chargeSpeedMultiplier;
    [SerializeField] private float chargeDistance       = 20f;
    // Tier1-authored, pristine read-only baseline forever (2026-09-22) — scaled fresh into
    // State.scaledChargesPerAttack by OnConfigureSpawn() (see EnemyBaseState.scaledMaxHealth's own doc
    // comment for the full reasoning).
    [SerializeField] private int   chargesPerAttack     = 4;
    [SerializeField] private float betweenChargePause   = 0.1f;
    [SerializeField] private GameObject   chargeWindupIndicator;
    [SerializeField] private WindupFillPD chargeWindupFill;
    // Charging unparents chargeWindupIndicator to let it track the actual travel corridor as it shrinks
    // (2026-09-14 — see UpdateView's own doc comment), so these are needed to know where/how to put it back
    // once Charging ends — exact same pattern as BlitzGloomPD's own _windupIndicatorParent/
    // _windupIndicatorLocalPos/_windupIndicatorLocalRot (see that class's own doc comments for the full "why"
    // on each, including the worldPositionStays:true reparent bug already fixed there).
    private Transform  _windupIndicatorParent;
    private Vector3    _windupIndicatorLocalPos;
    private Quaternion _windupIndicatorLocalRot;
    private bool       _windupIndicatorLocalPoseCaptured;
    // Max turn rate while tracking a target during ChargeWindup (2026-08-21) — copied from
    // ShielderGloomPD's own rotationRateDegPerSecond/rotationRateDegPerSecondTierGrowth (45 base, 0.2 tier
    // growth), same pristine-baseline treatment as chargesPerAttack above. Caps how fast this boss can
    // re-aim mid-windup so circling it (or timing a dash to arrive late in the windup) is a real counterplay
    // option — see TrackChargeWindupTarget's own doc comment.
    [SerializeField] private float rotationRateDegPerSecond = 45f;

    [Header("Post-Charge Reposition")]
    // Closes distance on the closest player right before Spike Burst — see EnterPostChargeDash's own doc
    // comment for why this phase exists (2026-09-02: this boss used to fire its AoE from wherever its last
    // Charge/interrupt happened to leave it, often nowhere near anyone). Same "dash toward a live target,
    // stop short at an offset, 180° = stop BETWEEN dasher and target" shape as SpearGloomPD.EnterDashToTarget,
    // via the same shared StartOffsetChase primitive.
    [SerializeField] private float postChargeDashOffsetDistance = 5f;
    // EffectiveMoveSpeed * this (2x move speed default) — deliberately its own tunable, not a reuse of
    // ChargeSpeed's own chargeSpeedMultiplier; this is a repositioning move, not the real Charge attack, and
    // shouldn't be forced to share that attack's own pacing/tuning.
    [SerializeField] private float postChargeDashSpeedMultiplier = 2f;
    private float PostChargeDashSpeed => EffectiveMoveSpeed * postChargeDashSpeedMultiplier;

    [Header("Spike Burst")]
    [SerializeField] private float spikeBurstChargeTime = 1.5f;
    // Flat, no tier growth (2026-08-31, explicit — was scaled per tier by spikeBurstRadiusTierGrowth; that
    // field and the whole authored-snapshot dance for this one are gone, it's just a plain constant now).
    [SerializeField] private float spikeBurstRadius     = 18f;
    [SerializeField] private GameObject   spikeBurstIndicator;
    [SerializeField] private WindupFillPD spikeBurstFill;

    [Header("Remote Spike Bursts")]
    [SerializeField] private GameObject remoteBurstPrefab;
    [SerializeField] private float      remoteBurstOffsetRadius = 3f;
    [SerializeField] private float      remoteBurstRadius       = 6f;
    // Tier1-authored, pristine read-only baseline forever — scaled fresh into State.scaledRemoteBurstCount.
    [SerializeField] private int        remoteBurstCount        = 8;
    // Tier1-authored, pristine read-only baseline forever — scaled fresh into
    // State.scaledRemoteBurstInterval — SHRINKS per tier ("20% shorter").
    [SerializeField] private float      remoteBurstInterval     = 1f;
    [SerializeField] private float      remoteChargeTime        = 1.5f;

    [Header("Persistent Remote Spike Bursts")]
    // Fires a Remote Spike Burst at EVERY non-invisible alive player on a flat interval, entirely independent
    // of this boss's own attack sequence/state (runs regardless of stateId — see Simulate()'s own
    // UpdatePersistentRemoteBursts tail). Same targeting/damage/radius/charge-time parameters as
    // SpawnRemoteBurstWave's own bursts — SpawnRemoteBurstFor is shared by both — just a different cadence
    // and an added invisible-player exclusion (SpawnRemoteBurstWave itself deliberately doesn't filter
    // invisible players: that's this boss's own attack sequence, which only ever fires after a real target
    // was found the normal way; this persistent version fires blind on a timer, so it needs its own explicit
    // check, same shape as EnemyBasePD.FaceTargetSmooth's own IsInvisible/IsTaunting guard).
    [SerializeField] private float persistentBurstInterval = 3f;

    [Header("Attack Cooldown")]
    // Only actually starts counting down once the whole remote burst sequence finishes (all waves spawned
    // AND the final wave's own remoteChargeTime elapsed) — see Simulate()'s own remote-sequence tail.
    [SerializeField] private float attackCooldown = 10f;

    [Header("Tier Scaling")]
    // Duration fields SHRINK per tier (base * (1 - (tier-1)*growth)) — "X% shorter/faster" means less time,
    // not more. Size/count fields GROW per tier — see each field's own default/comment. Linear per tier off
    // the tier1-authored base in all cases, never compounding/exponential (user's explicit "multiplicative
    // fields multiply base, no exponential tier scaling" rule).
    [SerializeField] private int   chargesPerAttackTierGrowth    = 2;
    [SerializeField] private int   remoteBurstCountTierGrowth    = 4;
    [SerializeField] private float remoteBurstIntervalTierGrowth = 0.2f;
    // Same linear-per-tier shape as every other *TierGrowth field — see ShielderGloomPD's own identical
    // field for the precedent this was copied from.
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void LateAwake()
    {
        base.LateAwake();

        // Guarded to a single real capture per instance — see LeaperGloomPD/BlitzGloomPD's own identical
        // _windupIndicatorParent/_windupIndicatorLocalPos capture for the full reasoning (a pooled reuse
        // could already be mid-Charging, unparented, by the time LateAwake() re-fires).
        if (chargeWindupIndicator != null) _windupIndicatorParent ??= chargeWindupIndicator.transform.parent;
        if (chargeWindupIndicator != null && !_windupIndicatorLocalPoseCaptured)
        {
            _windupIndicatorLocalPos = chargeWindupIndicator.transform.localPosition;
            _windupIndicatorLocalRot = chargeWindupIndicator.transform.localRotation;
            _windupIndicatorLocalPoseCaptured = true;
        }

        // Full cooldown grace period on spawn (2026-08-12: changed from half — this boss's opening attack
        // would otherwise fire before players have even finished loading in) — plain currentState
        // assignment, so even if LateAwake somehow re-fires on a pooled reuse this is harmless (idempotent),
        // unlike an accumulating event subscription would be.
        currentState.attackCooldownTimer = attackCooldown;
        // Same full-grace reasoning, and same "avoid the previous cooldown-starts-at-0-for-a-frame bug"
        // requirement explicitly called out for this mechanic — a plain currentState assignment here, BEFORE
        // OnConfigureSpawn scales persistentBurstInterval for real, means the very first Simulate() tick this
        // life ever runs already sees a full (if not yet correctly tier/difficulty-scaled) cooldown instead
        // of the struct's own zero-value default, which would otherwise fire a burst on frame one.
        currentState.persistentBurstCooldownTimer = persistentBurstInterval;
    }

    // Tier1-authored chargesPerAttack/remoteBurstCount/remoteBurstInterval, scaled by each field's own
    // *TierGrowth (see the "Tier Scaling" header's own doc comment for the duration-shrinks/size-grows
    // formula shapes). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call). All 3 are read live during gameplay off State.scaledX now (chargesPerAttack/
    // remoteBurstCount via refill-if-exhausted checks, remoteBurstInterval read directly) — see
    // EnemyBaseState.scaledMaxHealth's own doc comment for why. spikeBurstRadius is flat now, no tier
    // scaling — see its own field comment.
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        currentState.scaledChargesPerAttack    = chargesPerAttack    + (spawnTier - 1) * chargesPerAttackTierGrowth;
        currentState.scaledRemoteBurstCount    = remoteBurstCount    + (spawnTier - 1) * remoteBurstCountTierGrowth;
        currentState.scaledRemoteBurstInterval = remoteBurstInterval * (1f - (spawnTier - 1) * remoteBurstIntervalTierGrowth);
        currentState.scaledRotationRateDegPerSecond = rotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        currentState.scaledAttackCooldown = attackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;

        // Same Threat Level 5 scaling, explicitly requested to apply to Boss Persistent Attacks too — re-set
        // the placeholder persistentBurstCooldownTimer LateAwake() already assigned above, using the
        // now-scaled value.
        currentState.scaledPersistentBurstInterval = persistentBurstInterval * attackCooldownMultiplier;
        currentState.persistentBurstCooldownTimer = currentState.scaledPersistentBurstInterval;
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void Simulate(ref State state, float delta)
    {
        base.Simulate(ref state, delta);

        // Remote burst sequencing runs independent of HandleActiveState's FSM switch below — the boss is
        // already back in Moving by the time this needs to keep ticking (see FireSpikeBurst()). B.wiped
        // added (2026-08-12) — base.Simulate()'s own wiped early-return above only stops ITS OWN body (this
        // override's code below still runs unconditionally after that call returns, same as any other
        // base-call-then-continue override), so without this a King Spike wiped mid-remote-sequence (e.g.
        // EnemySpawnerPD.WipeAllEnemies right before a new boss spawns) would keep spawning MORE
        // RemoteSpikeBurstPD waves indefinitely from an invisible, supposedly-gone boss — same underlying
        // "wipe didn't actually stop simulation" bug as the one confirmed live for LeaperGloomPD's own
        // stuck windup indicator, just manifesting as an ongoing side effect instead of a stuck visual.
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (B.isDead || B.wiped) return;

        // Prunes activeRemoteBursts of anything that's naturally finished on its own (IsExpired) or no
        // longer resolves (pooled/deleted) — see that field's own doc comment for why this is needed every
        // tick to keep the list from growing unbounded. Backwards iteration so RemoveAt doesn't skip the
        // next entry. Deliberately simple/linear — activeRemoteBursts is always small (a handful of bursts
        // at most), so this is cheap even every tick.
        for (int i = state.activeRemoteBursts.Count - 1; i >= 0; i--)
        {
            var go = hierarchy.GetGameObject(state.activeRemoteBursts[i]);
            if (go == null || !go.TryGetComponent(out RemoteSpikeBurstPD existingBurst) || existingBurst.IsExpired)
                state.activeRemoteBursts.RemoveAt(i);
        }

        // Drives the charge windup indicator's own "collapse in behind the body as it travels" shrink
        // (2026-09-14) — see UpdateView's own doc comment for the full visual reasoning, and
        // BlitzGloomPD.Simulate's own identical computation (this boss's own Charge is "basically the exact
        // same shape as Blitz's blitz" per explicit design). MUST run here, in this override, not inside
        // HandleActiveState (which is where it originally lived) — EnemyBasePD.Simulate() only ever calls
        // HandleActiveState while !b.isDashing (see its own dash-dispatch block), so placing this inside
        // HandleActiveState meant it was SKIPPED ENTIRELY for this boss's own ENTIRE Charging duration, the
        // one state that actually needed it — confirmed live: the shrink never advanced past its initial 0 at
        // all. Placed here, ahead of the remoteSequenceActive early-return below, same "runs regardless of
        // stateId/remoteSequenceActive" reasoning UpdatePersistentRemoteBursts' own placement already uses —
        // Charging has nothing to do with the remote-burst sequence and must never be gated behind it.
        if (GetSpikeState(ref state) == KingSpikeState.Charging)
        {
            Vector3 destination = state.b.dashDestination;
            Vector3 origin = destination - state.chargeDirection * chargeDistance;
            float traveled = chargeDistance > 0.0001f
                ? Vector3.Dot(transform.position - origin, state.chargeDirection)
                : chargeDistance;
            state.chargeTravelProgress = Mathf.Clamp01(traveled / Mathf.Max(chargeDistance, 0.0001f));
        }

        // Runs regardless of stateId/remoteSequenceActive — see persistentBurstInterval's own doc comment
        // ("REGARDLESS of King Spike's current state", per explicit design). Placed here, ahead of the
        // remoteSequenceActive early-return below, specifically so it isn't gated behind this boss's own
        // attack-sequence-in-progress check.
        UpdatePersistentRemoteBursts(ref state, delta);

        if (!state.remoteSequenceActive) return;

        state.remoteSequenceTimer -= delta;
        if (state.remoteSequenceTimer > 0f) return;

        if (state.remoteBurstsSpawned < state.scaledRemoteBurstCount)
        {
            SpawnRemoteBurstWave();
            state.remoteBurstsSpawned++;
            state.remoteSequenceTimer = state.remoteBurstsSpawned < state.scaledRemoteBurstCount
                ? state.scaledRemoteBurstInterval
                : remoteChargeTime;
        }
        else
        {
            state.attackCooldownTimer = state.scaledAttackCooldown;
            state.remoteSequenceActive = false;
        }
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded)
        {
            // Two entirely different dashes fire this same callback — a real Charge (StartDashToPoint) and
            // the new PostChargeDash repositioning move (StartOffsetChase/StartDashToPoint, see
            // EnterPostChargeDash's own doc comment) — disambiguate by the state.stateId neither dash
            // primitive itself ever touches, so it's still whatever this method last set it to.
            if (GetSpikeState(ref state) == KingSpikeState.PostChargeDash)
            {
                // The repositioning dash itself just finished — now actually fire, having closed distance
                // on someone, instead of from wherever the last Charge/interrupt happened to end.
                EnterSpikeBurstWindup(ref state);
            }
            else
            {
                // A real Charge just ended. Decrement moved to EnterChargeWindup (attack START, not this
                // completion point) — see its own doc comment for why. chargesRemaining already reflects
                // the charge that just ended by the time we get here.
                // IsTargetStillValid (also covers a null target, same as everyone else's explicit != null
                // check) — a target that went invisible-and-non-taunting since the last charge shouldn't get
                // chained into another one against them; see that property's own doc comment.
                // !IsCrowdControlled — a true Stun() landing mid-sequence never interrupts the charge that
                // just ended above, but stops the chain from continuing into another one, falling through to
                // the post-charge reposition instead (a Slow does NOT stop the chain — see
                // IsCrowdControlled's own doc comment, hard CC only).
                if (state.chargesRemaining > 0 && IsTargetStillValid && !IsCrowdControlled) EnterBetweenCharges(ref state);
                else                                                  EnterPostChargeDash(ref state);
            }
        }

        var ss = GetSpikeState(ref state);

        // Only ticks while actually in Moving (2026-08-19 — uniform across every multi-phase enemy's own
        // attack-cadence cooldown now, see OrbiterGloomPD's own fix) — frozen through the whole Charge/
        // BetweenCharges/SpikeBurst/RemoteBursts sequence rather than ticking (and going increasingly
        // negative) throughout it.
        if (ss == KingSpikeState.Moving)
            state.attackCooldownTimer -= delta;

        // stateTimer scales live with EffectiveAttackSpeed (which includes any active slow) for
        // ChargeWindup/BetweenCharges, same pattern as every other enemy's own windup/recovery timers —
        // King Spike is slowable like everyone else. SpikeBurstWindup is the one deliberate exception (per
        // the user's own call, matching "SpikeBurst uninterruptable"/"RemoteSpikeBurst ignores stuns" —
        // both already fully CC-immune categories): it stays on a fixed real-time clock, achieved simply by
        // never reading EffectiveAttackSpeed for it rather than any cross-cutting immunity flag (see
        // EffectiveAttackSpeed's own doc comment). Moving doesn't meaningfully use stateTimer either way.
        state.stateTimer -= delta * (ss == KingSpikeState.SpikeBurstWindup ? 1f : EffectiveAttackSpeed);

        switch (ss)
        {
            case KingSpikeState.Moving:
                // An interrupted Charge/reposition-dash routes here with spikeBurstPending OR
                // postChargeDashPending set (see OnInterrupted()'s own doc comment for which one, and why)
                // instead of straight into Spike Burst/the reposition dash — Spike Burst's own windup is
                // deliberately stun-immune once entered (fixed real-time clock, ignores EffectiveAttackSpeed/
                // isStunnable — see this switch's SpikeBurstWindup case), so jumping there immediately would
                // let a Stun() paired with the same hit as the interrupt sail straight into an unstoppable
                // burst. Moving IS stunnable, so parking here first means a paired true stun freezes this
                // whole method before it ever reaches this branch (via Simulate()'s own isStunnable-gated
                // freeze) — this branch only actually runs once not truly stunned (IsCrowdControlled is hard
                // CC only, i.e. IsTrueStunned — a Slow does NOT hold this open, see its own doc comment), at
                // which point it fires straight into whichever of the two the interrupted state called for,
                // instead of the normal cooldown/charge-trigger logic below.
                if (state.spikeBurstPending || state.postChargeDashPending)
                {
                    if (IsCrowdControlled)
                    {
                        B.scaledMoveSpeed = baseMoveSpeed;
                        if (_target != null) HandleMovement(ref state, delta);
                    }
                    else if (state.spikeBurstPending)
                    {
                        state.spikeBurstPending = false;
                        EnterSpikeBurstWindup(ref state);
                    }
                    else
                    {
                        state.postChargeDashPending = false;
                        EnterPostChargeDash(ref state);
                    }
                    break;
                }

                // attackCooldownTimer decrements continuously through the whole charge/windup/burst
                // sequence (never reset upward mid-sequence), so without the remoteSequenceActive check
                // here it would already read as "ready" the instant the boss returns to Moving after
                // FireSpikeBurst — immediately re-triggering (or surging toward) a new attack while the
                // previous attack's remote bursts are still resolving. Per point 6, the next attack can't
                // begin until that sequence is fully done.
                bool cooldownReady = state.attackCooldownTimer <= 0f && !state.remoteSequenceActive;
                B.scaledMoveSpeed = cooldownReady ? baseMoveSpeed * 5f : baseMoveSpeed;
                if (_target != null) HandleMovement(ref state, delta);
                if (cooldownReady && _target != null &&
                    Vector3.Distance(transform.position, _target.position) <= chargeDistance)
                {
                    // chargesRemaining itself is handled by EnterChargeWindup now, not here — see its own
                    // doc comment for why.
                    // Ongoing attack starts here — see CaptureAttackAnchor's own doc comment.
                    CaptureAttackAnchor(ref state);
                    EnterChargeWindup(ref state, delta);
                }
                break;

            case KingSpikeState.ChargeWindup:
                TrackChargeWindupTarget(ref state, delta);
                if (state.stateTimer <= 0f) EnterCharging(ref state);
                break;

            case KingSpikeState.BetweenCharges:
                // Outside the ongoing window (ongoing ends the moment a single Charging finishes) — safe to
                // read live _target here, deciding whether/how the NEXT charge in the chain proceeds.
                if (state.stateTimer <= 0f)
                {
                    if (_target == null)
                    {
                        // No valid target for another charge — skip straight to the post-charge reposition
                        // instead of winding up at nobody. Unlike every other multi-stage enemy, this does
                        // NOT abort to Moving: SpikeBurstWindup/FireSpikeBurst/RemoteSpikeBurstPD never need
                        // a target (always hit every player directly via CharacterStatsPD.All), so there's
                        // no reason to cut the sequence short.
                        EnterPostChargeDash(ref state);
                    }
                    else
                    {
                        CaptureAttackAnchor(ref state);
                        EnterChargeWindup(ref state, delta);
                    }
                }
                break;

            case KingSpikeState.SpikeBurstWindup:
                if (state.stateTimer <= 0f) FireSpikeBurst(ref state);
                break;
        }

        ss = GetSpikeState(ref state);

        isStunnable = ss == KingSpikeState.Moving ? !stunResistant : false;

        // Kinematic (not collider-disable) whenever the boss is genuinely standing still — ChargeWindup,
        // BetweenCharges, and SpikeBurstWindup all zero velocity (matches old KingSpike's FixedUpdate,
        // which explicitly stopped the rigidbody for all three) and are stationary telegraphed-attack
        // windows, so the goal is "immovable but still physically present" (a solid obstacle players have
        // to path around, can still push other things, can't itself be knocked off its own indicator's
        // position) rather than "non-interacting." Moving needs to stay dynamic (HandleMovement drives it
        // via AddForce, which a kinematic body ignores), and Charging/PostChargeDash both need to stay
        // dynamic AND collider-disabled (fast movement — Case 1, non-interacting, handled entirely by the
        // dash primitives already — StartDashToPoint/StartOffsetChase both disable the collider themselves,
        // same self-contained shape either dash uses). isStunnable is already false for the entire attack
        // routine above, a superset of every kinematic window, so AddKnockbackVelocity's own
        // `if (!isStunnable ...) return;` guard already ensures knockback is never attempted against this
        // rigidbody while it's kinematic.
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = ss != KingSpikeState.Moving && ss != KingSpikeState.Charging && ss != KingSpikeState.PostChargeDash;
            // Zero velocity on the entering edge only (not every tick while already kinematic) — setting
            // velocity on an already-kinematic PredictedRigidbody throws ("not supported").
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }
    }

    // ── State transitions ────────────────────────────────────────────────────

    private void EnterChargeWindup(ref State state, float delta)
    {
        // Resets the Moving-state "rushing toward target" 5x speed boost (see HandleActiveState's own
        // moveSpeed = cooldownReady ? baseMoveSpeed * 5f : baseMoveSpeed) back down to normal (2026-08-19
        // fix) — that assignment only ever runs from the Moving case, so without this reset, moveSpeed stays
        // frozen at its boosted 5x value all the way through ChargeWindup and into EnterCharging(), which
        // reads ChargeSpeed => EffectiveMoveSpeed * chargeSpeedMultiplier. EffectiveMoveSpeed reads this same
        // stateful moveSpeed field (not the stable baseMoveSpeed), so the actual Charge dash was silently
        // launching at up to 5x its intended speed on top of chargeSpeedMultiplier — confirmed live, this was
        // NOT a pooling issue (LateAwake() already correctly resets moveSpeed on every reuse); it happened on
        // every single charge attempt, every time.
        B.scaledMoveSpeed = baseMoveSpeed;

        // Decrement lives HERE — the actual START of one charge attempt (dashtotarget/windup) — not at the
        // charge's own completion (dashJustEnded, above). A charge cancelled mid-windup or mid-dash via
        // Interrupt() now still counts against the budget, since chargesRemaining already dropped the
        // instant this ran; the old completion-gated decrement meant an interrupted charge was never counted
        // at all, letting a fresh Moving->attack cycle hand out a full chargesPerAttack again on top of it.
        // Refill-if-exhausted (not an unconditional reset) — called from both a fresh Moving-triggered
        // attack (chargesRemaining sitting at 0 from the previous fully-completed sequence, or a pooled
        // spawn) AND EnterBetweenCharges' own mid-chain continuation (chargesRemaining already > 0, so this
        // is a no-op there). This is also why Moving's own attack-trigger and OnInterrupted() no longer
        // stomp chargesRemaining themselves — this single self-contained check is now the only reset point,
        // so an interrupted sequence's leftover count survives into whatever attack comes next instead of
        // being silently thrown away.
        if (state.chargesRemaining <= 0) state.chargesRemaining = state.scaledChargesPerAttack;
        state.chargesRemaining--;

        // Captured here — the commit moment for this charge — not read live off _targetStats later; same
        // "commit once, keep reading that same reference" contract BlitzGloomPD's own _windupTrackedTarget
        // uses (immune to _targetStats getting reassigned to someone else mid-windup via taunt/invisible-
        // retarget). See TrackChargeWindupTarget's own doc comment.
        _chargeWindupTrackedTarget = _targetStats;
        state.stateTimer = chargeWindupDuration;
        SetSpikeState(ref state, KingSpikeState.ChargeWindup);
        // Snap to face the target FIRST (2026-08-24 fix), THEN let TrackChargeWindupTarget's own
        // rate-limited tracking take over from subsequent ticks — without this, the body kept whatever
        // rotation the PREVIOUS charge (or Moving's own chase) left it at, and rotationRateDegPerSecond alone
        // often isn't fast enough to fully turn around before the windup ends if the target's now roughly
        // behind it — confirmed live: this boss visibly kept charging in roughly the same direction over and
        // over instead of properly re-aiming at a player who'd since circled behind it. Only writes
        // chargeDirection on a non-zero return — see EnemyBasePD.SnapFaceTarget(CharacterStatsPD, bool) own
        // doc comment for the guard/degenerate cases that return Vector3.zero.
        Vector3 snapDir = SnapFaceTarget(_chargeWindupTrackedTarget);
        if (snapDir != Vector3.zero)
            state.chargeDirection = snapDir;
    }

    // The SPECIFIC player this ChargeWindup committed to, captured once at EnterChargeWindup — see its own
    // doc comment. Plain field (not State), same convention as BlitzGloomPD's own _windupTrackedTarget. Only
    // ever read while actually in ChargeWindup.
    private IFriendlyPD _chargeWindupTrackedTarget;

    // Re-faces the body toward _chargeWindupTrackedTarget's LIVE position every ChargeWindup tick, capped at
    // rotationRateDegPerSecond, via Vector3.RotateTowards off state.chargeDirection — the PREDICTED-STATE
    // value, not transform.rotation (2026-08-24 defensive fix, mirroring the confirmed-live bug fix on
    // BlitzGloomPD.TrackWindupTarget — see its own doc comment for the full root cause). This boss doesn't
    // currently hit that exact bug (chained charges route through BetweenCharges, an interstitial state whose
    // own pause defers EnterChargeWindup to a LATER tick — never the same tick as a preliminary
    // dashJustEnded-style block, so this method was never called same-tick as a fresh MoveRotation snap), but
    // it shared the identical fragile "read transform.rotation as the rotate-from basis" dependency — that
    // read lags a physics step behind any MoveRotation call, so ANY future same-tick double-processing here
    // would silently reproduce Blitz's exact bug. state.chargeDirection is written/read synchronously within
    // the same Simulate() tick, immune to that lag regardless of call count. Keeps state.chargeDirection
    // updated every tick, so whatever EnterCharging reads the instant ChargeWindup ends is always the
    // freshest reachable direction — the charge windup indicator (a Visuals child) now also reads this exact
    // same real body rotation via plain passive inheritance (2026-09-14, see UpdateView's own doc comment),
    // no separate plain-field snapshot needed for it anymore.
    // No-op (holds last direction) if the target's died/gone invisible-without-taunting mid-windup, or if the
    // computed direction is degenerate (target exactly on top of this boss). Rate scaled by
    // EffectiveAttackSpeed (2026-08-31) — was plain delta; now the boss's own re-aim turn rate speeds up in
    // step with the windup duration it's compressed into (see EnemySpawnerPD.difficultyAttackSpeedGrowthPerLevel's
    // own doc comment for why the windup itself already sped up but this tracking rate previously hadn't),
    // matching ShielderGloomPD's own SpikeShieldPD rotation tracking, which already did this.
    private void TrackChargeWindupTarget(ref State state, float delta)
    {
        if (_chargeWindupTrackedTarget == null || _chargeWindupTrackedTarget.IsDead ||
            (_chargeWindupTrackedTarget.IsInvisible && !_chargeWindupTrackedTarget.IsTaunting)) return;

        Vector3 dir = _chargeWindupTrackedTarget.transform.position - transform.position;
        dir.y = 0f;
        if (dir == Vector3.zero) return;
        dir.Normalize();

        Vector3 newDir = Vector3.RotateTowards(state.chargeDirection, dir, state.scaledRotationRateDegPerSecond * Mathf.Deg2Rad * delta * EffectiveAttackSpeed, 0f);
        _rigidbody.MoveRotation(Quaternion.LookRotation(newDir));
        state.chargeDirection = newDir;
    }

    private void EnterCharging(ref State state)
    {
        Vector3 endPoint = transform.position + state.chargeDirection * chargeDistance;
        SetSpikeState(ref state, KingSpikeState.Charging);
        state.chargeTravelProgress = 0f; // fresh charge, fresh progress — see its own doc comment on State
        StartDashToPoint(ref state, ChargeSpeed, endPoint);
    }

    // Reparents chargeWindupIndicator back under _windupIndicatorParent AND restores its exact authored local
    // pose — see LeaperGloomPD.ReparentWindupIndicatorHome's own doc comment for why this restores the pose
    // explicitly instead of trusting SetParent(parent, true)'s own worldPositionStays.
    private void ReparentWindupIndicatorHome()
    {
        if (chargeWindupIndicator == null) return;
        chargeWindupIndicator.transform.SetParent(_windupIndicatorParent, false);
        chargeWindupIndicator.transform.localPosition = _windupIndicatorLocalPos;
        chargeWindupIndicator.transform.localRotation = _windupIndicatorLocalRot;
    }

    private void EnterBetweenCharges(ref State state)
    {
        state.stateTimer = betweenChargePause;
        SetSpikeState(ref state, KingSpikeState.BetweenCharges);
    }

    // Funnel point for EVERY path that used to enter Spike Burst directly (2026-09-02, new phase) — the end
    // of a charge chain, "no valid target for another charge" in BetweenCharges, and an interrupted Charge's
    // own spikeBurstPending routing in Moving. All three used to jump straight into SpikeBurstWindup from
    // wherever the boss's last Charge (or interrupt) happened to leave it — often nowhere near any player,
    // firing the AoE on empty ground. This closes distance on the closest player first, same "dash toward a
    // live target, stop short at an offset, 180° = stop BETWEEN dasher and target" shape as
    // SpearGloomPD.EnterDashToTarget, via the shared StartOffsetChase primitive — HandleActiveState's own
    // dashJustEnded block (see its own PostChargeDash branch) fires EnterSpikeBurstWindup once this arrives.
    //
    // Re-derives _target/_targetStats fresh here — deliberately NOT whatever this attack sequence's own
    // _target happened to be (e.g. a taunting-but-far player from BetweenCharges' chain) — "closest player,"
    // per explicit design, not whoever this boss was already chasing. EXPLICITLY nulled before calling
    // FindClosestTarget() (not left as-is) — that method's own "leave untouched if nothing found" contract
    // (see its own doc comment) means a stale _target from before this call — e.g. someone who's since died
    // or gone invisible-without-taunting — would otherwise silently survive an unsuccessful scan instead of
    // correctly reading as "no valid target." Nulling first guarantees _target is EITHER a genuinely fresh,
    // currently-valid candidate, OR null — nothing in between, no stale-reference ambiguity.
    //
    // No valid candidate right now (every player invisible-and-not-taunting, or dead, confirmed by the
    // guaranteed-clean null above) falls back to a plain fixed-point dash (StartDashToPoint) straight at
    // lastKnownTargetPosition — this boss's own generic "wherever a target was last actually seen" record —
    // rather than not moving at all, per explicit design. No offset applied to that fallback destination
    // (unlike the live-target case) — there's no live target to stop "between" this boss and, so it just
    // dashes to the point itself.
    //
    // hasLastKnownTargetPosition guards the degenerate edge of that fallback (2026-09-02, confirmed real,
    // if rare) — lastKnownTargetPosition defaults to Vector3.zero (world origin) until it's genuinely been
    // assigned at least once; without this check, an enemy that's never once had a valid target since
    // spawning would dash straight at the world origin instead of resolving sanely. Treated the same as
    // "already close enough" below — skip the dash, fire Spike Burst immediately, no meaningless reposition.
    //
    // Already within postChargeDashOffsetDistance of the destination — skips the dash entirely and fires
    // Spike Burst immediately instead (explicit design) — a "dash" that's already shorter than its own
    // stop-short offset would just awkwardly snap-turn in place rather than actually closing any distance.
    private void EnterPostChargeDash(ref State state)
    {
        _target = null;
        _targetStats = null;
        FindClosestTarget();

        if (_target == null && !hasLastKnownTargetPosition)
        {
            EnterSpikeBurstWindup(ref state);
            return;
        }

        Vector3 destination = _target != null ? _target.position : lastKnownTargetPosition;
        if (Vector3.Distance(transform.position, destination) <= postChargeDashOffsetDistance)
        {
            EnterSpikeBurstWindup(ref state);
            return;
        }

        SetSpikeState(ref state, KingSpikeState.PostChargeDash);
        if (_target != null)
            // No-angle variant (2026-09-03, was the 180°-locked overload) — just needs to end up anywhere
            // within postChargeDashOffsetDistance of the target, see EnemyBasePD.StartOffsetChase's own
            // no-angle overload doc comment for why locking a specific bearing was never actually necessary.
            StartOffsetChase(ref state, PostChargeDashSpeed, postChargeDashOffsetDistance);
        else
            StartDashToPoint(ref state, PostChargeDashSpeed, destination);
    }

    private void EnterSpikeBurstWindup(ref State state)
    {
        state.stateTimer = spikeBurstChargeTime;
        SetSpikeState(ref state, KingSpikeState.SpikeBurstWindup);
    }

    // See SpearGloomPD._stabOverlapResults's own doc comment for the buffer-size reasoning.
    private static readonly Collider[] _spikeBurstOverlapResults = new Collider[8];

    private void FireSpikeBurst(ref State state)
    {
        Vector3 origin = transform.position;
        // EnrageDamageMultiplier multiplied in explicitly (2026-08-19) — this reads B.scaledDamage directly
        // rather than going through GetOwnDamage()/GetDamage(), so it doesn't pick the bonus up for free.
        float   dmg    = B.scaledDamage * spikeBurstDamageMultiplier * EnrageDamageMultiplier;
        // Physics.OverlapSphereNonAlloc against CombatLayerMasks.FriendlyHurtboxMask (2026-08-30, was a
        // manual FriendlyTargetRegistryPD distance check) — see SpearGloomPD.EnterStab's own doc comment for
        // why enemy AoE attacks now match every player weapon's own collider-query convention.
        int n = Physics.OverlapSphereNonAlloc(origin, spikeBurstRadius, _spikeBurstOverlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _spikeBurstOverlapResults[i].GetComponent<IFriendlyPD>();
            // No IsInvincible pre-check (2026-08-24 fix) — TakeDamage() already no-ops safely against
            // invincibility on its own, AND is the only place Dodge credit gets awarded (see
            // CharacterStatsPD.NotifyDodge's own doc comment); skipping the call here silently ate every
            // Dodge this attack should have granted while the player was dash-invincible.
            if (stats == null || stats.IsDead) continue;
            stats.TakeDamage(dmg, false, ObjectId);
        }

        // Boss returns to Moving immediately (point 5) — the remote sequence keeps ticking independently
        // via Simulate() above. attackCooldownTimer is deliberately NOT touched here (point 6) — the whole
        // sequence has to actually finish before the boss can attack again, which post-enrage never
        // happens (see Simulate()'s own comment).
        state.remoteSequenceActive = true;
        state.remoteBurstsSpawned  = 0;
        state.remoteSequenceTimer  = state.scaledRemoteBurstInterval;
        // Reprioritize now that the attack sequence is over — random, not closest, per this boss's own
        // design (see EnemyBasePD.FindRandomTarget's own doc comment for why).
        FindRandomTarget();
        SetSpikeState(ref state, KingSpikeState.Moving);
    }

    // Reworked (2026-08-12, reverses the old "chargesRemaining deliberately left untouched" design) — an
    // interrupted Charge no longer just falls back to Moving and lets a fresh charge attempt restart
    // (chargesRemaining used to be preserved specifically to prevent handing out extra charges on a retry,
    // but the retry itself is exactly what's being removed now). Zeroes chargesRemaining outright and routes
    // toward the post-charge reposition instead (2026-09-02: was Spike Burst directly, before that phase
    // existed) — the same destination this boss's own natural "ran out of chargesRemaining"/"no valid
    // target" paths already reach — via spikeBurstPending rather than calling EnterPostChargeDash() directly
    // here;
    // see Moving's own case in HandleActiveState for why that indirection exists (letting a paired stun
    // freeze this boss first, since Spike Burst itself can't be stunned once entered). No separate cooldown
    // write needed — Spike Burst's own remote-burst sequence already ends the whole attack cycle on a flat,
    // full attackCooldown once it resolves (see Simulate()'s own tail). remoteSequenceActive/
    // remoteBurstsSpawned untouched — those only ever get armed from FireSpikeBurst, which CanBeInterrupted
    // below already keeps this from ever running during.
    protected override void OnInterrupted()
    {
        // Which flag to set depends on WHICH state was actually interrupted (2026-09-02) — read BEFORE
        // SetSpikeState below overwrites it. A Charge/ChargeWindup/BetweenCharges interrupt still hasn't
        // closed distance on anyone yet, so it routes through the reposition dash first
        // (postChargeDashPending). The reposition dash ITSELF being interrupted means this boss already
        // started closing distance — no reason to make it retry that dash, so this goes straight to Spike
        // Burst instead (spikeBurstPending), firing the instant the resulting interrupt-stun actually wears
        // off (see Moving's own case for that holding step).
        bool interruptedDuringPostChargeDash = GetSpikeState(ref currentState) == KingSpikeState.PostChargeDash;

        currentState.chargesRemaining = 0;
        if (interruptedDuringPostChargeDash)
            currentState.spikeBurstPending = true;
        else
            currentState.postChargeDashPending = true;
        SetSpikeState(ref currentState, KingSpikeState.Moving);

        // Predicted-instant hide, not left for UpdateView's own stateId-driven redraw to eventually catch
        // up — Interrupt() mutates currentState out-of-band (from a different identity's own Simulate(),
        // not this enemy's own HandleActiveState flow this tick), which was leaving the windup indicator
        // visibly stuck for a stray tick before UpdateView's viewState caught up to the interrupt.
        // SpikeBurstWindup/spikeBurstFill untouched — that phase isn't interruptible in the first place
        // (see CanBeInterrupted).
        // _lastVisualState deliberately NOT written here — that field is UpdateView's own edge-detection
        // bookkeeping. Setting it here would make UpdateView's next real call see ss == _lastVisualState and
        // bail out early via its own guard, silently skipping whatever else that guard protects.
        if (chargeWindupIndicator != null) chargeWindupIndicator.SetActive(false);
        if (chargeWindupFill != null) chargeWindupFill.Hide();
        // Explicit reparent, not left for UpdateView's own edge-detected transition to eventually catch up —
        // an interrupt during Charging leaves chargeWindupIndicator unparented (see UpdateView's own
        // transition comment); Charging IS interruptible (see CanBeInterrupted below), so this is a real,
        // reachable case, not just defensive. Same fix/reasoning as BlitzGloomPD.OnInterrupted's own
        // identical check.
        if (chargeWindupIndicator != null && chargeWindupIndicator.transform.parent != _windupIndicatorParent)
            ReparentWindupIndicatorHome();
    }

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above) and
    // meant to be cancelable — CanBeInterrupted now defaults to false (see EnemyBasePD's own doc comment).
    // SpikeBurstWindup stays excluded regardless (per design — the actual spike burst/remote wave sequence
    // can't be cancelled once committed); Moving is isStunnable, so was never committed in the first place.
    protected override bool CanBeInterrupted
    {
        get
        {
            var ss = GetSpikeState(ref currentState);
            return ss == KingSpikeState.ChargeWindup || ss == KingSpikeState.Charging || ss == KingSpikeState.BetweenCharges
                || ss == KingSpikeState.PostChargeDash;
        }
    }

    // ── Remote bursts ────────────────────────────────────────────────────────

    // Scratch buffer for SpawnRemoteBurstWave below — cleared and reused every call, never left holding
    // references between calls, same zero-alloc convention as EnemyBasePD._randomTargetScratch.
    private static readonly List<CharacterStatsPD> _remoteBurstWaveScratch = new();

    // Players only (2026-08-30, explicit design exception to the rest of this boss's IFriendlyPD migration)
    // — Remote Spike Burst specifically stays CharacterStatsPD/CharacterStatsPD.All, never
    // FriendlyTargetRegistryPD, per explicit user direction: it's a "boss persistent attack" class mechanic
    // (spawns and hits at a target's position with no live tracking once launched — a friendly could easily
    // walk away before it detonates), unlike every other attack on this boss (FireSpikeBurst, charge, etc.),
    // which are free to hit friendlies.
    //
    // One random player per wave, not every player (2026-09-26) — was a foreach hitting everyone; explicit
    // design change, only for this attack-cycle version, NOT SpawnPersistentRemoteBurst below (that one
    // deliberately stays hitting everyone). No invisibility filter, same as before this change — this only
    // ever fires mid-attack-sequence, after a real target was already found the normal way (see this
    // method's own original doc comment, preserved above).
    private void SpawnRemoteBurstWave()
    {
        if (remoteBurstPrefab == null) return;

        _remoteBurstWaveScratch.Clear();
        foreach (var stats in CharacterStatsPD.All.Values)
            if (!stats.IsDead) _remoteBurstWaveScratch.Add(stats);

        if (_remoteBurstWaveScratch.Count == 0) return;

        var chosen = _remoteBurstWaveScratch[GameRandom.instance.Next(_remoteBurstWaveScratch.Count)];
        SpawnRemoteBurstFor(chosen);
    }

    // Extracted (2026-08-19) so UpdatePersistentRemoteBursts' own targeting loop can spawn the exact same
    // burst shape (offset/radius/damage/charge-time) as this boss's real attack sequence — see
    // persistentBurstInterval's own doc comment. Fixed-radius, random-angle offset — matches old KingSpike's
    // Random.insideUnitCircle.normalized * bombOffsetRadius (normalizing collapses the random point down to
    // a uniform random angle at a FIXED distance, not a random distance within it). Players only — see
    // SpawnRemoteBurstWave's own doc comment.
    private void SpawnRemoteBurstFor(CharacterStatsPD stats)
    {
        float   angle  = GameRandom.instance.NextFloat(0f, 360f) * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * remoteBurstOffsetRadius;
        Vector3 pos    = stats.transform.position + offset;

        var id = hierarchy.Create(remoteBurstPrefab, pos, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out RemoteSpikeBurstPD burst))
        {
            // EnrageDamageMultiplier multiplied in explicitly — same "reads B.scaledDamage directly"
            // reasoning as FireSpikeBurst's own dmg local above. EffectiveAttackSpeed passed as a one-time
            // snapshot (2026-09-14) so this hazard's own charge-then-detonate timer speeds up/slows down in
            // step with King Spike's own attack speed (including an active Slow) — see RemoteSpikeBurstPD's
            // own _chargeAttackSpeed doc comment for why it's snapshotted rather than read live.
            burst.Launch(remoteChargeTime, remoteBurstRadius, B.scaledDamage * remoteBurstDamageMultiplier * EnrageDamageMultiplier, EffectiveAttackSpeed);
            // Tracked so Die() can instantly Cancel() this — see activeRemoteBursts' own doc comment.
            currentState.activeRemoteBursts.Add(id.Value);
        }
    }

    // ── Persistent Remote Spike Bursts ──────────────────────────────────────────

    private void UpdatePersistentRemoteBursts(ref State state, float delta)
    {
        state.persistentBurstCooldownTimer -= delta;
        if (state.persistentBurstCooldownTimer > 0f) return;

        state.persistentBurstCooldownTimer = state.scaledPersistentBurstInterval;
        SpawnPersistentRemoteBurst();
    }

    // Players only — see SpawnRemoteBurstWave's own doc comment. Doubly applicable here: this is BOTH a
    // Remote Spike Burst AND a "boss persistent attack" (per explicit user direction, both categories are
    // players-only for targeting).
    private void SpawnPersistentRemoteBurst()
    {
        if (remoteBurstPrefab == null) return;

        foreach (var stats in CharacterStatsPD.All.Values)
        {
            // Same invisible-exclusion shape as KingOrbiterPD.BuildShotCandidates/EnemyBasePD.FindClosestTarget
            // — this persistent version fires blind on a timer rather than off a real target-acquisition pass,
            // so it needs its own explicit check (SpawnRemoteBurstWave's own real attack sequence doesn't).
            if (stats.IsDead || (stats.IsInvisible && !stats.IsTaunting)) continue;
            SpawnRemoteBurstFor(stats);
        }
    }

    // ── View ──────────────────────────────────────────────────────────────────

    private KingSpikeState _lastVisualState = (KingSpikeState)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        // Same reasoning as SpearGloomPD's guard — this can run while a pooled instance is transiently
        // inactive during replay; bail before recording _lastVisualState so the transition retries once
        // actually active, instead of leaving stale indicator state from a previous pooled life.
        if (!gameObject.activeInHierarchy) return;

        // Added 2026-10-15 — this class was missing the same guard Blitz/Leaper/Dagger already have: death
        // is handled by base.UpdateView() above (OnDeathStop() + hierarchy.Delete()), but without this,
        // execution continued into the rest of this method on the same frame a PredictedHierarchy
        // reconciliation rebuilds this instance after it died mid-Charging — the fresh _lastVisualState
        // default no longer matches the still-Charging stateId, so the transition block below would re-run
        // as if entering Charging for the first time, immediately re-enabling the very indicator
        // OnDeathStop() just hid. Bailing here preserves whatever OnDeathStop() already did. Same fix/
        // reasoning as LeaperGloomPD/BlitzGloomPD's own identical guard.
        if (viewState.b.isDead) return;

        // Re-applied every frame, not just once at spawn — if spikeBurstRadius is ever changed at runtime (a
        // live balance tweak, a future enrage/scaling hook, etc.), the indicator's visual footprint should
        // never silently drift out of sync with the actual value it represents. centerForwardOffset left at
        // its 0 default — spikeBurstIndicator's own current position (WindupFillPD's own transform) already
        // IS the center.
        if (spikeBurstIndicator != null && spikeBurstFill != null)
            spikeBurstFill.ShowCircle(spikeBurstRadius);

        var ss = (KingSpikeState)viewState.stateId;
        bool isChargeWindupOrCharging = ss == KingSpikeState.ChargeWindup || ss == KingSpikeState.Charging;

        // Charge windup indicator (2026-09-14 rework — this boss's own Charge is "basically the exact same
        // shape as Blitz's blitz" per explicit design, so this now mirrors BlitzGloomPD.UpdateView's own
        // identical block field-for-field; see that class's own doc comments for the full reasoning behind
        // each piece).
        //
        // ChargeWindup branch — full, unshrunk telegraph length, passively inherited under Visuals (not yet
        // unparented) — position/rotation are deliberately left untouched here, only the shape (ShowRectangle)
        // is driven. The explicit per-frame chargeWindupIndicator.transform.rotation write this branch used to
        // do is GONE (2026-09-14 fix) — it was a stale pre-Leaper-fix pattern: an explicit WORLD rotation
        // write on an object still parented under Visuals' own continuously-interpolating hierarchy is exactly
        // the class of read-timing mismatch that caused LeaperGloomPD's own original indicator jitter (see
        // that class's own doc comments) — re-asserting it every frame does NOT actually fix that, since
        // Unity composes local x parent transforms at RENDER time, not write time, so a later same-frame
        // Visuals update still produces a wrong final result. TrackChargeWindupTarget already turns the REAL
        // body via _rigidbody.MoveRotation every tick, so chargeWindupIndicator (a Visuals child) already
        // gets this exact same facing for free via plain passive inheritance — no explicit write needed at
        // all, same fix shape LeaperGloomPD's own windup indicator already went through.
        //
        // Charging branch — "collapses in behind the body" as it travels: the shown rectangle's near edge
        // advances from the fixed charge origin toward the fixed destination as viewState.chargeTravelProgress
        // climbs 0->1, while the far edge (the destination) never moves, so the length shown is always exactly
        // "how much of the charge is still ahead." chargeTravelProgress is written deterministically in
        // Simulate (see HandleActiveState's own doc comment) off the REAL tick-accurate transform.position —
        // this method itself never reads a live/smoothed transform for the shape math, only the
        // already-derived viewState result. Rotation here reads viewState.chargeDirection (not
        // _chargeDirectionSnapshot) — safe once unparented, since interpolating a value that's already fixed
        // for the whole Charging span against itself is a no-op (same reasoning BlitzGloomPD's own identical
        // read already establishes).
        if (chargeWindupIndicator != null && chargeWindupFill != null)
        {
            if (ss == KingSpikeState.Charging)
            {
                Vector3 destination = viewState.b.dashDestination;
                Vector3 origin = destination - viewState.chargeDirection * chargeDistance;
                float progress = Mathf.Clamp01(viewState.chargeTravelProgress);
                Vector3 nearEdge = Vector3.Lerp(origin, destination, progress);
                float remaining = chargeDistance * (1f - progress);
                chargeWindupIndicator.transform.SetPositionAndRotation(nearEdge, Quaternion.LookRotation(viewState.chargeDirection));
                chargeWindupFill.ShowRectangle(remaining, centerForwardOffset: remaining * 0.5f);
            }
            else
            {
                chargeWindupFill.ShowRectangle(chargeDistance, centerForwardOffset: chargeDistance * 0.5f);
            }
        }

        // Live, every frame regardless of the _lastVisualState-gated block below — reacts immediately to
        // EffectiveAttackSpeed changing mid-windup (a slow landing/expiring), same reasoning as every other
        // attack-speed-scaled enemy's own live fill. Gate widened to cover Charging too (2026-09-14, was
        // ChargeWindup-only) — per explicit design, the fill must read as cleanly, exactly fully filled
        // (relative to whatever the CURRENT, possibly-already-shrunk length is) for the entire Charging span.
        // Free for the same reason as Blitz: stateTimer counts down past 0 into negative territory through
        // Charging (nothing resets it at the ChargeWindup->Charging handoff), so Clamp01 of a negative ratio
        // floors to 0, and 1-0 = 1 — see BlitzGloomPD.UpdateView's own identical doc comment.
        if (chargeWindupFill != null && isChargeWindupOrCharging)
            chargeWindupFill.SetProgress(1f - Mathf.Clamp01(viewState.stateTimer / chargeWindupDuration));

        // Also live, every frame — see feedback_windupfill_self_timed_drift: SpikeBurstWindup's own timer is
        // never attack-speed-scaled (see HandleActiveState's own comment), but a self-timed StartFill() still
        // runs on its own Time.deltaTime clock, independent of the tick-based stateTimer that actually gates
        // FireSpikeBurst() — the two can still drift out of sync even at a fixed duration. Reusing
        // stateTimer/spikeBurstChargeTime here matches ChargeWindup's own live-SetProgress shape directly
        // above, just for the other windup.
        if (spikeBurstFill != null && ss == KingSpikeState.SpikeBurstWindup)
            spikeBurstFill.SetProgress(1f - Mathf.Clamp01(viewState.stateTimer / spikeBurstChargeTime));

        if (ss == _lastVisualState) return;
        KingSpikeState previousState = _lastVisualState;
        _lastVisualState = ss;

        // Per explicit design (2026-09-14) — the indicator must stay visible for the Charge's ENTIRE
        // movement, not just its ChargeWindup telegraph, matching the same "hold until the referenced enemy
        // has actually finished its own movement" convention established this session for King Orbiter/
        // Orbiter's own pull/shot indicators, and BlitzGloomPD's own dash.
        if (chargeWindupIndicator != null)
        {
            if (ss == KingSpikeState.Charging && previousState != KingSpikeState.Charging)
            {
                // Entering Charging — unparent once; the live-every-frame block above (which runs BEFORE this
                // gated block, same tick) already wrote this frame's correct world position/rotation/shape
                // using progress=0 (chargeTravelProgress was just reset to 0 in EnterCharging), so there's
                // nothing left to do here except detach from Visuals' continuously-interpolating hierarchy —
                // same reasoning BlitzGloomPD's own Blitzing-entry transition uses.
                chargeWindupIndicator.transform.SetParent(null, true);
            }
            else if (previousState == KingSpikeState.Charging && ss != KingSpikeState.Charging)
            {
                ReparentWindupIndicatorHome();
            }
            chargeWindupIndicator.SetActive(isChargeWindupOrCharging);
        }
        if (chargeWindupFill != null && !isChargeWindupOrCharging) chargeWindupFill.Hide();

        if (spikeBurstIndicator != null)
            spikeBurstIndicator.SetActive(ss == KingSpikeState.SpikeBurstWindup);
        if (spikeBurstFill != null && ss != KingSpikeState.SpikeBurstWindup) spikeBurstFill.Hide();
    }

    // Restores both windup indicators on a misprediction-revert — same class of bug as LeaperGloomPD/
    // BlitzGloomPD's own identical fix (see LeaperGloomPD.OnHiddenStateChanged's own doc comment for the
    // full mechanism). OnDeathStop() below is a one-way latch for both chargeWindupIndicator and
    // spikeBurstIndicator; nothing else re-shows either since stateId never actually left
    // Charging/ChargeWindup/SpikeBurstWindup (only the separate isDead flag mispredicted), so UpdateView's
    // _lastVisualState edge-detect never re-fires for either one. chargeWindupIndicator needs the same
    // reparent-if-Charging handling Blitz's own fix does; spikeBurstIndicator never gets unparented at all
    // (see UpdateView's own SpikeBurstWindup branch — no SetParent call exists for it), so a plain
    // SetActive(true) is enough there.
    protected override void OnHiddenStateChanged(bool hidden)
    {
        base.OnHiddenStateChanged(hidden);
        if (hidden) return;

        var ss = (KingSpikeState)currentState.stateId;

        if (chargeWindupIndicator != null && (ss == KingSpikeState.ChargeWindup || ss == KingSpikeState.Charging))
        {
            if (ss == KingSpikeState.Charging)
                chargeWindupIndicator.transform.SetParent(null, true);
            chargeWindupIndicator.SetActive(true);
        }

        if (spikeBurstIndicator != null && ss == KingSpikeState.SpikeBurstWindup)
            spikeBurstIndicator.SetActive(true);

        // Uncancels whichever snapshotted Remote Spike Bursts are still valid — see _pendingReclaimBursts'
        // own doc comment / Die()'s own doc comment for the full mechanism (same fix as OrbiterGloomPD/
        // KingOrbiterPD's own held-pleb reclaims, adapted for a standalone spawned effect instead of a held
        // enemy). A burst that's already been deleted (hierarchy.GetGameObject returns null — verified
        // caught up and confirmed the cancel before this reverted) is simply skipped, not re-spawned.
        for (int i = 0; i < _pendingReclaimBursts.Count; i++)
        {
            var id = _pendingReclaimBursts[i];
            var go = hierarchy.GetGameObject(id);
            if (go == null || !go.TryGetComponent(out RemoteSpikeBurstPD burst)) continue;
            burst.Uncancel();
            currentState.activeRemoteBursts.Add(id);
        }
        _pendingReclaimBursts.Clear();
    }

    // Snapshot of exactly which bursts Die() just cancelled — see its own doc comment for the full
    // mechanism. Plain field, not State — survives across the real-time window between Die() and the
    // verified-revert check in OnHiddenStateChanged, same as OrbiterGloomPD._pendingReclaimPlebs.
    private readonly List<PredictedObjectID> _pendingReclaimBursts = new();

    // Instantly cancels every currently-tracked Remote Spike Burst the moment death is predicted — see
    // RemoteSpikeBurstPD's own class doc comment for the design change this implements (previously these
    // lived fully independently of their firer). Snapshotted into _pendingReclaimBursts BEFORE cancelling,
    // same "real cross-identity predicted-state mutation needs an explicit revert path" reasoning as
    // OrbiterGloomPD.Die()'s own identical pattern — Cancel() has no auto-revert of its own, so without this
    // a misprediction-then-correction cycle would either leave a still-very-much-alive boss's burst
    // permanently cancelled, or (same as the held-pleb case) flicker cancelled/active as Die() re-fires.
    protected override void Die()
    {
        base.Die();
        _pendingReclaimBursts.Clear();
        for (int i = 0; i < currentState.activeRemoteBursts.Count; i++)
        {
            var id = currentState.activeRemoteBursts[i];
            _pendingReclaimBursts.Add(id);
            var go = hierarchy.GetGameObject(id);
            if (go != null && go.TryGetComponent(out RemoteSpikeBurstPD burst))
                burst.Cancel();
        }
        currentState.activeRemoteBursts.Clear();
    }

    // Same burst-cancel loop as Die() above, for the difficulty-pacing wipe path (2026-10-16 fix) — see
    // EnemyBasePD.OnSilentDespawn's own doc comment. No _pendingReclaimBursts snapshot here — that exists
    // purely to support a misprediction-revert, which a wipe (deterministic difficulty pacing, not a
    // speculative prediction) never undergoes — nothing to reclaim, only something to cancel.
    protected override void OnSilentDespawn()
    {
        base.OnSilentDespawn();
        for (int i = 0; i < currentState.activeRemoteBursts.Count; i++)
        {
            var go = hierarchy.GetGameObject(currentState.activeRemoteBursts[i]);
            if (go != null && go.TryGetComponent(out RemoteSpikeBurstPD burst))
                burst.Cancel();
        }
        currentState.activeRemoteBursts.Clear();
    }

    protected override void OnDeathStop()
    {
        if (chargeWindupIndicator != null) chargeWindupIndicator.SetActive(false);
        if (chargeWindupFill != null)      chargeWindupFill.Hide();
        // Explicit reparent, fixed immediately rather than left for OnRespawnReset's own defensive check to
        // catch on the NEXT pooled reuse — same "leftover indicator stuck mid-air" class of bug OnInterrupted's
        // own identical fix addresses. Dying mid-Charging leaves chargeWindupIndicator unparented (see
        // UpdateView's own transition comment).
        if (chargeWindupIndicator != null && chargeWindupIndicator.transform.parent != _windupIndicatorParent)
            ReparentWindupIndicatorHome();
        if (spikeBurstIndicator != null)   spikeBurstIndicator.SetActive(false);
        if (spikeBurstFill != null)        spikeBurstFill.Hide();
    }

    protected override void OnRespawnReset()
    {
        // Plain field, not State — see its own doc comment. Cleared here so a reused instance never tries
        // to Uncancel() a stale burst ObjectId left over from a previous life's own correctly-confirmed
        // (never-reverted) death.
        _pendingReclaimBursts.Clear();

        currentState.stateId              = 0;
        currentState.stateTimer           = 0f;
        currentState.attackCooldownTimer  = currentState.scaledAttackCooldown; // full grace, matching LateAwake()'s own fix
        currentState.chargesRemaining     = 0;
        currentState.spikeBurstPending    = false;
        currentState.postChargeDashPending = false;
        currentState.remoteSequenceActive = false;
        currentState.remoteBurstsSpawned  = 0;
        currentState.remoteSequenceTimer  = 0f;
        // Full grace, matching LateAwake()'s own reasoning for persistentBurstCooldownTimer.
        currentState.persistentBurstCooldownTimer = currentState.scaledPersistentBurstInterval;
        if (chargeWindupIndicator != null) chargeWindupIndicator.SetActive(false);
        if (chargeWindupFill != null)      chargeWindupFill.Hide();
        // In case death happened mid-Charging, while the indicator was unparented — a fresh reuse should
        // never inherit a previous life's detached indicator (defensive backstop — OnDeathStop's own
        // identical check should already have caught this, but this covers any path that skipped it).
        if (chargeWindupIndicator != null && chargeWindupIndicator.transform.parent != _windupIndicatorParent)
            ReparentWindupIndicatorHome();
        if (spikeBurstIndicator != null)   spikeBurstIndicator.SetActive(false);
        if (spikeBurstFill != null)        spikeBurstFill.Hide();
        // In case death happened mid-SpikeBurstWindup, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (stateId resets to Moving above), but resetting explicitly
        // here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (KingSpikeState)(-1);
        _chargeWindupTrackedTarget = null;
    }
}
