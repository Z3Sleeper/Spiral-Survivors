using System.Collections.Generic;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Boss. Extends OrbiterGloomPD's grab-and-orbit mechanic into a 5-phase cycle: Moving -> Pull -> Orbit ->
// Knockback -> Shooting -> repeat. Pull grabs 3 separate rings of Pleb-tier enemies (inner/middle/outer,
// sequential sub-pulls — grab+pull inner to completion, then middle, then outer, each shaped exactly like
// OrbiterGloomPD's own single Windup->Pulling sequence), each ring orbiting at its own radius/speed. Orbit
// chases players with the held rings spinning (the only stunnable+interruptible phase). Knockback freezes
// every held ring in place (SetOrbitAngularSpeed(0), doesn't drop them) and radially knocks back nearby
// players — same shape as KingSpikePD's own SpikeBurst, just knockback instead of damage. Shooting then
// launches each ring's plebs (outer first, then middle, then inner) at one shared random target per ring
// (individually aimed from each pleb's own position, converging on that target) via the new generic
// IEnemyPD.LaunchAlong primitive — released once they travel the full shootingRange regardless of what they
// hit along the way (damage during flight comes from each pleb's own ordinary, untouched contact-damage
// collider, not any hit-detection code here).
//
// No size/elite scaling in this script beyond whatever EnemyBasePD already provides by default — hand-tuned
// boss, prefab is a scaled-up copy of the Orbiter prefab (per explicit design).
public class KingOrbiterPD : EnemyBasePD<KingOrbiterPD.State>
{
    public enum KingOrbiterPhase { Moving, PullWindup, Pulling, Orbit, KnockbackWindup, ShootWindup, ShootFlight }

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   phaseId;
        public float phaseTimer;
        public float attackCooldownTimer;
        // 0=inner,1=middle,2=outer — drives PullWindup/Pulling.
        public int pullRingIndex;
        // 0=outer,1=middle,2=inner (opposite order from pull) — drives ShootWindup/ShootFlight, see
        // ShootPhysicalRing.
        public int shootRingIndex;
        public DisposableList<PredictedObjectID> innerHeld;
        public DisposableList<PredictedObjectID> middleHeld;
        public DisposableList<PredictedObjectID> outerHeld;
        // Last ring's chosen shot target — excluded from the next ring's random pick when possible ("unique
        // from the last target if possible"). Default(PlayerID) on a fresh spawn just means "nothing to
        // exclude yet", same as every other unguarded default-sentinel PlayerID field in this codebase (e.g.
        // EnemyBaseState.killerOwner).
        public PlayerID lastShotTargetId;

