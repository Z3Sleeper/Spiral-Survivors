using PurrNet.Packing;
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
        // Set by OnInterrupted() when Charge gets cancelled — Moving's own case checks this before its
        // normal cooldown-trigger logic (see HandleActiveState) and, once no longer crowd-controlled, fires
        // straight into Spike Burst instead of a fresh Charge attempt. See OnInterrupted()'s own doc comment
        // for why this holding step exists instead of transitioning straight into Spike Burst.
        public bool    spikeBurstPending;
        public bool    remoteSequenceActive;
        public int     remoteBurstsSpawned;
        // Dual-purpose: counts down to the next remote burst wave while remoteBurstsSpawned < remoteBurstCount;
        // once the last wave is spawned, counts down that wave's own charge time instead, and expiring THEN
        // arms attackCooldownTimer. This is how the cooldown ends up starting only once the last remote
        // burst has finished charging, without KingSpikePD needing to observe the spawned hazards directly.
        public float   remoteSequenceTimer;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    public enum KingSpikeState { Moving, ChargeWindup, Charging, BetweenCharges, SpikeBurstWindup }

    private KingSpikeState GetSpikeState(ref State s) => (KingSpikeState)s.stateId;
    private void SetSpikeState(ref State s, KingSpikeState st) => s.stateId = (int)st;

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    // Overrides GetOwnDamage(), not GetDamage() itself — GetDamage() is the fixed base-class wrapper that
    // checks held-state first (see its own doc comment); overriding this hook instead means this charge
    // multiplier automatically composes correctly if this boss were ever grabbable in the future, without
    // needing its own held-check re-added here. EnrageDamageMultiplier multiplied in explicitly (2026-08-19)
    // — this override REPLACES the base GetOwnDamage() entirely rather than calling base(), so it doesn't
    // inherit that composition for free the way a non-overriding enemy does.
    protected override float GetOwnDamage() =>
        (KingSpikeState)currentState.stateId == KingSpikeState.Charging ? damage * chargeDamageMultiplier * EnrageDamageMultiplier : damage * EnrageDamageMultiplier;

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
    // Tier1-authored baseline, scaled per tier by chargesPerAttackTierGrowth below (applied in
    // OnConfigureSpawn()).
    [SerializeField] private int   chargesPerAttack     = 4;
    [SerializeField] private float betweenChargePause   = 0.1f;
    [SerializeField] private GameObject   chargeWindupIndicator;
    [SerializeField] private WindupFillPD chargeWindupFill;

    [Header("Spike Burst")]
    [SerializeField] private float spikeBurstChargeTime = 1.5f;
    // Tier1-authored baseline, scaled per tier by spikeBurstRadiusTierGrowth below (applied in
    // OnConfigureSpawn()) — GROWS per tier ("20% larger").
    [SerializeField] private float spikeBurstRadius     = 18f;
    [SerializeField] private GameObject   spikeBurstIndicator;
    [SerializeField] private WindupFillPD spikeBurstFill;

    [Header("Remote Spike Bursts")]
    [SerializeField] private GameObject remoteBurstPrefab;
    [SerializeField] private float      remoteBurstOffsetRadius = 3f;
    [SerializeField] private float      remoteBurstRadius       = 6f;
    // Tier1-authored baseline, scaled per tier by remoteBurstCountTierGrowth below (applied in
    // OnConfigureSpawn()).
    [SerializeField] private int        remoteBurstCount        = 8;
    // Tier1-authored baseline, scaled per tier by remoteBurstIntervalTierGrowth below (applied in
    // OnConfigureSpawn()) — SHRINKS per tier ("20% shorter").
    [SerializeField] private float      remoteBurstInterval     = 1f;
    [SerializeField] private float      remoteChargeTime        = 1.5f;

    [Header("Attack Cooldown")]
    // Only actually starts counting down once the whole remote burst sequence finishes (all waves spawned
    // AND the final wave's own remoteChargeTime elapsed) — see Simulate()'s own remote-sequence tail.
    [SerializeField] private float attackCooldown = 10f;
    // Captured once in OnAwake(), reset back onto attackCooldown every pooled reuse in LateAwake(), scaled
    // by attackCooldownMultiplier (Threat Level 5) in OnConfigureSpawn() — same "_authoredX snapshot"
    // pattern as the 4 tier-scaled fields below.
    private float _authoredAttackCooldown;

    [Header("Tier Scaling")]
    // Duration fields SHRINK per tier (base * (1 - (tier-1)*growth)) — "X% shorter/faster" means less time,
    // not more. Size/count fields GROW per tier — see each field's own default/comment. Linear per tier off
    // the tier1-authored base in all cases, never compounding/exponential (user's explicit "multiplicative
    // fields multiply base, no exponential tier scaling" rule).
    [SerializeField] private int   chargesPerAttackTierGrowth    = 2;
    [SerializeField] private float spikeBurstRadiusTierGrowth    = 0.2f;
    [SerializeField] private int   remoteBurstCountTierGrowth    = 4;
    [SerializeField] private float remoteBurstIntervalTierGrowth = 0.2f;

    // Captured once in OnAwake(), reset back onto these 4 fields every pooled reuse in LateAwake() — same
    // "_authoredX snapshot" pattern established for every other tier-scaled field this pass.
    private int   _authoredChargesPerAttack;
    private float _authoredSpikeBurstRadius;
    private int   _authoredRemoteBurstCount;
    private float _authoredRemoteBurstInterval;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnAwake()
    {
        base.OnAwake();
        _authoredChargesPerAttack    = chargesPerAttack;
        _authoredSpikeBurstRadius    = spikeBurstRadius;
        _authoredRemoteBurstCount    = remoteBurstCount;
        _authoredRemoteBurstInterval = remoteBurstInterval;
        _authoredAttackCooldown      = attackCooldown;
    }

    protected override void LateAwake()
    {
        base.LateAwake();
        // Reset to the tier1-authored baseline before OnConfigureSpawn (which always runs AFTER LateAwake(),
        // once hierarchy.Create() returns) scales these fresh for this life — see _authoredChargesPerAttack's
        // own doc comment.
        chargesPerAttack    = _authoredChargesPerAttack;
        spikeBurstRadius    = _authoredSpikeBurstRadius;
        remoteBurstCount    = _authoredRemoteBurstCount;
        remoteBurstInterval = _authoredRemoteBurstInterval;
        attackCooldown      = _authoredAttackCooldown;
        // Full cooldown grace period on spawn (2026-08-12: changed from half — this boss's opening attack
        // would otherwise fire before players have even finished loading in) — plain currentState
        // assignment, so even if LateAwake somehow re-fires on a pooled reuse this is harmless (idempotent),
        // unlike an accumulating event subscription would be.
        currentState.attackCooldownTimer = attackCooldown;
    }

    // Tier1-authored chargesPerAttack/spikeBurstRadius/remoteBurstCount/remoteBurstInterval, scaled by each
    // field's own *TierGrowth (see the "Tier Scaling" header's own doc comment for the duration-shrinks/
    // size-grows formula shapes). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call). All 4 are read live during gameplay (chargesPerAttack/remoteBurstCount via
    // refill-if-exhausted checks, spikeBurstRadius/remoteBurstInterval read directly), not mirrored into
    // predicted State at spawn time, so no extra currentState reset is needed anywhere else.
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        chargesPerAttack    += (spawnTier - 1) * chargesPerAttackTierGrowth;
        spikeBurstRadius    *= 1f + (spawnTier - 1) * spikeBurstRadiusTierGrowth;
        remoteBurstCount    += (spawnTier - 1) * remoteBurstCountTierGrowth;
        remoteBurstInterval *= 1f - (spawnTier - 1) * remoteBurstIntervalTierGrowth;

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        attackCooldown = _authoredAttackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = attackCooldown;
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
        if (B.isDead || B.wiped || !state.remoteSequenceActive) return;

        state.remoteSequenceTimer -= delta;
        if (state.remoteSequenceTimer > 0f) return;

        if (state.remoteBurstsSpawned < remoteBurstCount)
        {
            SpawnRemoteBurstWave();
            state.remoteBurstsSpawned++;
            state.remoteSequenceTimer = state.remoteBurstsSpawned < remoteBurstCount
                ? remoteBurstInterval
                : remoteChargeTime;
        }
        else
        {
            state.attackCooldownTimer = attackCooldown;
            state.remoteSequenceActive = false;
        }
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded)
        {
            // Decrement moved to EnterChargeWindup (attack START, not this completion point) — see its own
            // doc comment for why. chargesRemaining already reflects the charge that just ended by the time
            // we get here.
            // IsTargetStillValid (also covers a null target, same as everyone else's explicit != null check)
            // — a target that went invisible-and-non-taunting since the last charge shouldn't get chained
            // into another one against them; see that property's own doc comment.
            // !IsCrowdControlled — a true Stun() landing mid-sequence never interrupts the charge that just
            // ended above, but stops the chain from continuing into another one, falling through to the
            // spike burst phase instead (a Slow does NOT stop the chain — see IsCrowdControlled's own doc
            // comment, hard CC only).
            if (state.chargesRemaining > 0 && IsTargetStillValid && !IsCrowdControlled) EnterBetweenCharges(ref state);
            else                                                  EnterSpikeBurstWindup(ref state);
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
                // An interrupted Charge routes here with spikeBurstPending set (see OnInterrupted()'s own
                // doc comment) instead of straight into Spike Burst — Spike Burst's own windup is
                // deliberately stun-immune once entered (fixed real-time clock, ignores EffectiveAttackSpeed/
                // isStunnable — see this switch's SpikeBurstWindup case), so jumping there immediately would
                // let a Stun() paired with the same hit as the interrupt sail straight into an unstoppable
                // burst. Moving IS stunnable, so parking here first means a paired true stun freezes this
                // whole method before it ever reaches this branch (via Simulate()'s own isStunnable-gated
                // freeze) — this branch only actually runs once not truly stunned (IsCrowdControlled is hard
                // CC only, i.e. IsTrueStunned — a Slow does NOT hold this open, see its own doc comment), at
                // which point it fires straight into Spike Burst instead of the normal cooldown/charge-
                // trigger logic below.
                if (state.spikeBurstPending)
                {
                    if (IsCrowdControlled)
                    {
                        moveSpeed = baseMoveSpeed;
                        if (_target != null) HandleMovement(ref state, delta);
                    }
                    else
                    {
                        state.spikeBurstPending = false;
                        EnterSpikeBurstWindup(ref state);
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
                moveSpeed = cooldownReady ? baseMoveSpeed * 5f : baseMoveSpeed;
                if (_target != null) HandleMovement(ref state, delta);
                if (cooldownReady && _target != null &&
                    Vector3.Distance(transform.position, _target.position) <= chargeDistance)
                {
                    // chargesRemaining itself is handled by EnterChargeWindup now, not here — see its own
                    // doc comment for why.
                    // Ongoing attack starts here — see CaptureAttackAnchor's own doc comment.
                    CaptureAttackAnchor(ref state);
                    EnterChargeWindup(ref state);
                }
                break;

            case KingSpikeState.ChargeWindup:
                if (state.stateTimer <= 0f) EnterCharging(ref state);
                break;

            case KingSpikeState.BetweenCharges:
                // Outside the ongoing window (ongoing ends the moment a single Charging finishes) — safe to
                // read live _target here, deciding whether/how the NEXT charge in the chain proceeds.
                if (state.stateTimer <= 0f)
                {
                    if (_target == null)
                    {
                        // No valid target for another charge — skip straight to Spike Burst instead of
                        // winding up at nobody. Unlike every other multi-stage enemy, this does NOT abort
                        // to Moving: SpikeBurstWindup/FireSpikeBurst/RemoteSpikeBurstPD never need a target
                        // (always hit every player directly via CharacterStatsPD.All), so there's no reason
                        // to cut the sequence short.
                        EnterSpikeBurstWindup(ref state);
                    }
                    else
                    {
                        CaptureAttackAnchor(ref state);
                        EnterChargeWindup(ref state);
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
        // via AddForce, which a kinematic body ignores), and Charging needs to stay dynamic AND
        // collider-disabled (fast movement — Case 1, non-interacting, handled entirely by the dash
        // primitives already). isStunnable is already false for the entire attack routine above, a
        // superset of every kinematic window, so AddKnockbackVelocity's own `if (!isStunnable ...) return;`
        // guard already ensures knockback is never attempted against this rigidbody while it's kinematic.
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = ss != KingSpikeState.Moving && ss != KingSpikeState.Charging;
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

    // Plain (not predicted State) shadow of state.chargeDirection, written in lockstep inside
    // EnterChargeWindup() — the same deterministic Simulate() call that decides chargeDirection itself, so
    // it's available instantly on every machine with no network round-trip. Same reasoning as
    // LeaperGloomPD._leapTargetSnapshot: viewState wouldn't work either, since PurrDiction linearly
    // interpolates every State field (including plain Vector3s) between ticks, and this is a discrete
    // "decided once" value that shouldn't blend.
    private Vector3 _chargeDirectionSnapshot;

    private void EnterChargeWindup(ref State state)
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
        moveSpeed = baseMoveSpeed;

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
        if (state.chargesRemaining <= 0) state.chargesRemaining = chargesPerAttack;
        state.chargesRemaining--;

        var snapDir = SnapFaceAnchor(ref state);
        if (snapDir != Vector3.zero) state.chargeDirection = snapDir;
        _chargeDirectionSnapshot = state.chargeDirection;
        state.stateTimer = chargeWindupDuration;
        SetSpikeState(ref state, KingSpikeState.ChargeWindup);
    }

    private void EnterCharging(ref State state)
    {
        Vector3 endPoint = transform.position + state.chargeDirection * chargeDistance;
        SetSpikeState(ref state, KingSpikeState.Charging);
        StartDashToPoint(ref state, ChargeSpeed, endPoint);
    }

    private void EnterBetweenCharges(ref State state)
    {
        state.stateTimer = betweenChargePause;
        SetSpikeState(ref state, KingSpikeState.BetweenCharges);
    }

    private void EnterSpikeBurstWindup(ref State state)
    {
        state.stateTimer = spikeBurstChargeTime;
        SetSpikeState(ref state, KingSpikeState.SpikeBurstWindup);
    }

    private void FireSpikeBurst(ref State state)
    {
        Vector3 origin = transform.position;
        // EnrageDamageMultiplier multiplied in explicitly (2026-08-19) — this reads the raw `damage` field
        // directly rather than going through GetOwnDamage()/GetDamage(), so it doesn't pick the bonus up
        // for free.
        float   dmg    = damage * spikeBurstDamageMultiplier * EnrageDamageMultiplier;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead || stats.IsInvincible) continue;
            if (Vector3.Distance(stats.transform.position, origin) <= spikeBurstRadius)
                stats.TakeDamage(dmg);
        }

        // Boss returns to Moving immediately (point 5) — the remote sequence keeps ticking independently
        // via Simulate() above. attackCooldownTimer is deliberately NOT touched here (point 6) — the whole
        // sequence has to actually finish before the boss can attack again, which post-enrage never
        // happens (see Simulate()'s own comment).
        state.remoteSequenceActive = true;
        state.remoteBurstsSpawned  = 0;
        state.remoteSequenceTimer  = remoteBurstInterval;
        // Reprioritize now that the attack sequence is over — random, not closest, per this boss's own
        // design (see EnemyBasePD.FindRandomTarget's own doc comment for why).
        FindRandomTarget();
        SetSpikeState(ref state, KingSpikeState.Moving);
    }

    // Reworked (2026-08-12, reverses the old "chargesRemaining deliberately left untouched" design) — an
    // interrupted Charge no longer just falls back to Moving and lets a fresh charge attempt restart
    // (chargesRemaining used to be preserved specifically to prevent handing out extra charges on a retry,
    // but the retry itself is exactly what's being removed now). Zeroes chargesRemaining outright and routes
    // toward Spike Burst instead — the same destination this boss's own natural "ran out of chargesRemaining"
    // path already reaches — via spikeBurstPending rather than calling EnterSpikeBurstWindup() directly here;
    // see Moving's own case in HandleActiveState for why that indirection exists (letting a paired stun
    // freeze this boss first, since Spike Burst itself can't be stunned once entered). No separate cooldown
    // write needed — Spike Burst's own remote-burst sequence already ends the whole attack cycle on a flat,
    // full attackCooldown once it resolves (see Simulate()'s own tail). remoteSequenceActive/
    // remoteBurstsSpawned untouched — those only ever get armed from FireSpikeBurst, which CanBeInterrupted
    // below already keeps this from ever running during.
    protected override void OnInterrupted()
    {
        currentState.chargesRemaining   = 0;
        currentState.spikeBurstPending  = true;
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
        if (chargeWindupFill != null) chargeWindupFill.ResetFill();
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
            return ss == KingSpikeState.ChargeWindup || ss == KingSpikeState.Charging || ss == KingSpikeState.BetweenCharges;
        }
    }

    // ── Remote bursts ────────────────────────────────────────────────────────

    private void SpawnRemoteBurstWave()
    {
        if (remoteBurstPrefab == null) return;

        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead) continue;

            // Fixed-radius, random-angle offset — matches old KingSpike's
            // Random.insideUnitCircle.normalized * bombOffsetRadius (normalizing collapses the random
            // point down to a uniform random angle at a FIXED distance, not a random distance within it).
            float   angle  = GameRandom.instance.NextFloat(0f, 360f) * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * remoteBurstOffsetRadius;
            Vector3 pos    = stats.transform.position + offset;

            var id = hierarchy.Create(remoteBurstPrefab, pos, Quaternion.identity);
            if (!id.HasValue) continue;

            var go = hierarchy.GetGameObject(id.Value);
            if (go != null && go.TryGetComponent(out RemoteSpikeBurstPD burst))
                // EnrageDamageMultiplier multiplied in explicitly — same "reads raw damage directly" reasoning
                // as FireSpikeBurst's own dmg local above.
                burst.Launch(remoteChargeTime, remoteBurstRadius, damage * remoteBurstDamageMultiplier * EnrageDamageMultiplier);
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

        // Re-applied every frame, not just once at spawn — if spikeBurstRadius/chargeDistance are ever
        // changed at runtime (a live balance tweak, a future enrage/scaling hook, etc.), the indicators'
        // visual footprints should never silently drift out of sync with the actual values they represent.
        // Charge applied once to the parent (ChargeDangerIndicator/ChargeWindupFill are both its children,
        // axis-aligned with it) rather than each child individually.
        if (spikeBurstIndicator != null) IndicatorMath.ApplyRadialTransform(spikeBurstIndicator.transform, spikeBurstRadius);
        if (chargeWindupIndicator != null) IndicatorMath.ApplyLineTransform(chargeWindupIndicator.transform, chargeDistance);

        var ss = (KingSpikeState)viewState.stateId;

        // Sourced from _chargeDirectionSnapshot (a plain field, not predicted State) — instantly available
        // on every machine the moment EnterChargeWindup() decides it, with no network round-trip and no
        // interpolation blend (see the field's own doc comment; this used to read off `verified`, which
        // only populates for non-owner clients after a round-trip — the same class of visible-desync bug
        // fixed for LeaperGloomPD's leap indicator).
        //
        // Tracked every frame, NOT gated behind the one-shot _lastVisualState transition guard below —
        // chargeWindupIndicator is a child of "Visuals", the transform PredictedTransform's _graphics
        // field points at and continuously interpolates every render frame (toward the physics root's
        // tick-accurate rotation), independent of anything else touching it. Setting an absolute world
        // rotation on one of its children only once would get visually dragged along as Visuals keeps
        // interpolating afterward. Re-asserting every frame keeps the indicator pinned regardless of what
        // Visuals is doing underneath it.
        //
        // EnterCharging()'s actual dash direction reads the authoritative ref State directly — no
        // interpolation involved at all — so it's always exactly correct; only the indicator needed this.
        if (chargeWindupIndicator != null && ss == KingSpikeState.ChargeWindup)
        {
            chargeWindupIndicator.transform.rotation = Quaternion.LookRotation(_chargeDirectionSnapshot);
        }

        // Live, every frame regardless of the _lastVisualState-gated block below — reacts immediately to
        // EffectiveAttackSpeed changing mid-windup (a slow landing/expiring), same reasoning as every other
        // attack-speed-scaled enemy's own live fill.
        if (chargeWindupFill != null && ss == KingSpikeState.ChargeWindup)
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
        _lastVisualState = ss;

        if (chargeWindupIndicator != null)
            chargeWindupIndicator.SetActive(ss == KingSpikeState.ChargeWindup);
        if (chargeWindupFill != null && ss != KingSpikeState.ChargeWindup) chargeWindupFill.ResetFill();

        if (spikeBurstIndicator != null)
            spikeBurstIndicator.SetActive(ss == KingSpikeState.SpikeBurstWindup);
        if (spikeBurstFill != null && ss != KingSpikeState.SpikeBurstWindup) spikeBurstFill.ResetFill();
    }

    protected override void OnDeathStop()
    {
        if (chargeWindupIndicator != null) chargeWindupIndicator.SetActive(false);
        if (chargeWindupFill != null)      chargeWindupFill.ResetFill();
        if (spikeBurstIndicator != null)   spikeBurstIndicator.SetActive(false);
        if (spikeBurstFill != null)        spikeBurstFill.ResetFill();
    }

    protected override void OnRespawnReset()
    {
        currentState.stateId              = 0;
        currentState.stateTimer           = 0f;
        currentState.attackCooldownTimer  = attackCooldown; // full grace, matching LateAwake()'s own fix
        currentState.chargesRemaining     = 0;
        currentState.spikeBurstPending    = false;
        currentState.remoteSequenceActive = false;
        currentState.remoteBurstsSpawned  = 0;
        currentState.remoteSequenceTimer  = 0f;
        if (chargeWindupIndicator != null) chargeWindupIndicator.SetActive(false);
        if (chargeWindupFill != null)      chargeWindupFill.ResetFill();
        if (spikeBurstIndicator != null)   spikeBurstIndicator.SetActive(false);
        if (spikeBurstFill != null)        spikeBurstFill.ResetFill();
        // In case death happened mid-SpikeBurstWindup, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (stateId resets to Moving above), but resetting explicitly
        // here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState = (KingSpikeState)(-1);
    }
}
