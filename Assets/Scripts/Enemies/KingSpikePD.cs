using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// The one and only boss. Ported from _OldSystemArchive/Enemies/KingSpike.cs + BossBase.cs, with the old
// coroutine-based bomb sequence (StartCoroutine/WaitForSeconds — not viable in PD, not deterministic or
// resimulation-safe) redesigned as tick-based timers instead. Charging reuses EnemyBasePD's generic dash
// primitives (StartDashToPoint/HandleDash) directly — no custom movement code needed for it. Enrage uses
// EnemyBasePD's generic enrageTimer/OnEnrage() (armed via the enrageTimer Inspector field).
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

    public override float GetDamage() =>
        (KingSpikeState)currentState.stateId == KingSpikeState.Charging ? damage * chargeDamageMultiplier : damage;

    // Fires exactly once, via EnemyBasePD's enrageTimer/enrageCountdown machinery (armed by setting
    // enrageTimer in the Inspector — 120s per the old system's default). One-time permanent reassignment
    // (halves whatever attackCooldown currently is, not a repeating multiplier) — safe to read attackCooldown
    // here since OnEnrage only ever fires once, before anything else has had a chance to touch it.
    // The "cooldown starts at Spike Burst instead of after the remote sequence" half of enrage lives in
    // FireSpikeBurst()/Simulate(), gated on state.b.enraged.
    protected override void OnEnrage()
    {
        attackCooldown *= 0.5f;
    }

    [Header("Damage")]
    [SerializeField] private float chargeDamageMultiplier      = 1f;
    [SerializeField] private float spikeBurstDamageMultiplier  = 1.5f;
    [SerializeField] private float remoteBurstDamageMultiplier = 0.75f;

    [Header("Charge")]
    [SerializeField] private float chargeWindupDuration = 0.7f;
    [SerializeField] private float chargeSpeedMultiplier = 3f;
    private float ChargeSpeed => baseMoveSpeed * chargeSpeedMultiplier;
    [SerializeField] private float chargeDistance       = 15f;
    [SerializeField] private float attackCooldown       = 8f;
    [SerializeField] private int   chargesPerAttack     = 3;
    [SerializeField] private float betweenChargePause   = 0.5f;
    [SerializeField] private GameObject   chargeWindupIndicator;
    [SerializeField] private WindupFillPD chargeWindupFill;

    [Header("Spike Burst")]
    [SerializeField] private float spikeBurstChargeTime = 1.5f;
    [SerializeField] private float spikeBurstRadius     = 8f;
    [SerializeField] private GameObject   spikeBurstIndicator;
    [SerializeField] private WindupFillPD spikeBurstFill;

    [Header("Remote Spike Bursts")]
    [SerializeField] private GameObject remoteBurstPrefab;
    [SerializeField] private float      remoteBurstOffsetRadius = 1f;
    [SerializeField] private float      remoteBurstRadius       = 3f;
    [SerializeField] private int        remoteBurstCount        = 4;
    [SerializeField] private float      remoteBurstInterval     = 1f;
    [SerializeField] private float      remoteChargeTime        = 1f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void LateAwake()
    {
        base.LateAwake();
        // Half cooldown grace period on spawn, matching old KingSpike — plain currentState assignment, so
        // even if LateAwake somehow re-fires on a pooled reuse this is harmless (idempotent), unlike an
        // accumulating event subscription would be.
        currentState.attackCooldownTimer = attackCooldown * 0.5f;
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void Simulate(ref State state, float delta)
    {
        base.Simulate(ref state, delta);

        // Remote burst sequencing runs independent of HandleActiveState's FSM switch below — the boss is
        // already back in Moving by the time this needs to keep ticking (see FireSpikeBurst()).
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (B.isDead || !state.remoteSequenceActive) return;

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
            // Post-enrage, attackCooldownTimer was already armed immediately in FireSpikeBurst() instead
            // of here — don't stomp it back to a fresh full duration now that it's had a head start.
            if (!state.b.enraged) state.attackCooldownTimer = attackCooldown;
            state.remoteSequenceActive = false;
        }
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        if (dashJustEnded)
        {
            state.chargesRemaining--;
            if (state.chargesRemaining > 0) EnterBetweenCharges(ref state);
            else                             EnterSpikeBurstWindup(ref state);
        }

        state.attackCooldownTimer -= delta;
        state.stateTimer          -= delta;

        var ss = GetSpikeState(ref state);

        switch (ss)
        {
            case KingSpikeState.Moving:
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
                    state.chargesRemaining = chargesPerAttack;
                    EnterChargeWindup(ref state);
                }
                break;

            case KingSpikeState.ChargeWindup:
                if (state.stateTimer <= 0f) EnterCharging(ref state);
                break;

            case KingSpikeState.BetweenCharges:
                if (state.stateTimer <= 0f) EnterChargeWindup(ref state);
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
        var snapDir = SnapFaceTarget(ref state);
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
        float   dmg    = damage * spikeBurstDamageMultiplier;
        foreach (var stats in CharacterStatsPD.All.Values)
        {
            if (stats.IsDead || stats.IsInvincible) continue;
            if (Vector3.Distance(stats.transform.position, origin) <= spikeBurstRadius)
                stats.TakeDamage(dmg);
        }

        // Boss returns to Moving immediately (point 5) — the remote sequence keeps ticking independently
        // via Simulate() above. attackCooldownTimer is deliberately NOT touched here (point 6) — except
        // once enraged, where it arms immediately instead, racing the remote burst sequence rather than
        // waiting for it (see Simulate()'s matching skip in the completed-sequence branch).
        state.remoteSequenceActive = true;
        state.remoteBurstsSpawned  = 0;
        state.remoteSequenceTimer  = remoteBurstInterval;
        if (state.b.enraged) state.attackCooldownTimer = attackCooldown;
        SetSpikeState(ref state, KingSpikeState.Moving);
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
                burst.Launch(remoteChargeTime, remoteBurstRadius, damage * remoteBurstDamageMultiplier);
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

        if (ss == _lastVisualState) return;
        _lastVisualState = ss;

        if (chargeWindupIndicator != null)
            chargeWindupIndicator.SetActive(ss == KingSpikeState.ChargeWindup);
        if (chargeWindupFill != null)
        {
            if (ss == KingSpikeState.ChargeWindup) chargeWindupFill.StartFill(chargeWindupDuration);
            else                                    chargeWindupFill.ResetFill();
        }

        if (spikeBurstIndicator != null)
            spikeBurstIndicator.SetActive(ss == KingSpikeState.SpikeBurstWindup);
        if (spikeBurstFill != null)
        {
            if (ss == KingSpikeState.SpikeBurstWindup) spikeBurstFill.StartFill(spikeBurstChargeTime);
            else                                        spikeBurstFill.ResetFill();
        }
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
        currentState.attackCooldownTimer  = attackCooldown * 0.5f;
        currentState.chargesRemaining     = 0;
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