        public void Dispose()
        {
            b.contactCooldowns.Dispose();
            b.killLaunchHitIds.Dispose();
            innerHeld.Dispose();
            middleHeld.Dispose();
            outerHeld.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.innerHeld = innerHeld.Duplicate();
            copy.middleHeld = middleHeld.Duplicate();
            copy.outerHeld = outerHeld.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    private KingOrbiterPhase GetPhase(ref State s) => (KingOrbiterPhase)s.phaseId;

    [Header("Damage")]
    // Applied the instant a pleb is selected (PullWindup entry) — see orbitDamageMultiplier/
    // shootingDamageMultiplier for the other two phases' own overrides.
    [SerializeField] private float pullDamageMultiplier     = 1f;
    // Applied once a ring's own Pulling completes; stays applied through Orbit AND KnockbackWindup (held
    // plebs keep this multiplier while frozen for the knockback, per explicit design).
    [SerializeField] private float orbitDamageMultiplier    = 1f;
    // Applied at each ring's own ShootWindup entry.
    [SerializeField] private float shootingDamageMultiplier = 1f;

    [Header("Pull - Shared")]
    [SerializeField] private float grabRadius     = 14f;
    [SerializeField] private float windupDuration = 0.5f;
    [SerializeField] private float pullDuration   = 0.25f;

    [Header("Pull - Inner Ring")]
    [SerializeField] private int   innerCount             = 3;
    [SerializeField] private float innerOrbitRadius        = 5f;
    [SerializeField] private float innerOrbitAngularSpeed  = 180f;

    [Header("Pull - Middle Ring")]
    [SerializeField] private int   middleCount             = 5;
    [SerializeField] private float middleOrbitRadius        = 7.5f;
    [SerializeField] private float middleOrbitAngularSpeed  = 120f;

    [Header("Pull - Outer Ring")]
    [SerializeField] private int   outerCount             = 7;
    [SerializeField] private float outerOrbitRadius        = 10f;
    [SerializeField] private float outerOrbitAngularSpeed  = 60f;

    [Header("Orbit")]
    [SerializeField] private float orbitDuration            = 4f;
    // Same EffectiveMoveSpeed-override shape as OrbiterGloomPD.holdMoveSpeedMultiplier.
    [SerializeField] private float orbitMoveSpeedMultiplier = 1.5f;

    [Header("Knockback")]
    [SerializeField] private float        knockbackChargeTime = 1.5f;
    [SerializeField] private float        knockbackRadius     = 10f;
    [SerializeField] private float        knockbackForce      = 10f;
    [SerializeField] private GameObject   knockbackIndicator;
    [SerializeField] private WindupFillPD knockbackFill;

    [Header("Shooting")]
    [SerializeField] private float shootingRange     = 20f;
    [SerializeField] private float shotWindupDuration = 0.5f;
    [SerializeField] private float shotSpeed          = 25f;

    [Header("Attack Cadence")]
    [SerializeField] private float attackCooldown = 10f;

    // Single shared line-indicator pool, cloned to outerCount (the largest ring) in LateAwake() — same
    // Instantiate-N-times convention as OrbiterGloomPD.windupIndicatorTemplate. Reused for BOTH the Pull
    // Phase's per-ring windup and the Shooting Phase's per-ring aim lines — PullWindup and ShootWindup are
    // mutually exclusive phases (never active at the same time), so one pool safely covers both; only the
    // near/far-edge math and fill duration differ per phase (see UpdateLineIndicators).
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

    protected override void LateAwake()
    {
        base.LateAwake();
        // Full cooldown on spawn, not KingSpikePD's own half-cooldown grace — per explicit design, this
        // boss's Pull Phase would otherwise fire almost immediately, before the field has had a chance to
        // spawn more than a handful of plebs to actually grab.
        currentState.attackCooldownTimer = attackCooldown;
        BuildIndicatorPool(lineIndicatorTemplate, ref _lineIndicators, ref _lineFills);
    }

    // Guarded to a single real build per instance (LateAwake() re-fires on every pooled reuse) — see
    // OrbiterGloomPD.LateAwake's own identical guard for why.
    private void BuildIndicatorPool(Transform template, ref Transform[] indicators, ref WindupFillPD[] fills)
    {
        if (indicators != null || template == null) return;
        indicators = new Transform[outerCount];
        fills      = new WindupFillPD[outerCount];
        for (int i = 0; i < outerCount; i++)
        {
            var clone = Instantiate(template, template.parent);
            clone.gameObject.SetActive(false);
            indicators[i] = clone;
            fills[i]      = clone.GetComponentInChildren<WindupFillPD>(true);
        }
        template.gameObject.SetActive(false);
    }

    protected override State GetInitialState()
    {
        var s = base.GetInitialState();
        s.innerHeld  = DisposableList<PredictedObjectID>.Create(innerCount);
        s.middleHeld = DisposableList<PredictedObjectID>.Create(middleCount);
        s.outerHeld  = DisposableList<PredictedObjectID>.Create(outerCount);
        return s;
    }

    public override float EffectiveMoveSpeed =>
        base.EffectiveMoveSpeed * (GetPhase(ref currentState) == KingOrbiterPhase.Orbit ? orbitMoveSpeedMultiplier : 1f);

    // ── Ring helpers ─────────────────────────────────────────────────────────
    // Doubles once enraged (EnemyBasePD's own generic enrageTimer/b.enraged system — see the inherited
    // "Enrage" Inspector header; enrageTimer <= 0 leaves this a permanent no-op, matching every other
    // enemy that doesn't configure it) — a one-time, permanent-for-the-rest-of-this-life buff per explicit
    // design, since b.enraged only ever gets set once and never reverts. No OnEnrage() override needed —
    // B.enraged is just read directly here, same convention KingSpikePD's own enrage rework already uses.
    // Only affects the CAP used by future Pull Phases (TryGrabRing) — an already-completed pull's held
    // count doesn't retroactively grow if enrage lands mid-Orbit/Knockback/Shooting.
    private int RingCount(int ring)
    {
        int baseCount = ring switch { 0 => innerCount, 1 => middleCount, _ => outerCount };
        return B.enraged ? baseCount * 2 : baseCount;
    }
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
        state.attackCooldownTimer -= delta;
        var phase = GetPhase(ref state);

        switch (phase)
        {
            case KingOrbiterPhase.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f)
                {
                    state.pullRingIndex = 0;
                    // Turtles up for the WHOLE Pull Phase — one continuous invincibility window spanning
                    // every ring's windup+pulling (not per-ring flickering, per explicit design). Reverted
                    // once the entire Pull Phase resolves — see AdvancePullRingOrFinish's own doc comment.
                    SetHurtboxDisabled(true);
                    EnterPullWindup(ref state);
                }
                break;

            case KingOrbiterPhase.PullWindup:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) BeginPull(ref state);
                break;

