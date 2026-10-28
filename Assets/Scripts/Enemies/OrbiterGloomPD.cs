using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Moves toward players like a base Spiky (its own HandleActiveState only adds the orb sequence on top of
// the inherited default). On cooldown, spawns its own OrbiterOrbPD hazard orbs (2026-10-20 rework — replaces
// the old "grab up to 3 nearby Pleb-tier enemies and orbit them" mechanic entirely, see
// project_orbiter_drop_spiky_grab_rework_planned memory for the full why) in a ring around itself, lets them
// rise/spawn in for spawnDuration (the new "windup," interruptible), then has them actually orbit and deal
// contact damage for orbitDuration before despawning. Each orb is fully self-owned (its own
// PredictedIdentity, see OrbiterOrbPD's own class doc comment) — this class only orchestrates WHEN each
// phase transitions and pushes stun-freeze/despawn to its own currently-active orbs; the orbs themselves own
// their per-tick position/visual/damage logic.
public class OrbiterGloomPD : EnemyBasePD<OrbiterGloomPD.State>
{
    public enum OrbiterState { Moving, Spawning, Orbiting }

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public int   phaseId;
        // Counts UP while Spawning (elapsed since the spawn/windup began, against spawnDuration), counts
        // DOWN while Orbiting (remaining time, against orbitDuration) — same dual-purpose shape
        // KingSpikePD's own remoteSequenceTimer already uses.
        public float phaseTimer;
        public float attackCooldownTimer;
        // Every orb currently spawned for THIS attack cycle — no generation-check parallel list needed here
        // (unlike the old heldPlebs/heldPlebGeneration pair), since these are fully owned spawned objects
        // this class alone ever creates/destroys, not externally-pooled independent enemies some OTHER
        // system could silently reassign out from under it.
        public DisposableList<PredictedObjectID> activeOrbs;
        // Runtime-scaled — see EnemyBaseState.scaledMaxHealth's own doc comment for why these live in State
        // rather than as a plain field mutated by OnConfigureSpawn.
        public int   scaledOrbCount;
        public float scaledAttackCooldown;

        public void Dispose()
        {
            b.contactCooldowns.Dispose();
            b.killLaunchHitIds.Dispose();
            activeOrbs.Dispose();
        }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            copy.activeOrbs = activeOrbs.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    [Header("Orbs")]
    [SerializeField] private OrbiterOrbPD orbPrefab;
    // The new "windup" — orbs rise in place over this duration before actually orbiting. Interruptible (see
    // CanBeInterrupted below) — explicit 1s default per design.
    [SerializeField] private float spawnDuration = 1f;
    [SerializeField] private float orbitDuration = 3f;
    [SerializeField] private float orbitRadius = 2f;
    // Scales with size (an Elite is just a bigger prefab) — same "* size" precedent SpikeShieldPD/
    // SpearGloomPD already use. Read live everywhere orbitRadius would otherwise be used directly (spawn
    // slot positions, orbit radius) so orbs can never drift out of sync with this Orbiter's own current size.
    private float EffectiveOrbitRadius => orbitRadius * size;
    [SerializeField] private float orbitAngularSpeed = 120f;
    // Each orb's own damage, locked in once at spawn (same convention EnemyProjectilePD.Launch's own damage
    // param already uses — not live-re-read from this Orbiter afterward even if, say, Enrage changes its
    // damage mid-cycle).
    [SerializeField] private float orbitDamageMultiplier = 0.5f;
    // Single source of truth for how many orbs one cycle spawns — Tier1-authored, pristine read-only
    // baseline forever (OnConfigureSpawn scales it into State.scaledOrbCount instead of mutating this in
    // place, see EnemyBaseState.scaledMaxHealth's own doc comment for the full reasoning).
    [SerializeField] private int orbCount = 2;

    [Header("Attack Cooldown")]
    [SerializeField] private float attackCooldown = 5f;

    [Header("Tier Scaling")]
    [SerializeField] private int orbCountTierGrowth = 1;

    private OrbiterState GetPhase(ref State s) => (OrbiterState)s.phaseId;

    protected override void LateAwake()
    {
        base.LateAwake();

        // Guaranteed at least one full cooldown before the first spawn attempt — same gap this already
        // closed for the old grab attack (2026-08-12 fix history), re-runs on every pooled reuse since
        // LateAwake() itself does.
        currentState.attackCooldownTimer = attackCooldown;
    }

    // Tier1-authored orbCount, scaled by orbCountTierGrowth per tier above 1.
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        currentState.scaledOrbCount = orbCount + (spawnTier - 1) * orbCountTierGrowth;

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment). Re-set
        // the placeholder attackCooldownTimer LateAwake() already assigned above, using the now-scaled value.
        currentState.scaledAttackCooldown = attackCooldown * attackCooldownMultiplier;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
    }

    protected override State GetInitialState()
    {
        var s = base.GetInitialState();
        s.scaledOrbCount = orbCount;
        s.scaledAttackCooldown = attackCooldown;
        s.activeOrbs = DisposableList<PredictedObjectID>.Create(orbCount);
        return s;
    }

    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        var phase = GetPhase(ref state);
        // Only ticks while actually waiting to spawn again — NOT mid-Spawning/Orbiting, same "cooldown gates
        // re-entry into Moving, not overlapped with the attack sequence itself" convention the old grab
        // attack already used.
        if (phase == OrbiterState.Moving)
            state.attackCooldownTimer -= delta;

        switch (phase)
        {
            case OrbiterState.Moving:
                if (_target != null) HandleMovement(ref state, delta);
                if (state.attackCooldownTimer <= 0f)
                {
                    SpawnOrbs(ref state);
                    state.phaseId    = (int)OrbiterState.Spawning;
                    state.phaseTimer = 0f;
                }
                break;

            // Stationary through the whole Spawning sequence (no HandleMovement call), matching every other
            // windup+strike enemy's own kinematic-through-the-sequence convention.
            case OrbiterState.Spawning:
            {
                // Scaled by EffectiveAttackSpeed, same convention the old Pulling phase's own phaseTimer
                // already used.
                state.phaseTimer += delta * EffectiveAttackSpeed;
                if (state.phaseTimer >= spawnDuration)
                {
                    BeginOrbitingAll(ref state);
                    state.phaseId    = (int)OrbiterState.Orbiting;
                    state.phaseTimer = orbitDuration;
                }
                break;
            }

            case OrbiterState.Orbiting:
                if (_target != null) HandleMovement(ref state, delta);
                state.phaseTimer -= delta;
                // Pushed every tick, not edge-triggered — same "never a one-shot transition, re-assert
                // regardless" reasoning the old RefreshHeldDamageOverrides/SetOrbitFrozen pair already
                // established, now trimmed to just the one flag orbs actually need pushed to them.
                PushStunnedToAllOrbs(ref state, IsTrueStunned && isStunnable);
                if (state.phaseTimer <= 0f)
                {
                    DespawnAllOrbs(ref state);
                    state.phaseId             = (int)OrbiterState.Moving;
                    state.attackCooldownTimer = state.scaledAttackCooldown;
                }
                break;
        }

        // Stunnable in Moving/Orbiting, deliberately NOT in Spawning (2026-10-20, explicit design — the old
        // Pulling phase WAS stunnable; this is a real behavior change) — a true stun's timer still ticks
        // down normally and this Orbiter's own body still shows the yellow tint regardless (IsTrueStunned
        // reads trueStunTimer > 0 unconditionally, no isStunnable gate on the tint-paint logic itself — see
        // EnemyBasePD.IsTrueStunned's own doc comment), it just can't freeze/pause the spawn countdown.
        phase = GetPhase(ref state);
        isStunnable = phase != OrbiterState.Spawning && !stunResistant;
        if (_rigidbody != null)
        {
            bool shouldBeKinematic = phase == OrbiterState.Spawning;
            if (shouldBeKinematic && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = shouldBeKinematic;
        }
    }

    // Slot 0 = this Orbiter's current facing direction (transform.forward, projected to XZ) — no base-level
    // "facing" field exists anywhere in this codebase to reuse (confirmed), every enemy just reads
    // transform.forward/transform.rotation directly, same as this does. Remaining slots fan out evenly
    // (360/count increments) — no GameRandom call, fixed points per explicit design.
    private float SlotAngleDeg(int index, int count)
    {
        Vector3 fwd = transform.forward;
        float baseAngle = Mathf.Atan2(fwd.z, fwd.x) * Mathf.Rad2Deg;
        return baseAngle + index * (360f / count);
    }

    private void SpawnOrbs(ref State state)
    {
        state.activeOrbs.Clear();
        if (orbPrefab == null) return;

        int count = state.scaledOrbCount;
        Vector3 spawnPos = transform.position;
        float damage = GetOwnDamage() * orbitDamageMultiplier;
        for (int i = 0; i < count; i++)
        {
            var orbId = hierarchy.Create(orbPrefab.gameObject, spawnPos, Quaternion.identity);
            if (!orbId.HasValue) continue;
            var go = hierarchy.GetGameObject(orbId.Value);
            if (go == null || !go.TryGetComponent(out OrbiterOrbPD orb)) continue;

            go.transform.localScale = Vector3.one * size;
            orb.Launch(ObjectId, SlotAngleDeg(i, count), EffectiveOrbitRadius, orbitAngularSpeed,
                spawnDuration, damage, TierAccentMaterial);
            state.activeOrbs.Add(orbId.Value);
        }
    }

    private void BeginOrbitingAll(ref State state)
    {
        for (int i = 0; i < state.activeOrbs.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.activeOrbs[i]);
            if (go != null && go.TryGetComponent(out OrbiterOrbPD orb))
                orb.BeginOrbiting();
        }
    }

    private void PushStunnedToAllOrbs(ref State state, bool stunned)
    {
        for (int i = 0; i < state.activeOrbs.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.activeOrbs[i]);
            if (go != null && go.TryGetComponent(out OrbiterOrbPD orb))
                orb.SetStunned(stunned);
        }
    }

    private void DespawnAllOrbs(ref State state)
    {
        for (int i = 0; i < state.activeOrbs.Count; i++)
        {
            var go = hierarchy.GetGameObject(state.activeOrbs[i]);
            if (go != null && go.TryGetComponent(out OrbiterOrbPD orb))
                orb.Despawn();
        }
        state.activeOrbs.Clear();
    }

    // Exactly the states that are both !isStunnable (see isStunnable's own per-tick assignment above) and
    // meant to be cancelable — CanBeInterrupted defaults to false. Spawning is now the interruptible phase
    // (2026-10-20, moved from the old Holding/now-Orbiting) — Orbiting is explicitly NOT interruptible
    // anymore, per explicit design.
    protected override bool CanBeInterrupted => GetPhase(ref currentState) == OrbiterState.Spawning;

    // Drops every currently-spawning orb and returns to Moving — can now only ever fire during Spawning (see
    // CanBeInterrupted above), so hurtboxDisabled is always already true at this point (set at Moving->
    // Spawning entry, only reverted at the Spawning->Orbiting transition, which never got here) — revert it.
    // attackCooldownTimer resets to a FULL cooldown (not left at its already-exhausted value), same bug-class
    // fix every other enemy's own OnInterrupted() already applies.
    protected override void OnInterrupted()
    {
        DespawnAllOrbs(ref currentState);
        SetHurtboxDisabled(false);
        currentState.phaseId             = (int)OrbiterState.Moving;
        currentState.phaseTimer          = 0f;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
    }

    // Die()/OnSilentDespawn() are both Simulate()-context (confirmed — Die() already safely mutates
    // currentState directly elsewhere in this codebase, SilentDespawn() is called from EnemySpawnerPD's own
    // Simulate()-context WipeAllEnemies()), safe to push Despawn() to each active orb's own predicted State
    // from here. Deliberately NOT done from OnDeathStop() — that's called from inside UpdateView() (view-
    // layer, confirmed), mutating another identity's predicted State from there would be exactly the
    // "mutate predicted state from view-layer code" bug class already fixed elsewhere this session.
    protected override void Die()
    {
        base.Die();
        DespawnAllOrbs(ref currentState);
    }

    protected override void OnSilentDespawn()
    {
        base.OnSilentDespawn();
        DespawnAllOrbs(ref currentState);
    }
}
