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
                    state.phaseId    = (int)OrbiterState.Pulling;
                    state.phaseTimer = pullDuration;
                }
                break;

            case OrbiterState.Pulling:
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f)
                {
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

    // Cancels the grab the same as death, per explicit design decision — an Interrupt() (e.g. a stun+interrupt
    // combo ability) mid-windup/pull/hold ends it immediately rather than the sequence being immune to it.
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

            if (indicator.gameObject.activeSelf != slotFilled)
            {
                indicator.gameObject.SetActive(slotFilled);
                // Self-timed, not SetProgress-driven — windupDuration is fixed (Orbiter's own windup isn't
                // attack-speed-scaled, unlike e.g. SpearGloomPD's), so there's no live progress to react to,
                // same reasoning SpikeShieldPD's own windup fill already uses.
                var fill = _windupFills != null && i < _windupFills.Length ? _windupFills[i] : null;
                if (fill != null)
                {
                    if (slotFilled) fill.StartFill(windupDuration);
                    else             fill.ResetFill();
                }
            }
            if (!slotFilled) continue;

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