            case KingOrbiterPhase.Pulling:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) EndPullingRing(ref state);
                break;

            case KingOrbiterPhase.Orbit:
                if (_target != null) HandleMovement(ref state, delta);
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) EnterKnockbackWindup(ref state);
                break;

            case KingOrbiterPhase.KnockbackWindup:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) FireKnockback(ref state);
                break;

            case KingOrbiterPhase.ShootWindup:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) FireShotRing(ref state);
                break;

            case KingOrbiterPhase.ShootFlight:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f) EndShotRing(ref state);
                break;
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

    // Selects this ring's candidates immediately (matches OrbiterGloomPD.TryStartGrab's own "lock in at
    // Windup entry, not at pull start" design — indicators/invulnerability need to be live for the whole
    // windup). Empty ring skips straight past without entering PullWindup at all.
    private void EnterPullWindup(ref State state)
    {
        int ring = state.pullRingIndex;
        if (!TryGrabRing(ref state, ring))
        {
            AdvancePullRingOrFinish(ref state);
            return;
        }
        state.phaseId    = (int)KingOrbiterPhase.PullWindup;
        state.phaseTimer = windupDuration;
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
            chosen.SetDamageOverride(true, damage * pullDamageMultiplier);
            chosen.ClaimForOrbit(ObjectId);
        }
        return true;
    }

    // Windup finished — starts the actual pull motion (kinematic/invulnerable/etc. already applied back in
    // TryGrabRing), matches OrbiterGloomPD.BeginPull.
    private void BeginPull(ref State state)
    {
        int ring  = state.pullRingIndex;
        var held  = RingHeldList(ref state, ring);
        int count = held.Count;
        for (int i = 0; i < count; i++)
        {
            var go = hierarchy.GetGameObject(held[i]);
            if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) continue;
            enemy.OrbitTarget(ObjectId, transform.position, RingRadius(ring), pullDuration, RingAngularSpeed(ring), SlotAngleDeg(i, count));
        }
        state.phaseId    = (int)KingOrbiterPhase.Pulling;
        state.phaseTimer = pullDuration;
    }

    // Pull duration elapsed (lands the same tick each held pleb's own internal pull-lerp completes, since
    // both derive from the same pullDuration) — pull -> orbit damage multiplier switch, mirrors
    // OrbiterGloomPD's own Pulling->Holding transition.
    private void EndPullingRing(ref State state)
    {
        int ring = state.pullRingIndex;
        ApplyDamageOverrideToRing(ref state, ring, damage * orbitDamageMultiplier);
        AdvancePullRingOrFinish(ref state);
    }

    // Shared tail for both "this ring found nobody" (EnterPullWindup) and "this ring finished pulling"
    // (EndPullingRing) — advances to the next ring's windup, or — once ring 2 (outer) is resolved either way
    // — runs the total-held safety check (2026-08-11 gap fix): an entirely-whiffed pull (e.g. a near-empty
    // field) must NOT enter Orbit holding nothing; falls back to Moving at FULL cooldown instead of an
    // immediate retry.
    private void AdvancePullRingOrFinish(ref State state)
    {
        if (state.pullRingIndex < 2)
        {
            state.pullRingIndex++;
            EnterPullWindup(ref state);
            return;
        }

        // All 3 rings resolved either way — the Pull Phase's own continuous invincibility window (see
        // HandleActiveState's Moving case for where it began) ends here, regardless of which branch below
        // this falls into.
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

        state.shootRingIndex = 0;
        EnterShootWindupOrAdvance(ref state);
    }

    // ── Shooting ─────────────────────────────────────────────────────────────

    // Entry for the current shootRingIndex — skips empty rings, aborts the whole phase if no valid targets
    // remain anywhere, otherwise picks this ring's shared target and starts its windup. Shared entry point
    // for the Knockback->Shooting transition and every ShootFlight->next-ring transition, so all the
    // "skip/abort" edge cases only need handling in one place.
    private void EnterShootWindupOrAdvance(ref State state)
    {
        while (state.shootRingIndex <= 2)
        {
            int ring = ShootPhysicalRing(state.shootRingIndex);
            if (RingHeldList(ref state, ring).Count == 0)
            {
                state.shootRingIndex++;
                continue;
            }

            // "if no valid targets remain, release held spikies and skip the rest of this phase" — read as
            // the whole Shooting Phase, not just this ring.
            if (!CharacterStatsPD.AnyValidTargetExists)
            {
                ReleaseAllHeld(ref state);
                state.attackCooldownTimer = attackCooldown;
                FindRandomTarget();
                state.phaseId = (int)KingOrbiterPhase.Moving;
                return;
            }

            PickShotTarget(ref state);
            ApplyDamageOverrideToRing(ref state, ring, damage * shootingDamageMultiplier);
            state.phaseId    = (int)KingOrbiterPhase.ShootWindup;
            state.phaseTimer = shotWindupDuration;
            return;
        }

        // All 3 rings resolved (shot or skipped-empty) — full attack cycle complete.
        state.attackCooldownTimer = attackCooldown;
        FindRandomTarget();
        state.phaseId = (int)KingOrbiterPhase.Moving;
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

    // Windup finished — launches every pleb in this ring toward _shotTargetSnapshot, individually aimed from
    // each pleb's own current position (all converging on the one shared target), each travelling the full
    // shootingRange regardless of what it hits along the way.
    private void FireShotRing(ref State state)
    {
        int ring = ShootPhysicalRing(state.shootRingIndex);
        var list = RingHeldList(ref state, ring);
        Vector3 targetPos = _shotTargetSnapshot != null ? _shotTargetSnapshot.transform.position : transform.position;

        for (int i = 0; i < list.Count; i++)
        {
            var go = hierarchy.GetGameObject(list[i]);
            if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) continue;

            Vector3 dir = targetPos - go.transform.position;
            dir.y = 0f;
            dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : transform.forward;
            enemy.LaunchAlong(dir, shootingRange, shotSpeed);
        }

        state.phaseId    = (int)KingOrbiterPhase.ShootFlight;
        state.phaseTimer = shootingRange / Mathf.Max(0.0001f, shotSpeed);
    }

    // Flight duration elapsed (self-tracked, lands the same tick each pleb's own LaunchAlong finishes since
    // both derive from the same shootingRange/shotSpeed) — full release, mirrors
    // OrbiterGloomPD.ReleaseAllHeld's per-pleb body, then moves on to the next ring.
    private void EndShotRing(ref State state)
    {
        int ring = ShootPhysicalRing(state.shootRingIndex);
        ReleaseRing(ref state, ring);
        state.shootRingIndex++;
        EnterShootWindupOrAdvance(ref state);
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
    }

    // Only Orbit is interruptible, per explicit design.
    protected override bool CanBeInterrupted => GetPhase(ref currentState) == KingOrbiterPhase.Orbit;

    // Unlike OrbiterGloomPD, interrupting this boss does NOT drop the held plebs — it just fast-forwards
    // straight into the Knockback Phase (same freeze-in-place entry as a natural Orbit timer expiry).
    protected override void OnInterrupted() => EnterKnockbackWindup(ref currentState);

    // ── View ─────────────────────────────────────────────────────────────────

    private KingOrbiterPhase _lastVisualState = (KingOrbiterPhase)(-1);

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);
        if (!gameObject.activeInHierarchy) return;

        if (knockbackIndicator != null) IndicatorMath.ApplyRadialTransform(knockbackIndicator.transform, knockbackRadius);

        var phase = GetPhase(ref viewState);
        UpdateLineIndicators(viewState, phase);

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

    // Shared pool for both windups (see _lineIndicators' own doc comment — PullWindup/ShootWindup never
    // overlap). Pull: near edge = this ring's orbit slot (radius/SlotAngleDeg offset), far edge = the live
    // pleb position — same math/reasoning as OrbiterGloomPD.UpdateView's own windup indicators. Shoot: near
    // edge = each pleb's own LIVE position (not a slot — they're not being pulled anywhere), far edge = that
    // position + a FIXED shootingRange span in the aimed direction (unlike the pull shape's live-distance
    // span) — individually aimed per pleb, all converging on the one shared _shotTargetSnapshot.
    private void UpdateLineIndicators(State viewState, KingOrbiterPhase phase)
    {
        if (_lineIndicators == null) return;

        bool pullActive = phase == KingOrbiterPhase.PullWindup;
        bool shotActive = phase == KingOrbiterPhase.ShootWindup && _shotTargetSnapshot != null;
        bool active     = pullActive || shotActive;

        int     ring          = pullActive ? viewState.pullRingIndex : ShootPhysicalRing(viewState.shootRingIndex);
        var     held          = active ? RingHeldList(ref viewState, ring) : default;
        int     heldCount     = active ? held.Count : 0;
        Vector3 shotTargetPos = shotActive ? _shotTargetSnapshot.transform.position : default;
        float   fillDuration  = pullActive ? windupDuration : shotWindupDuration;

        for (int i = 0; i < _lineIndicators.Length; i++)
        {
            var indicator = _lineIndicators[i];
            if (indicator == null) continue;

            bool slotFilled = active && i < heldCount;
            Vector3 plebPos = default;
            if (slotFilled)
            {
                var go = hierarchy.GetGameObject(held[i]);
                slotFilled = go != null;
                if (slotFilled) plebPos = go.transform.position;
            }

            var fill = _lineFills != null && i < _lineFills.Length ? _lineFills[i] : null;
            if (indicator.gameObject.activeSelf != slotFilled)
            {
                indicator.gameObject.SetActive(slotFilled);
                if (fill != null && !slotFilled) fill.ResetFill();
            }
            if (!slotFilled) continue;

            // Live, every frame — see feedback_windupfill_self_timed_drift: a self-timed StartFill() runs on
            // its own Time.deltaTime clock, independent of the tick-based phaseTimer that actually gates
            // BeginPull()/FireShotRing(), and can drift out of sync with it. viewState.phaseTimer is shared
            // across every phase, but this method only ever runs while pullActive/shotActive is true, so it
            // always reflects whichever windup (Pull or Shoot) is currently live.
            if (fill != null)
                fill.SetProgress(1f - Mathf.Clamp01(viewState.phaseTimer / fillDuration));

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
                Vector3 toTarget = shotTargetPos - plebPos;
                toTarget.y = 0f;
                float distance = toTarget.magnitude;
                nearEdge = plebPos;
                aim      = distance > 0.001f ? toTarget / distance : transform.forward;
                span     = shootingRange;
            }

            indicator.SetPositionAndRotation(nearEdge + aim * (span * 0.5f), Quaternion.LookRotation(aim));
            var scale = indicator.localScale;
            scale.z = span * 0.1f;
            indicator.localScale = scale;
        }
    }

    protected override void OnDeathStop()
    {
        HideIndicatorPool(_lineIndicators, _lineFills);
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
        currentState.pullRingIndex       = 0;
        currentState.shootRingIndex      = 0;
        currentState.lastShotTargetId    = default;
        currentState.innerHeld.Clear();
        currentState.middleHeld.Clear();
        currentState.outerHeld.Clear();
        OnDeathStop();
        // In case death/pooling happened mid-windup, while kinematic was on — HandleActiveState would
        // correct this on the next tick anyway (phaseId resets to Moving above), but resetting explicitly
        // here avoids even a one-tick window where a freshly-reused instance reads as kinematic.
        if (_rigidbody != null) _rigidbody.isKinematic = false;
        _lastVisualState    = (KingOrbiterPhase)(-1);
        _shotTargetSnapshot = null;
    }
}
