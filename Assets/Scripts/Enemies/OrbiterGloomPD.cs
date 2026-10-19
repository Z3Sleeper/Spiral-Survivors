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
    public enum OrbiterState { Moving, Pulling, Holding }

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   phaseId;
        // Dual-purpose, per phase (same convention KingSpikePD's own remoteSequenceTimer already uses):
        // counts UP (elapsed time since Pulling began) while phaseId == Pulling, counts DOWN (remaining
        // time) while phaseId == Holding — see each case's own comment in HandleActiveState.
        public float phaseTimer;
        public float attackCooldownTimer;
        // Capacity 3, index doubles as the windupIndicators slot — see UpdateView's own use of the index.
        public DisposableList<PredictedObjectID> heldPlebs;
        // Snapshotted IEnemyPD.SpawnGeneration for each heldPlebs entry, at the exact tick it's grabbed
        // (2026-10-16 fix) — same mechanism as KingOrbiterPD.persistentHeldGeneration's own doc comment: a
        // raw PredictedObjectID alone can't tell any of the many hierarchy.GetGameObject(heldPlebs[i]) call
        // sites below whether they're still resolving to the SAME pleb originally grabbed, versus a
        // pooled-reuse or PredictedHierarchy instance-ID-collision victim now silently wearing that id
        // (client-only — only non-host clients ever resimulate, which is when the collision bug fires).
        // Without this, DispatchPull/BeginOrbiting/RefreshHeldDamageOverrides/ReleaseAllHeld could all act on
        // a completely unrelated enemy that happens to occupy the same id now.
        public DisposableList<int>               heldPlebGeneration;
        // How many of heldPlebs have had DispatchPull() called on them so far during the CURRENT Pulling
        // phase — see DispatchPull's own doc comment for the one-at-a-time sequencing this drives. Reset to
        // 0 wherever Pulling begins (Moving's own TryStartGrab success branch).
        public int pullSequenceIndex;
        // Runtime-scaled — see EnemyBaseState.scaledMaxHealth's own doc comment for why these live in State
        // rather than as a plain field mutated by OnConfigureSpawn (2026-09-22 follow-up pass).
        public int   scaledMaxGrabCount;
        public float scaledAttackCooldown;

        public void Dispose()
        {
            b.contactCooldowns.Dispose();
            b.killLaunchHitIds.Dispose();
            heldPlebs.Dispose();
            heldPlebGeneration.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.heldPlebs = heldPlebs.Duplicate();
            copy.heldPlebGeneration = heldPlebGeneration.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    [Header("Grab")]
    [SerializeField] private float grabRadius = 15f;
    [SerializeField] private float windupDuration = 1f;
    // Flat units/sec (2026-09-12, was pullDuration — a flat seconds-to-arrive value, meaning every held
    // pleb took the same time regardless of how far it started) — matches KingOrbiterPD's own shotSpeed
    // value (30f) per explicit design, so both bosses' pull feels like the same consistent travel speed.
    // See IEnemyPD.OrbitTarget's own doc comment for the MoveTowards mechanic this now drives.
    [SerializeField] private float pullSpeed = 30f;
    // Gap between each held pleb's own windup START (2026-08-17 — used to pull the whole group in one shot)
    // — pleb i's own windup begins i * pullSequentialDelay after Pulling started, REGARDLESS of whether an
    // earlier pleb's own windup/pull has finished yet (confirmed live design intent: windups overlap, they
    // don't queue behind each other) — see DispatchPull's own doc comment for the full timing model.
    // Independently tunable per tier/prefab, per explicit design (intended to shrink as tiers get tuned
    // harder).
    [SerializeField] private float pullSequentialDelay = 0.25f;
    [SerializeField] private float holdDuration = 3f;
    [SerializeField] private float holdOrbitRadius = 2f;
    // Only holdOrbitRadius scales with size (an Elite is just a bigger prefab, size tuned up in the Editor
    // same as any other enemy) — every other tunable here (durations, angular speed, grab radius/cooldown)
    // stays exactly as authored regardless of size. Same "* size" precedent SpikeShieldPD's own orbitRadius/
    // SpearGloomPD's own stabRange already use — read live everywhere holdOrbitRadius would otherwise be used
    // directly (BeginPull's actual pull target, UpdateView's indicator aim) so the real orbit and its
    // indicator can never drift out of sync with each other.
    private float EffectiveHoldOrbitRadius => holdOrbitRadius * size;
    [SerializeField] private float holdOrbitAngularSpeed = 120f;
    // Each held pleb's own damage is overridden to this Orbiter's own `damage * X` for the duration of the
    // hold (see IEnemyPD.SetDamageOverride's own doc comment) — "using a grabbed pleb like a weapon" hits as
    // hard as its wielder, scaled independently of the pleb's own tuning. Split (2026-08-11) into two separate
    // multipliers so Pulling (still being reeled in) and Holding (actively orbiting/chasing) can be tuned
    // independently — RefreshHeldDamageOverrides (called every Simulate tick, 2026-09-16) is the real source
    // of truth for which one currently applies to each held pleb, switching it the instant THAT pleb's own
    // orbitPhase moves past Pulling, independent of whether the rest of the group has also arrived yet.
    [SerializeField] private float pullDamageMultiplier  = 1f;
    [SerializeField] private float orbitDamageMultiplier = 0.5f;
    // Single source of truth for how many plebs one grab pulls — retuning this alone is enough to change the
    // count; no prefab surgery needed (see windupIndicatorTemplate's own comment for why). Tier1-authored,
    // pristine read-only baseline forever (2026-09-22) — OnConfigureSpawn scales it into
    // State.scaledMaxGrabCount instead of mutating this in place, so no _authoredX snapshot/reset is needed
    // anymore (see EnemyBaseState.scaledMaxHealth's own doc comment for the full reasoning).
    // EnsureIndicatorPoolCapacity/GetInitialState's own heldPlebs sizing were ALREADY built (2026-08-19)
    // anticipating this exact future tier-scaling — see their own doc comments.
    [SerializeField] private int maxGrabCount = 2;

    [Header("Attack Cooldown")]
    // Same pristine-baseline treatment as maxGrabCount above — scales into State.scaledAttackCooldown.
    [SerializeField] private float attackCooldown = 5f;

    [Header("Tier Scaling")]
    // Added onto maxGrabCount per tier above 1 — see OnConfigureSpawn().
    [SerializeField] private int maxGrabCountTierGrowth = 1;

    // ONE authored line indicator (same shape as SpearGloomPD's own WindupIndicator — an empty parent
    // Transform with a flattened-Cube "DangerIndicator" mesh child), kept inactive on the prefab and never
    // itself shown directly — LateAwake() clones it maxGrabCount times instead of requiring maxGrabCount
    // hand-placed copies in the Editor, so retuning maxGrabCount alone is enough, no re-wiring.
    [Header("Indicators")]
    [SerializeField] private Transform windupIndicatorTemplate;

    // Built once from windupIndicatorTemplate (see LateAwake()) — index i is aimed at heldPlebs[i], hidden
    // when that slot is empty or outside its own individual windup window (see UpdateView — each slot's
    // window is independent now, not tied to a single shared phase). Not serialized; these are runtime
    // clones.
    private Transform[] _windupIndicators;
    // Parallel to _windupIndicators — resolved from each clone's own child (same WindupFill-under-
    // WindupIndicator shape SpearGloomPD's own template uses), null entries just mean that clone has none.
    private WindupFillPD[] _windupFills;

    // Scratch buffer for TryStartGrab — cleared and reused every call, same zero-alloc convention as every
    // other enemy's own scratch list in this codebase (e.g. ShooterGloomPD._multishotAngles).
    private static readonly List<IEnemyPD> _grabCandidates = new();
    // Scratch buffer for OnInterrupted() — snapshots heldPlebs before ReleaseAllHeld() clears it, so the
    // post-release interrupt-stun loop still has something to iterate. See OnInterrupted's own doc comment.
    private static readonly List<PredictedObjectID> _interruptStunScratch = new();

    // Snapshot of exactly who Die() just released — see OnHiddenStateChanged's own doc comment for why this
    // exists (2026-10-16). Per-instance, not static scratch — unlike _interruptStunScratch above, this needs
    // to survive across multiple real frames (until the verified-revert check in OnHiddenStateChanged either
    // confirms the death or reclaims these plebs), not just within one method call.
    private readonly List<PredictedObjectID> _pendingReclaimPlebs = new();
    // Snapshotted alongside _pendingReclaimPlebs (2026-10-16 fix) — same "verify, don't just trust the id"
    // reasoning as heldPlebGeneration's own doc comment, applied to the reclaim path too.
    private readonly List<int> _pendingReclaimPlebGeneration = new();

    // Resolves heldPlebs[i] to its IEnemyPD, verifying it's still the SAME logical pleb originally grabbed
    // by TryStartGrab — not a pooled-reuse or PredictedHierarchy instance-ID-collision victim now wearing
    // its id (see heldPlebGeneration's own doc comment). A mismatch is treated exactly like every other
    // "target gone/invalid" case in this class.
    private bool TryResolveHeldPleb(ref State state, int i, out IEnemyPD enemy)
    {
        enemy = null;
        var go = hierarchy.GetGameObject(state.heldPlebs[i]);
        if (go == null || !go.TryGetComponent(out IEnemyPD candidate)) return false;
        if (candidate.SpawnGeneration != state.heldPlebGeneration[i]) return false;
        enemy = candidate;
        return true;
    }

    private OrbiterState GetPhase(ref State s) => (OrbiterState)s.phaseId;

    protected override void LateAwake()
    {
        base.LateAwake();

        // Guaranteed at least one full cooldown before the first grab attempt. This was a real gap (2026-08-12
        // fix): nothing here or in GetInitialState() ever set an initial value at all, so attackCooldownTimer
        // defaulted to 0 and a freshly-spawned/pooled-reused Orbiter was immediately grab-ready the instant
        // it spawned. Re-runs on every pooled reuse since LateAwake() itself does (see the indicator-pool
        // guard right below for that same reasoning).
        currentState.attackCooldownTimer = attackCooldown;

        // Indicator pool build moved to OnConfigureSpawn() (2026-08-19) — see EnsureIndicatorPoolCapacity's
        // own doc comment for why: maxGrabCount may end up per-tier-scaled there in the future, and
        // ConfigureSpawn always runs AFTER LateAwake, so sizing the pool here would lock it to the
        // PRE-scaling count.
    }

    // Lazy + GROWABLE, not build-once (2026-08-19) — called from OnConfigureSpawn(), after any future
    // per-tier scaling on maxGrabCount has already been applied (see OnConfigureSpawn's own doc comment on
    // EnemyBasePD for the general pattern), so this always sees the FINAL count for whichever life is
    // currently spawning. Growable specifically because a single pooled instance can be reused across
    // DIFFERENT tiers over its lifetime (pooling has no guarantee reuse only ever goes same-or-higher tier),
    // so a fixed build-once array (the old LateAwake()-based approach) could come up short on a LATER,
    // higher-tier life even though it was correctly sized for whichever life built it first. Never shrinks —
    // a later lower-tier life just uses a prefix of the larger arrays; every other read site already scopes
    // to heldPlebs.Count/the current grab count, not the array's own Length.
    // Reads currentState.scaledMaxGrabCount, not the plain maxGrabCount field (2026-09-22) — also now
    // called defensively from TryStartGrab (not just OnConfigureSpawn) so a machine that never itself ran
    // OnConfigureSpawn for this instance (a PredictedHierarchy-reconciled enemy — see
    // EnemyBaseState.scaledMaxHealth's own doc comment) still grows the pool before it's actually needed,
    // rather than staying stuck undersized forever. Cheap/idempotent either way (early-returns once already
    // big enough), so calling it an extra time here costs nothing in the common case.
    private void EnsureIndicatorPoolCapacity()
    {
        if (windupIndicatorTemplate == null) return;
        int have = _windupIndicators?.Length ?? 0;
        int need = currentState.scaledMaxGrabCount;
        if (have >= need) return;

        var newIndicators = new Transform[need];
        var newFills = new WindupFillPD[need];
        for (int i = 0; i < have; i++)
        {
            newIndicators[i] = _windupIndicators[i];
            newFills[i] = _windupFills[i];
        }
        for (int i = have; i < need; i++)
        {
            var clone = Instantiate(windupIndicatorTemplate, windupIndicatorTemplate.parent);
            clone.gameObject.SetActive(false);
            newIndicators[i] = clone;
            newFills[i] = clone.GetComponentInChildren<WindupFillPD>(true);
        }
        _windupIndicators = newIndicators;
        _windupFills = newFills;
        windupIndicatorTemplate.gameObject.SetActive(false);
    }

    // Tier1-authored maxGrabCount, scaled by maxGrabCountTierGrowth per tier above 1 (see that field's own
    // doc comment). spawnTier is already final by the time this runs (set earlier in the same
    // ConfigureSpawn() call) — scaled BEFORE EnsureIndicatorPoolCapacity() so the pool always sizes to this
    // life's FINAL count.
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        currentState.scaledMaxGrabCount = maxGrabCount + (spawnTier - 1) * maxGrabCountTierGrowth;
        EnsureIndicatorPoolCapacity();

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        currentState.scaledAttackCooldown = attackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
    }

    protected override State GetInitialState()
    {
        var s = base.GetInitialState();
        s.scaledMaxGrabCount = maxGrabCount;
        s.scaledAttackCooldown = attackCooldown;
        s.heldPlebs = DisposableList<PredictedObjectID>.Create(maxGrabCount);
        s.heldPlebGeneration = DisposableList<int>.Create(maxGrabCount);
        return s;
    }

    // Overridden (2026-09-16) so RefreshHeldDamageOverrides below runs EVERY tick regardless of dashing/
    // offset-chasing — base EnemyBasePD.Simulate() only calls HandleActiveState() when neither of those is
    // true, so logic that must run unconditionally every tick has to live here instead (same gotcha
    // KingOrbiterPD/BlitzGloomPD/KingSpikePD's own identical Simulate() overrides already exist for).
    protected override void Simulate(ref State state, float delta)
    {
        base.Simulate(ref state, delta);
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (B.isDead || B.wiped) return;
        RefreshHeldDamageOverrides(ref state);
    }

    // Keeps every currently-held pleb's own contact-damage override correctly matched to its LIVE orbitPhase,
    // every tick — 2026-09-16 fix, same reasoning/shape as KingOrbiterPD's own identical
    // RefreshHeldDamageOverrides (see that method's own doc comment for the full "why"): a pleb should STOP
    // dealing pullDamageMultiplier's elevated contact damage the INSTANT its own pull movement ends (reaches
    // WaitingToOrbit), even before the REST of this group has also arrived and the old batch-gated
    // AllArrivedAtOrbitSlot switch (HandleActiveState's own Pulling case, below) actually fires. Supersedes
    // that batch switch and TryStartGrab's own initial one-time set as the real source of truth — recomputing
    // fresh every tick from each pleb's own live IsPulling means there's no edge-detection/timing state to
    // track or get wrong here. No EnrageDamageMultiplier factor — matches this class's own existing damage*
    // multiplier formula shape exactly (unlike KingOrbiterPD, a boss, this normal enemy's damage override
    // never included that factor to begin with).
    private void RefreshHeldDamageOverrides(ref State state)
    {
        // See IEnemyPD.SetOrbitFrozen's own doc comment for why this reads BOTH IsTrueStunned and isStunnable
        // — a true stun can land and tick down even mid-Pulling (see Stun()'s own doc comment), but only
        // actually FREEZES this Orbiter's own Simulate() while isStunnable is true; a held pleb should only
        // visually freeze/stop dealing damage in lockstep with THIS Orbiter genuinely being frozen right now,
        // not merely carrying an unexpired timer through a phase it can't be interrupted by one during.
        bool ownerFrozen = IsTrueStunned && isStunnable;
        for (int i = 0; i < state.heldPlebs.Count; i++)
        {
            if (!TryResolveHeldPleb(ref state, i, out var enemy) || enemy._isDead) continue;
            bool isPulling = enemy.IsPulling;
            float multiplier = isPulling ? pullDamageMultiplier : orbitDamageMultiplier;
            enemy.SetDamageOverride(true, damage * multiplier);
            // Pulling is left unaffected by this Orbiter's own stun — only the actively-orbiting (or
            // arrived-and-waiting) sub-phases freeze, matching the "orbiting phase" scope this was asked for.
            enemy.SetOrbitFrozen(ownerFrozen && !isPulling);
        }
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        var phase = GetPhase(ref state);
        // Only ticks while actually waiting to grab again — NOT mid-Pulling/Holding (2026-08-19 fix). Unlike
        // some other enemies' own attackCooldownTimer, which deliberately keeps ticking through the whole
        // attack sequence for overlap pacing (e.g. SpearGloomPD's own spearAttackCooldownTimer — "pacing
        // between whole attack sequences, not attack execution speed"), Orbiter's own sequence explicitly
        // should NOT burn down cooldown while it's actively pulling/holding plebs — per design, the cooldown
        // is meant to gate re-entry into Moving's own "ready to grab again" check specifically, not to overlap
        // with the grab+hold sequence itself.
        if (phase == OrbiterState.Moving)
            state.attackCooldownTimer -= delta;

        switch (phase)
        {
            case OrbiterState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f && TryStartGrab(ref state))
                {
                    // Straight into Pulling — no separate shared Windup phase anymore (2026-08-17); each
                    // held pleb now gets its OWN windup, staggered by pullSequentialDelay — see this case's
                    // own comment below for the full timing model.
                    state.phaseId    = (int)OrbiterState.Pulling;
                    state.phaseTimer = 0f;
                    state.pullSequenceIndex = 0;
                    // Turtles up the instant selection locks in — invulnerable through the WHOLE committed
                    // sequence (selection -> pull -> right up until it actually starts orbiting/chasing),
                    // not just the pull motion itself (also grants untargetability internally, one-way
                    // coupling — see IEnemyPD.SetHurtboxDisabled's own doc comment). Reverted the instant
                    // Holding begins below.
                    SetHurtboxDisabled(true);
                }
                break;

            // Stationary through the whole Pulling sequence (no HandleMovement call) — keeps the pull
            // destination math simple (owner position fixed for the whole pull) and reads as one committed
            // action, matching every other windup+strike enemy's own kinematic-through-the-sequence
            // convention in this codebase.
            //
            // phaseTimer counts UP here (elapsed since Pulling began), not down — held pleb i's own windup
            // window is [i * pullSequentialDelay, i * pullSequentialDelay + windupDuration), and its actual
            // OrbitTarget() dispatch lands the instant that window closes. Confirmed live design intent
            // (2026-08-17): each pleb's windup starts on its OWN staggered schedule regardless of whether an
            // earlier pleb's windup/pull has finished — NOT gated behind the previous one completing first
            // — so with windupDuration > pullSequentialDelay (the common case), multiple plebs sit mid-
            // windup simultaneously. See DispatchPull's own comment for the exact dispatch condition, and
            // UpdateView for how the same per-slot window math drives each indicator independently.
            case OrbiterState.Pulling:
            {
                // Scaled by EffectiveAttackSpeed (2026-08-31, was plain delta) — see
                // KingOrbiterPD.HandleActiveState's own identical Pulling-case change for why (the
                // dispatch/release thresholds below stay fixed constants; this only changes how fast
                // phaseTimer climbs toward them).
                state.phaseTimer += delta * EffectiveAttackSpeed;
                int count = state.heldPlebs.Count;

                // While loop, not if — guards against a single tick's delta covering more than one
                // scheduled dispatch (a brief stall), so a pull can never be silently skipped, same
                // convention RogueSpinAbilityPD's own hit-scheduling loop uses.
                while (state.pullSequenceIndex < count &&
                       state.phaseTimer >= state.pullSequenceIndex * pullSequentialDelay + windupDuration)
                {
                    DispatchPull(ref state, state.pullSequenceIndex);
                    state.pullSequenceIndex++;
                }

                // Every held pleb has been dispatched AND has actually arrived at its own orbit slot (see
                // AllArrivedAtOrbitSlot below — 2026-08-31, replaced a phaseTimer-threshold ESTIMATE of when
                // the last-dispatched pleb should have finished pulling in, same fix as
                // KingOrbiterPD.HandleActiveState's own identical Pulling-case change; see
                // IEnemyPD.IsWaitingToOrbit's own doc comment for why) — vulnerable again the instant it
                // starts actually chasing with its now-orbiting plebs — only isStunnable (below) stays off
                // through Holding, per explicit design: a genuine "hit it with an interrupt to stop it"
                // counter-play window, not a full invincibility window like Pulling was.
                if (count > 0 && state.pullSequenceIndex >= count && AllArrivedAtOrbitSlot(ref state))
                {
                    SetHurtboxDisabled(false);
                    // No longer sets the orbit damage override here (2026-09-16 — RefreshHeldDamageOverrides
                    // already switched each pleb over individually the instant IT finished pulling, not
                    // gated on the whole group like this release point is). Still releases every held pleb
                    // into actually orbiting (BeginOrbiting) — each one has been frozen at its own slot
                    // (WaitingToOrbit) since finishing its own staggered pull-in; releasing them all in this
                    // SAME tick is what keeps their angular spacing exactly even instead of drifting (see
                    // EnemyOrbitPhase's own doc comment).
                    for (int i = 0; i < count; i++)
                    {
                        if (TryResolveHeldPleb(ref state, i, out var enemy))
                            enemy.BeginOrbiting();
                    }
                    state.phaseId    = (int)OrbiterState.Holding;
                    state.phaseTimer = holdDuration;
                }
                break;
            }

            case OrbiterState.Holding:
                if (_target != null) HandleMovement(ref state, delta);
                state.phaseTimer -= delta;
                if (state.phaseTimer <= 0f)
                {
                    ReleaseAllHeld(ref state);
                    state.phaseId             = (int)OrbiterState.Moving;
                    state.attackCooldownTimer = state.scaledAttackCooldown;
                }
                break;
        }

        // Stunnable in every phase, including Holding (2026-09-21 fix — matches KingOrbiterPD.HandleActiveState's
        // own identical formula, which never excluded its own Orbit phase to begin with). A true stun freezes
        // this Orbiter's own Simulate() (see EnemyBasePD.Simulate()'s own isStunnable-gated freeze) — HandleMovement
        // and this phaseTimer countdown both pause with it — but does NOT touch the held-orbit itself: each held
        // pleb's own position math lives entirely in ITS OWN LateSimulate() (reading this Orbiter's live, now-
        // frozen, position — see EnemyBasePD.LateSimulate()'s own doc comment), so the plebs keep orbiting in
        // place around the stunned Orbiter rather than freezing themselves. Interrupt() is still the only thing
        // that actually ends Holding early / drops everything held (see OnInterrupted() below) — the intended
        // counter-play, unaffected by this change. Kinematic through the whole Pulling sequence (windup+pull
        // both, now merged into one phase — see that case's own comment), same asymmetric "re-zero velocity on
        // the entering edge only" shape every other multi-phase enemy in this codebase uses.
        phase = GetPhase(ref state);
        isStunnable = !stunResistant;
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
    // than requiring exactly 3, per explicit design) targets immediately at Moving->Pulling entry, not
    // deferred to each one's own individual windup — invulnerability needs to be live for the whole
    // sequence, and a targeted pleb shouldn't keep wandering while waiting for its own turn.
    private bool TryStartGrab(ref State state)
    {
        if (EnemySpawnerPD.instance == null) return false;

        // Defensive — see EnsureIndicatorPoolCapacity's own doc comment for why this call is here too, not
        // just in OnConfigureSpawn.
        EnsureIndicatorPoolCapacity();

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
        state.heldPlebGeneration.Clear();
        int grabCount = Mathf.Min(state.scaledMaxGrabCount, _grabCandidates.Count);
        for (int i = 0; i < grabCount; i++)
        {
            var chosen = _grabCandidates[i];
            state.heldPlebs.Add(chosen.ObjectId);
            state.heldPlebGeneration.Add(chosen.SpawnGeneration);
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
            // above, not deferred to BeginPull. Immediately superseded by RefreshHeldDamageOverrides
            // (2026-09-16, called every Simulate tick) — that continuously-self-correcting refresh is the
            // real source of truth for this pleb's own damage from here on; this first set just avoids a
            // one-tick gap before RefreshHeldDamageOverrides' own next run.
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
    // pleb was selected) — this fires the actual pull MOTION for exactly held pleb index i, called by
    // Pulling's own case handler the instant that pleb's windup window closes (see its own comment for the
    // full timing model). Order within the ring doesn't matter (per explicit design); the index just walks
    // heldPlebs in whatever order TryStartGrab already sorted them into (furthest-first).
    private void DispatchPull(ref State state, int i)
    {
        int count = state.heldPlebs.Count;
        // Dead-target/generation guard — a claimed pleb can still go away mid-sequence (e.g. EnemySpawnerPD's
        // pre-boss WipeAllEnemies doesn't check invulnerability), or this id can end up silently repointed at
        // an unrelated pooled-reuse/instance-ID-collision occupant (see heldPlebGeneration's own doc comment).
        // Left in heldPlebs/still counted toward SlotAngleDeg's spacing either way (removing it here would
        // mean re-spacing everyone else mid-sequence for a vanishingly rare case) — just skipped so it
        // doesn't error (or orbit the wrong enemy) trying to act on something that no longer resolves to the
        // original pleb.
        if (TryResolveHeldPleb(ref state, i, out var enemy) && !enemy._isDead)
            enemy.OrbitTarget(ObjectId, transform.position, EffectiveHoldOrbitRadius, pullSpeed, holdOrbitAngularSpeed, SlotAngleDeg(i, count));
    }

    // Direct-state gate (2026-08-31) for HandleActiveState's own Pulling-case release check — see
    // KingOrbiterPD.AllArrivedAtOrbitSlot's own identical doc comment for the full reasoning (queries each
    // pleb's own IsWaitingToOrbit directly instead of estimating from a parallel time formula; a dead/
    // unresolvable member is treated as non-blocking rather than stalling release forever).
    private bool AllArrivedAtOrbitSlot(ref State state)
    {
        for (int i = 0; i < state.heldPlebs.Count; i++)
        {
            if (!TryResolveHeldPleb(ref state, i, out var enemy) || enemy._isDead) continue;
            if (!enemy.IsWaitingToOrbit) return false;
        }
        return true;
    }

    // Reverses every effect DispatchPull/TryStartGrab applied, on whichever plebs are currently held regardless
    // of which phase this was called from (natural hold-expiry, death, or Interrupt()) — a held pleb's own
    // orbit fields self-clear via StopOrbiting() rather than needing this to know their exact sub-phase.
    private void ReleaseAllHeld(ref State state)
    {
        for (int i = 0; i < state.heldPlebs.Count; i++)
        {
            if (TryResolveHeldPleb(ref state, i, out var enemy))
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
        state.heldPlebGeneration.Clear();
    }

    // "Immediately release" requires this to happen in the SAME call as B.isDead flipping true, not deferred
    // to eventual corpse teardown (Destroyed() could fire many ticks later if any death-hold effect is
    // active) — see Die()'s own base doc comment for why deathHoldCount can delay that.
    //
    // Snapshotted into _pendingReclaimPlebs BEFORE releasing (2026-10-16) — unlike the generic hide/render
    // system, ReleaseAllHeld() is a real, cross-identity predicted-state mutation (StopOrbiting() on each
    // held pleb), not a view-layer action, and it had no revert path at all: a misprediction-then-correction
    // cycle (same confirmed pattern as every other death-hide flicker fixed this session) genuinely released
    // held Spikies, then nothing ever reclaimed them when the death turned out wrong, leaving them either
    // stuck released or caught in a rapid held/released flicker as Die() re-fired for the real kill shortly
    // after. See OnHiddenStateChanged's own doc comment for the reclaim half of this fix.
    protected override void Die()
    {
        base.Die();
        _pendingReclaimPlebs.Clear();
        _pendingReclaimPlebGeneration.Clear();
        for (int i = 0; i < currentState.heldPlebs.Count; i++)
        {
            _pendingReclaimPlebs.Add(currentState.heldPlebs[i]);
            _pendingReclaimPlebGeneration.Add(currentState.heldPlebGeneration[i]);
        }
        ReleaseAllHeld(ref currentState);
    }

    // Same release call as Die() above, for the difficulty-pacing wipe path (2026-10-16 fix) — see
    // EnemyBasePD.OnSilentDespawn's own doc comment. No _pendingReclaimPlebs snapshot here — that exists
    // purely to support OnHiddenStateChanged's misprediction-revert path, which a wipe (deterministic
    // difficulty pacing, not a speculative prediction) never undergoes — nothing to reclaim, only something
    // to release.
    protected override void OnSilentDespawn()
    {
        base.OnSilentDespawn();
        ReleaseAllHeld(ref currentState);
    }

    // Reclaims whichever snapshotted plebs are still valid once a misprediction-revert is confirmed (see
    // Die()'s own doc comment for why this snapshot exists). Only fires on the revert direction (hidden ==
    // false) — the release itself already happened instantly from Die(), unchanged. Re-runs the exact same
    // claim sequence TryStartGrab uses rather than trying to restore each pleb's exact prior sub-phase/angle
    // (not worth the complexity for what's normally a sub-0.2s window) — this means a reclaimed pleb
    // restarts from a fresh Pulling phase, a brief visible re-approach rather than a seamless resume, but
    // that's a minor cosmetic approximation next to the alternative (staying released, or flickering,
    // forever/repeatedly). A pleb that's died or already been claimed by someone else in the meantime is
    // simply skipped, not force-reclaimed.
    protected override void OnHiddenStateChanged(bool hidden)
    {
        base.OnHiddenStateChanged(hidden);
        if (hidden || _pendingReclaimPlebs.Count == 0) return;

        int snapshotCount = _pendingReclaimPlebs.Count;
        currentState.heldPlebs.Clear();
        currentState.heldPlebGeneration.Clear();
        int slot = 0;
        for (int i = 0; i < snapshotCount; i++)
        {
            var id = _pendingReclaimPlebs[i];
            var go = hierarchy.GetGameObject(id);
            if (go == null || !go.TryGetComponent(out IEnemyPD enemy) || enemy._isDead || enemy.IsBeingHeld) continue;
            // Verifies this id still resolves to the SAME pleb originally snapshotted, not a pooled-reuse/
            // instance-ID-collision victim now wearing it — see heldPlebGeneration's own doc comment.
            if (enemy.SpawnGeneration != _pendingReclaimPlebGeneration[i]) continue;

            currentState.heldPlebs.Add(id);
            currentState.heldPlebGeneration.Add(enemy.SpawnGeneration);
            enemy.SetKinematic(true);
            enemy.SetMovementDisabled(true);
            enemy.SetPhysicsColliderDisabled(true);
            enemy.SetHurtboxDisabled(true);
            enemy.SetDamageOverride(true, damage * pullDamageMultiplier);
            enemy.ClaimForOrbit(ObjectId);
            enemy.OrbitTarget(ObjectId, transform.position, EffectiveHoldOrbitRadius, pullSpeed, holdOrbitAngularSpeed, SlotAngleDeg(slot, snapshotCount));
            slot++;
        }
        _pendingReclaimPlebs.Clear();
        _pendingReclaimPlebGeneration.Clear();
    }

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above) and
    // meant to be cancelable — CanBeInterrupted now defaults to false (see EnemyBasePD's own doc comment).
    // Only Holding qualifies: it's the one phase actually meant to have a real "hit it with an interrupt to
    // stop it" counter-play window (per explicit design). Pulling (windup+pull, now one merged phase) is
    // stunnable (not committed) and isn't interruptible either, superseding the earlier "Interrupt() cancels
    // the grab at any phase" behavior.
    protected override bool CanBeInterrupted => GetPhase(ref currentState) == OrbiterState.Holding;

    // Drops everything held and returns to Moving — can now only ever fire during Holding (see
    // CanBeInterrupted above), so hurtboxDisabled is always already false by this point (set true all the
    // way back at Moving->Windup entry, reverted at the Pulling->Holding transition, long before Holding
    // itself begins) — no extra revert needed here. attackCooldownTimer is explicitly reset to a FULL
    // cooldown here (2026-08-19 fix) — it was already exhausted by the time Holding was ever reached (that's
    // what let TryStartGrab fire in the first place), so leaving it untouched meant Moving's own
    // attackCooldownTimer <= 0 check passed again the instant the paired interrupt-stun wore off, triggering
    // an immediate re-grab instead of actually waiting out a cooldown — same bug class (and same flat, not
    // randomized, value — matches this phase's own natural Holding-timeout transition just above) as the fix
    // already applied to Spear/Dagger/Blitz/Shooter/Leaper/Mortar's own OnInterrupted().
    protected override void OnInterrupted()
    {
        // Snapshotted before ReleaseAllHeld() clears heldPlebs — see the stun loop at the bottom of this
        // method for why it's needed after that call, not before.
        _interruptStunScratch.Clear();
        for (int i = 0; i < currentState.heldPlebs.Count; i++)
            _interruptStunScratch.Add(currentState.heldPlebs[i]);

        ReleaseAllHeld(ref currentState);
        currentState.phaseId             = (int)OrbiterState.Moving;
        currentState.phaseTimer          = 0f;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;

        // Fires AFTER the release above, per explicit design — every pleb that was just dropped gets
        // stunned by the ORBITER itself (not by whatever interrupted the Orbiter), silently: InterruptTrueStun
        // never pops its own "Stun" popup (see EnemyBasePD.Stun()'s own doc comment on that split), so
        // freeing up to maxGrabCount plebs at once doesn't flood the screen with redundant popups when the
        // one "Interrupted" popup already shown on the Orbiter itself already conveys the whole event.
        foreach (var id in _interruptStunScratch)
        {
            var go = hierarchy.GetGameObject(id);
            if (go != null && go.TryGetComponent(out IEnemyPD enemy) && !enemy._isDead)
                enemy.InterruptTrueStun();
        }
    }

    protected override void OnDeathStop()
    {
        if (_windupIndicators == null) return;
        foreach (var indicator in _windupIndicators)
            if (indicator != null) indicator.gameObject.SetActive(false);
        if (_windupFills == null) return;
        foreach (var fill in _windupFills)
            if (fill != null) fill.Hide();
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);
        if (_windupIndicators == null || !gameObject.activeInHierarchy) return;

        bool  pullingPhase = GetPhase(ref viewState) == OrbiterState.Pulling;
        int   heldCount    = viewState.heldPlebs.Count;
        for (int i = 0; i < _windupIndicators.Length; i++)
        {
            var indicator = _windupIndicators[i];
            if (indicator == null) continue;

            // Per-slot windup window (2026-08-17) THROUGH the actual pull-in movement (2026-09-14 fix, was
            // gated to localElapsed < windupDuration — hid this slot's indicator the instant DispatchPull
            // actually fired, before that pleb had even started moving toward its slot, let alone arrived;
            // per explicit design, this pleb's own indicator should stay up for its ENTIRE movement,
            // independent of every other slot — they can and do overlap). Shows from the moment this slot's
            // own windup window opens (localElapsed >= 0), only hiding once this SPECIFIC pleb has actually
            // arrived at its orbit slot (IsWaitingToOrbit) or become invalid (dead/unresolvable) — no upper
            // time bound anymore, since "how long the pull-in takes" is now speed-based (pullSpeed), not a
            // fixed duration this view code could estimate against (see pullSpeed's own doc comment).
            float localElapsed = viewState.phaseTimer - i * pullSequentialDelay;
            bool slotFilled = pullingPhase && i < heldCount && localElapsed >= 0f;

            // Near edge = the orbit slot itself (this Orbiter's position + the holdOrbitRadius/SlotAngleDeg
            // offset this pleb is being pulled TO), NOT this Orbiter's own center — far edge = the target
            // pleb's actual LIVE position (where it's being pulled FROM). The indicator is the pull path,
            // not a targeting line from the Orbiter to something.
            Vector3 spikyPos = default;
            if (slotFilled)
            {
                var go = hierarchy.GetGameObject(viewState.heldPlebs[i]);
                // IsPulling (2026-09-16, was !enemy.IsWaitingToOrbit — same bug/fix as
                // KingOrbiterPD.UpdatePullLineIndicators, see IEnemyPD.IsPulling's own doc comment) — a pleb
                // whose orbitPhase has moved PAST WaitingToOrbit into Orbiting used to read IsWaitingToOrbit as
                // false again, silently re-admitting it here. Generation-checked too (2026-10-16 fix) — see
                // heldPlebGeneration's own doc comment; without it this indicator could point at an unrelated
                // enemy's live position instead of correctly reading as empty.
                slotFilled = go != null && go.TryGetComponent(out IEnemyPD enemy) && !enemy._isDead
                    && enemy.SpawnGeneration == viewState.heldPlebGeneration[i] && enemy.IsPulling;
                if (slotFilled) spikyPos = go.transform.position;
            }

            var fill = _windupFills != null && i < _windupFills.Length ? _windupFills[i] : null;
            if (indicator.gameObject.activeSelf != slotFilled)
            {
                indicator.gameObject.SetActive(slotFilled);
                if (fill != null && !slotFilled) fill.Hide();
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

            // Positioned directly at the WORLD-space midpoint between the two edges above — every other line
            // indicator in this codebase only ever faces one direction (the owning enemy's own body facing),
            // but these 3 indicators each independently rotate to face a DIFFERENT live target every frame,
            // so this sets rotation explicitly here (LookRotation(aim)) rather than relying on any inherited
            // facing. ROTATION MUST BE SET BEFORE SetProgress below — WindupFillPD's own outline still reads
            // this object's CURRENT transform.forward/.right for its corner math (see RecomputeOutline's own
            // doc comment on why the outline can't avoid a genuine world-space read the way the fill mesh's
            // own position now does), so setting it after would read last frame's stale facing instead. This
            // object is also explicitly, synchronously self-positioned every frame with no separate smoother
            // involved, so — unlike Blitz/Spear/etc.'s passively-inherited-rotation case — it was never
            // actually susceptible to the jitter ShowRectangle's own doc comment describes.
            indicator.SetPositionAndRotation(slotPos + aim * (distance * 0.5f), Quaternion.LookRotation(aim));

            // Near edge = the orbit slot itself, far edge = the target pleb's live position — length is the
            // distance already computed above; width left at -1 (authored default), matching the old code's
            // own behavior (only ever set scale.z, never touched X). centerForwardOffset left at its 0
            // default — indicator's own position (just set above) already IS the true center.
            if (fill != null)
            {
                fill.ShowRectangle(distance);
                // Live, every frame, off this slot's own localElapsed — see
                // feedback_windupfill_self_timed_drift: a self-timed StartFill() runs on its own
                // Time.deltaTime clock, independent of the tick-based phaseTimer that actually gates
                // DispatchPull(), and can drift out of sync with it even though windupDuration itself is
                // fixed (never attack-speed-scaled) — a fixed duration doesn't guarantee the two clocks stay
                // aligned.
                fill.SetProgress(Mathf.Clamp01(localElapsed / windupDuration));
            }
        }
    }

    // _pendingReclaimPlebs is a plain field, not State — survives pooled reuse otherwise, which would let a
    // fresh life's own OnHiddenStateChanged(false) try to reclaim stale ObjectIds left over from a PREVIOUS
    // life's own (correctly-confirmed, never-reverted) death. Cleared explicitly here so a reused instance
    // always starts with an empty snapshot.
    protected override void OnRespawnReset()
    {
        base.OnRespawnReset();
        _pendingReclaimPlebs.Clear();
        _pendingReclaimPlebGeneration.Clear();
    }
}
