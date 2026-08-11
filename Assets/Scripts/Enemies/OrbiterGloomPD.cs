using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Moves toward players like a base Spiky (its own HandleActiveState only adds the grab sequence on top of
// the inherited default). On cooldown, grabs up to 3 nearby Pleb-tier enemies (SpawnCategory, not a type
// check — covers every difficulty tier's own Spiky-equivalent prefab uniformly) within grabRadius, pulls them
// in, and has them orbit for holdDuration while this enemy's own move speed is boosted — "using them like a
// player uses weapons." The actual pull/orbit position math lives generically on EnemyBasePD<TState>
// (LateSimulate() + OrbitTarget/StopOrbiting — see their own doc comments), not here; this class only
// orchestrates WHO gets grabbed, WHEN each phase transitions, and the windup indicators/move-speed boost.
public class OrbiterGloomPD : EnemyBasePD<OrbiterGloomPD.State>
{
    public enum OrbiterState { Moving, Windup, Pulling, Holding }

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   phaseId;
        public float phaseTimer;
        public float attackCooldownTimer;
        // Capacity 3, index doubles as the windupIndicators slot — see UpdateView's own use of the index.
        public DisposableList<PredictedObjectID> heldPlebs;

        public void Dispose()
        {
            b.contactCooldowns.Dispose();
            b.killLaunchHitIds.Dispose();
            heldPlebs.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.heldPlebs = heldPlebs.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    [Header("Grab")]
    [SerializeField] private float grabRadius = 10f;
    [SerializeField] private float grabCooldown = 8f;
    [SerializeField] private float windupDuration = 0.5f;
    [SerializeField] private float pullDuration = 0.25f;
    [SerializeField] private float holdDuration = 3f;
    [SerializeField] private float holdOrbitRadius = 2f;
    // Only holdOrbitRadius scales with size (an Elite is just a bigger prefab, size tuned up in the Editor
    // same as any other enemy) — every other tunable here (durations, angular speed, move-speed multiplier,
    // grab radius/cooldown) stays exactly as authored regardless of size. Same "* size" precedent
    // SpikeShieldPD's own orbitRadius/SpearGloomPD's own stabRange already use — read live everywhere
    // holdOrbitRadius would otherwise be used directly (BeginPull's actual pull target, UpdateView's
    // indicator aim) so the real orbit and its indicator can never drift out of sync with each other.
    private float EffectiveHoldOrbitRadius => holdOrbitRadius * size;
    [SerializeField] private float holdOrbitAngularSpeed = 180f;
    [SerializeField] private float holdMoveSpeedMultiplier = 1.5f;
    // Each held pleb's own damage is overridden to this Orbiter's own `damage * X` for the duration of the
    // hold (see IEnemyPD.SetDamageOverride's own doc comment) — "using a grabbed pleb like a weapon" hits as
    // hard as its wielder, scaled independently of the pleb's own tuning. Split (2026-08-11) into two separate
    // multipliers so Pulling (still being reeled in) and Holding (actively orbiting/chasing) can be tuned
    // independently — TryStartGrab applies pullDamageMultiplier at selection, HandleActiveState's own
    // Pulling->Holding transition switches every held pleb over to orbitDamageMultiplier.
    [SerializeField] private float pullDamageMultiplier  = 1f;
    [SerializeField] private float orbitDamageMultiplier = 1f;
    // Single source of truth for how many plebs one grab pulls — retuning this alone is enough to change the
    // count; no prefab surgery needed (see windupIndicatorTemplate's own comment for why).
    [SerializeField] private int maxGrabCount = 3;
    // ONE authored line indicator (same shape as SpearGloomPD's own WindupIndicator — an empty parent
    // Transform with a flattened-Cube "DangerIndicator" mesh child), kept inactive on the prefab and never
    // itself shown directly — LateAwake() clones it maxGrabCount times instead of requiring maxGrabCount
    // hand-placed copies in the Editor, so retuning maxGrabCount alone is enough, no re-wiring.
    [SerializeField] private Transform windupIndicatorTemplate;

    // Built once from windupIndicatorTemplate (see LateAwake()) — index i is aimed at heldPlebs[i], hidden
    // when that slot is empty or the current phase isn't Windup. Not serialized; these are runtime clones.
    private Transform[] _windupIndicators;
    // Parallel to _windupIndicators — resolved from each clone's own child (same WindupFill-under-
    // WindupIndicator shape SpearGloomPD's own template uses), null entries just mean that clone has none.
    private WindupFillPD[] _windupFills;

    // Scratch buffer for TryStartGrab — cleared and reused every call, same zero-alloc convention as every
    // other enemy's own scratch list in this codebase (e.g. ShooterGloomPD._multishotAngles).
    private static readonly List<IEnemyPD> _grabCandidates = new();

    private OrbiterState GetPhase(ref State s) => (OrbiterState)s.phaseId;

    protected override void LateAwake()
    {
        base.LateAwake();

        // Guaranteed at least one full cooldown before the first grab attempt, plus a randomized extra so
        // multiple Orbiters don't all attack in lockstep — same pattern every other timed-attack enemy in
        // this codebase uses (e.g. SpearGloomPD.spearAttackCooldownTimer). This was a real gap (2026-08-12
        // fix): nothing here or in GetInitialState() ever set an initial value at all, so attackCooldownTimer
        // defaulted to 0 and a freshly-spawned/pooled-reused Orbiter was immediately grab-ready the instant
        // it spawned. Re-runs on every pooled reuse since LateAwake() itself does (see the indicator-pool
        // guard right below for that same reasoning).
        currentState.attackCooldownTimer = grabCooldown + GameRandom.instance.NextFloat(0f, grabCooldown);

        // Guarded to a single real build per instance (LateAwake() re-fires on every pooled reuse, but the
        // clones themselves persist across a pooled life just like this GameObject itself does — no reason
        // to destroy/recreate them every reuse). Plain Unity Instantiate, not hierarchy.Create() — these are
        // purely cosmetic, driven entirely off this identity's own predicted state in UpdateView() (which
        // already runs identically on every machine), so they need no PredictedIdentity/networked existence
        // of their own, same as every other purely-decorative child object in this codebase.
        if (_windupIndicators == null && windupIndicatorTemplate != null)
        {
            _windupIndicators = new Transform[maxGrabCount];
            _windupFills      = new WindupFillPD[maxGrabCount];
            for (int i = 0; i < maxGrabCount; i++)
            {
                var clone = Instantiate(windupIndicatorTemplate, windupIndicatorTemplate.parent);
                clone.gameObject.SetActive(false);
                _windupIndicators[i] = clone;
                _windupFills[i]      = clone.GetComponentInChildren<WindupFillPD>(true);
            }
            windupIndicatorTemplate.gameObject.SetActive(false);
        }
    }

    protected override State GetInitialState()
    {
        var s = base.GetInitialState();
        s.heldPlebs = DisposableList<PredictedObjectID>.Create(maxGrabCount);
        return s;
    }

    // base.EffectiveMoveSpeed already folds in the shared slow-multiplier logic — this just layers the
    // hold-phase boost on top, without duplicating HandleMovement's own force/rotation code.
    public override float EffectiveMoveSpeed =>
        base.EffectiveMoveSpeed * (GetPhase(ref currentState) == OrbiterState.Holding ? holdMoveSpeedMultiplier : 1f);

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        state.attackCooldownTimer -= delta;
        var phase = GetPhase(ref state);

        switch (phase)
        {
            case OrbiterState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TryStartGrab(ref state))
                {
                    state.phaseId    = (int)OrbiterState.Windup;
                    state.phaseTimer = windupDuration;
                }
                break;

            // Stationary through Windup and Pulling (no HandleMovement call in either) — keeps the pull
            // destination math simple (owner position fixed for the whole pull) and reads as one committed
            // action, matching every other windup+strike enemy's own kinematic-through-the-sequence
            // convention in this codebase.
            case OrbiterState.Windup:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f)
                {
                    BeginPull(ref state);
                    // Turtles up for the pull itself — kinematic (see the tail of this method) + untargetable
                    // (also grants invulnerability internally, one-way coupling — see IEnemyPD.
                    // SetHurtboxDisabled's own doc comment), per explicit design: the reeling-in moment is
                    // protected, not vulnerable. Reverted the instant Holding begins below.
                    SetHurtboxDisabled(true);
                    state.phaseId    = (int)OrbiterState.Pulling;
                    state.phaseTimer = pullDuration;
                }
                break;

            case OrbiterState.Pulling:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f)
                {
                    // Vulnerable again the instant it starts actually chasing with its now-orbiting plebs —
                    // only isStunnable (below) stays off through Holding, per explicit design: a genuine
                    // "hit it with an interrupt to stop it" counter-play window, not a full invincibility
                    // window like Pulling was.
                    SetHurtboxDisabled(false);
                    // Pull -> orbit damage multiplier switch — see pullDamageMultiplier/orbitDamageMultiplier's
                    // own doc comment.
                    for (int i = 0; i < state.heldPlebs.Count; i++)
                    {
                        var go = hierarchy.GetGameObject(state.heldPlebs[i]);
                        if (go != null && go.TryGetComponent(out IEnemyPD enemy))
                            enemy.SetDamageOverride(true, damage * orbitDamageMultiplier);
                    }
                    state.phaseId    = (int)OrbiterState.Holding;
                    state.phaseTimer = holdDuration;
                }
                break;

            case OrbiterState.Holding:
                if (_target != null) HandleMovement(ref state, delta);
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f)
                {
                    ReleaseAllHeld(ref state);
                    state.phaseId             = (int)OrbiterState.Moving;
                    state.attackCooldownTimer = grabCooldown;
                }
                break;
        }

        // Not stunnable while actively chasing with orbiting plebs — a stun landing here doesn't freeze the
        // held-orbit itself either way (that logic lives entirely in each pleb's own LateSimulate(), reading
        // this Orbiter's live position — see EnemyBasePD.LateSimulate()'s own doc comment), but Interrupt()
        // still lands and forces it back to Moving/drops everything held (see OnInterrupted() below) — the
        // intended counter-play. Kinematic only through Pulling (Windup is already stationary via omission,
        // matching every other windup+strike enemy's own "no HandleMovement call" convention — see the
        // switch's own comment), same asymmetric "re-zero velocity on the entering edge only" shape every
        // other multi-phase enemy in this codebase uses.
        phase = GetPhase(ref state);
        isStunnable = phase != OrbiterState.Holding;
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = phase == OrbiterState.Pulling;
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }
    }

    // Locks in the 3 (or fewer, if that's all that's nearby — grabbing however many are available rather
    // than requiring exactly 3, per explicit design) targets immediately at Moving->Windup entry, not at
    // pull start, since the windup indicators and invulnerability both need to be live for the full 0.5s
    // delay.
    private bool TryStartGrab(ref State state)
    {
        if (EnemySpawnerPD.instance == null) return false;

        _grabCandidates.Clear();
        foreach (var enemy in EnemySpawnerPD.instance.ActiveEnemies)
        {
            if (enemy.SpawnCategory != EnemySpawnCategory.Pleb) continue;
            if (enemy._isDead || enemy.IsBeingHeld) continue;
            // Excludes a freshly-spawned-this-tick pleb whose own first Simulate() tick hasn't run yet —
            // see IEnemyPD.HasInitialized's own doc comment for the exact race this closes (a real,
            // confirmed-live bug: grabbing one before that tick runs let its own fresh-spawn reset silently
            // undo the grab's invincibility/hurtbox-disable the moment it finally did). Just means this
            // particular pleb isn't eligible THIS scan — harmless, it'll qualify normally next attack cycle.
            if (!enemy.HasInitialized) continue;
            if (Vector3.Distance(transform.position, enemy.transform.position) > grabRadius) continue;
            _grabCandidates.Add(enemy);
        }
        if (_grabCandidates.Count == 0) return false;

        // Furthest-within-range first, per explicit design — no more randomness in WHICH plebs get grabbed
        // (GameRandom is no longer used here at all). ObjectId is the tiebreaker for an exact-distance tie
        // (vanishingly rare, but two candidates could land on the identical float) — its underlying numeric
        // ID is assigned deterministically by PurrNet itself, identical on every machine, so the full sort
        // order (and therefore which plebs actually get taken) can never drift between machines or across a
        // resimulation replay of this same tick, unlike the raw HashSet enumeration order ActiveEnemies is
        // iterated in above (bucket-layout-dependent, NOT guaranteed stable — this was the confirmed root
        // cause of an earlier bug where a resimulated tick could pick a different pleb than originally
        // predicted, leaving the de-selected one stuck with no matching release ever coming).
        Vector3 origin = transform.position;
        _grabCandidates.Sort((a, b) =>
        {
            float distA = Vector3.Distance(origin, a.transform.position);
            float distB = Vector3.Distance(origin, b.transform.position);
            int cmp = distB.CompareTo(distA); // descending: furthest first
            return cmp != 0 ? cmp : a.ObjectId.instanceId.value.CompareTo(b.ObjectId.instanceId.value);
        });

        state.heldPlebs.Clear();
        int grabCount = Mathf.Min(maxGrabCount, _grabCandidates.Count);
        for (int i = 0; i < grabCount; i++)
        {
            var chosen = _grabCandidates[i];
            state.heldPlebs.Add(chosen.ObjectId);
            // Frozen the instant it's selected — not deferred to BeginPull (windup end) — per explicit
            // design: a targeted pleb shouldn't keep wandering during the whole windup delay. ClaimForOrbit
            // also flips IsBeingHeld true immediately, so a second Orbiter's own grab-scan (the foreach
            // above) correctly excludes this pleb starting THIS tick, not just once the pull actually begins.
            chosen.SetKinematic(true);
            chosen.SetMovementDisabled(true);
            chosen.SetPhysicsColliderDisabled(true);
            // Untargetable, same reasoning LeaperGloomPD already uses mid-leap — also grants invulnerability
            // internally (see IEnemyPD.SetHurtboxDisabled's own doc comment for the one-way coupling), so no
            // separate SetInvulnerable(true) call needed here — a homing/pierce/cleave effect wasting itself
            // on a pleb that can't be meaningfully damaged anyway is exactly the gap this closes.
            chosen.SetHurtboxDisabled(true);
            // "Using a grabbed pleb like a weapon" — see pullDamageMultiplier/orbitDamageMultiplier's own doc
            // comment. Applied from the moment of selection, same as every other freeze/untargetable effect
            // above, not deferred to BeginPull — switched to orbitDamageMultiplier once Holding actually
            // begins (see HandleActiveState's own Pulling->Holding transition).
            chosen.SetDamageOverride(true, damage * pullDamageMultiplier);
            chosen.ClaimForOrbit(ObjectId);
        }
        return true;
    }

    // Evenly-spaced slot angle for held-index i out of `count` currently-held plebs (0/count fraction of a
    // full circle, base 0 = world +X) — the single source of truth for where each held pleb ends up, used
    // identically here (the actual pull target) and in UpdateView (the windup indicator's aim point), so the
    // indicator always shows exactly where a pleb is about to be pulled to, never its live position.
    private static float SlotAngleDeg(int index, int count) => index * (360f / count);

    // Kinematic/movement-disabled/invulnerable are already applied back in TryStartGrab (the instant each
    // pleb was selected) — this only starts the actual pull MOTION now that the windup has finished.
    private void BeginPull(ref State state)
    {
        int count = state.heldPlebs.Count;
        for (int i = 0; i < count; i++)
        {
            var go = hierarchy.GetGameObject(state.heldPlebs[i]);
            // Dead-target guard — a claimed pleb can still go away mid-windup (e.g. EnemySpawnerPD's
            // pre-boss WipeAllEnemies doesn't check invulnerability). Left in heldPlebs/still counted toward
            // SlotAngleDeg's spacing either way (removing it here would mean re-spacing everyone else
            // mid-sequence for a vanishingly rare case) — just skipped so it doesn't error trying to orbit
            // something that no longer resolves.
            if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead) continue;
            enemy.OrbitTarget(ObjectId, transform.position, EffectiveHoldOrbitRadius, pullDuration, holdOrbitAngularSpeed, SlotAngleDeg(i, count));
        }
    }

    // Reverses every effect BeginPull/TryStartGrab applied, on whichever plebs are currently held regardless
    // of which phase this was called from (natural hold-expiry, death, or Interrupt()) — a held pleb's own
    // orbit fields self-clear via StopOrbiting() rather than needing this to know their exact sub-phase.
    private void ReleaseAllHeld(ref State state)
    {
        for (int i = 0; i < state.heldPlebs.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.heldPlebs[i]);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy))
            {
                enemy.StopOrbiting();
                enemy.SetKinematic(false);
                enemy.SetMovementDisabled(false);
                enemy.SetPhysicsColliderDisabled(false);
                // Also reverts invulnerability internally — see IEnemyPD.SetHurtboxDisabled's own doc
                // comment for the one-way coupling — so no separate SetInvulnerable(false) call needed here.
                enemy.SetHurtboxDisabled(false);
                enemy.SetDamageOverride(false);
            }
        }
        state.heldPlebs.Clear();
    }

    // "Immediately release" requires this to happen in the SAME call as B.isDead flipping true, not deferred
    // to eventual corpse teardown (Destroyed() could fire many ticks later if any death-hold effect is
    // active) — see Die()'s own base doc comment for why deathHoldCount can delay that.
    protected override void Die()
    {
        base.Die();
        ReleaseAllHeld(ref currentState);
    }

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above) and
    // meant to be cancelable — CanBeInterrupted now defaults to false (see EnemyBasePD's own doc comment).
    // Only Holding qualifies: it's the one phase actually meant to have a real "hit it with an interrupt to
    // stop it" counter-play window (per explicit design). Windup/Pulling are stunnable (not committed) and
    // are no longer interruptible either, superseding the earlier "Interrupt() cancels the grab at any
    // phase" behavior.
    protected override bool CanBeInterrupted => GetPhase(ref currentState) == OrbiterState.Holding;

    // Drops everything held and returns to Moving — can now only ever fire during Holding (see
    // CanBeInterrupted above), so hurtboxDisabled is always already false by this point (only ever set true
    // during Pulling, long reverted by the time Holding begins) — no extra revert needed here.
    protected override void OnInterrupted()
    {
        ReleaseAllHeld(ref currentState);
        currentState.phaseId    = (int)OrbiterState.Moving;
        currentState.phaseTimer = 0f;
    }

    protected override void OnDeathStop()
    {
        if (_windupIndicators == null) return;
        foreach (var indicator in _windupIndicators)
            if (indicator != null) indicator.gameObject.SetActive(false);
        if (_windupFills == null) return;
        foreach (var fill in _windupFills)
            if (fill != null) fill.ResetFill();
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);
        if (_windupIndicators == null || !gameObject.activeInHierarchy) return;

        bool windupActive = GetPhase(ref viewState) == OrbiterState.Windup;
        int  heldCount    = viewState.heldPlebs.Count;
        for (int i = 0; i < _windupIndicators.Length; i++)
        {
            var indicator = _windupIndicators[i];
            if (indicator == null) continue;

            // Near edge = the orbit slot itself (this Orbiter's position + the holdOrbitRadius/SlotAngleDeg
            // offset this pleb is being pulled TO), NOT this Orbiter's own center — far edge = the target
            // pleb's actual LIVE position (where it's being pulled FROM). The indicator is the pull path,
            // not a targeting line from the Orbiter to something.
            bool slotFilled = windupActive && i < heldCount;
            Vector3 spikyPos = default;
            if (slotFilled)
            {
                var go = hierarchy.GetGameObject(viewState.heldPlebs[i]);
                slotFilled = go != null;
                if (slotFilled) spikyPos = go.transform.position;
            }

            var fill = _windupFills != null && i < _windupFills.Length ? _windupFills[i] : null;
            if (indicator.gameObject.activeSelf != slotFilled)
            {
                indicator.gameObject.SetActive(slotFilled);
                if (fill != null && !slotFilled) fill.ResetFill();
            }
            if (!slotFilled) continue;

            // Live, every frame — see feedback_windupfill_self_timed_drift: a self-timed StartFill() runs on
            // its own Time.deltaTime clock, independent of the tick-based phaseTimer that actually gates
            // BeginPull(), and can drift out of sync with it even though windupDuration itself is fixed
            // (never attack-speed-scaled) — a fixed duration doesn't guarantee the two clocks stay aligned.
            if (fill != null)
                fill.SetProgress(1f - Mathf.Clamp01(viewState.phaseTimer / windupDuration));

            float   rad = SlotAngleDeg(i, heldCount) * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
            Vector3 slotPos = transform.position + dir * EffectiveHoldOrbitRadius;

            Vector3 toTarget = spikyPos - slotPos;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            if (distance <= 0.001f) continue;
            Vector3 aim = toTarget / distance;

            // Positioned directly at the WORLD-space midpoint between the two edges above, then scaled to
            // span exactly that distance — NOT IndicatorMath.ApplyLineTransform, which offsets along the
            // indicator's own LOCAL Z assuming its rotation stays fixed/inherited. Every other line indicator
            // in this codebase only ever faces one direction (the owning enemy's own body facing), so that
            // assumption holds; these 3 indicators each independently rotate to face a DIFFERENT live target
            // every frame, which broke that assumption (indicators were shooting out both sides of the
            // Orbiter instead of stopping at their target — a real, confirmed bug, not theoretical).
            indicator.SetPositionAndRotation(slotPos + aim * (distance * 0.5f), Quaternion.LookRotation(aim));
            var scale = indicator.localScale;
            scale.z = distance * 0.1f;
            indicator.localScale = scale;
        }
    }
}
