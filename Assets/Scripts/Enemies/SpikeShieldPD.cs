using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// The actual "enemy" here — ShielderGloomPD's body is nearly featureless (see its own class doc comment);
// almost everything Shielder Gloom does lives on this orbiting sub-object instead. Deliberately NOT
// IEnemyPD/EnemyBasePD — no health, no death, no XP ("similar to a player's weapon, it's just a tool the
// enemy uses" — this is what makes it safe for the body to keep walking through the Shield's whole
// windup/release sequence unlike SpearGloomPD's own body, which freezes for its own attack).
//
// Structured after WeaponBasePD's own equipped-weapon orbit — every player weapon is already a separate
// top-level PredictedIdentity (never Unity-parented under the player) that computes its own world position
// every tick straight from the owner's live transform.position (see WeaponBasePD.OrbitalPosition()) — this
// mirrors that exact, already-proven-smooth mechanism rather than inventing a new one. Position AND facing
// are always the same computed direction (orbits at a fixed radius, always facing straight outward along
// that same radius), so there's only ever one angle to track per phase.
//
// Owns two fully independent damage sources: a continuous, always-on contact-damage trigger ("same shape as
// our other enemies for contact damage" — a direct copy of EnemyBasePD's own ContactCooldown/OnContactTrigger
// mechanism, since this class can't literally inherit it) and a single-target windup->release attack that
// only ever considers the Shielder's own current target, never a broad sweep of every nearby player.
[RequireComponent(typeof(PredictedRigidbody))]
public class SpikeShieldPD : PredictedIdentity<SpikeShieldPD.State>
{
    public enum ShieldPhase { FreeRotating, Windup, Releasing }

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        // Live free-rotation facing angle (degrees) — only advances during FreeRotating, wrapped 0-360
        // every change. CompressedFloat, not a plain float, since this changes virtually every tick while
        // free-rotating — a plain float would pay PurrNet's full 33-bit "changed" cost every single tick
        // instead of CompressedFloat's quantized/compressed one (same reasoning as CharacterStatsPD's own
        // weaponOrbitAngle).
        public CompressedFloat facingAngle;
        // Captured the instant Windup actually ends and the burst fires (2026-08-22 — used to be captured
        // at Windup ENTRY instead, freezing the angle for the whole windup; Windup now spins live, tracking
        // _windupTrackedTarget the same way FreeRotating tracks _owner.Target, see LateSimulate's own
        // comment) — Releasing reads this so it holds the exact angle the burst just fired at, instead of
        // continuing to follow facingAngle after the fact. Plain float, not CompressedFloat: changes once
        // per attack, so it already gets the cheap "unchanged" case for free without needing the wrapped-int
        // machinery.
        public float lockedAngle;
        public int phaseId;
        public float phaseTimer;
        // Ticks down unconditionally every tick, any phase — gates the FreeRotating->Windup transition.
        // Not its own separate "Cooldown" phase, mirroring SpearGloomPD.spearAttackCooldownTimer's own
        // always-ticking design.
        public float attackCooldownTimer;
        // The Shield's own independent copy of the per-player contact-damage cooldown list — same
        // ContactCooldown struct EnemyBasePD already defines (a public top-level type, reused directly, not
        // duplicated) for its own contact damage, just tracked separately here since this class isn't an
        // EnemyBasePD subclass and can't inherit its copy.
        public DisposableList<ContactCooldown> contactCooldowns;
        // Runtime-scaled, pushed in by the owning ShielderGloomPD (Configure() for the baseline,
        // SetRotationRateAndAttackRange/SetAttackCooldown for the tier-scaled correction moments later) —
        // see EnemyBaseState.scaledMaxHealth's own doc comment for why these need to live in State rather
        // than a plain field (2026-09-22 follow-up pass): a machine that never itself ran the Shielder's own
        // OnConfigureSpawn for this pair (a PredictedHierarchy-reconciled Shield) would otherwise keep this
        // Shield's rate/range/cooldown stuck at the tier-1 baseline forever.
        public float scaledRotationRateDegPerSecond;
        public float scaledAttackRange;
        public float scaledAttackCooldown;

        public void Dispose() => contactCooldowns.Dispose();

        public State Duplicate()
        {
            var copy = this;
            copy.contactCooldowns = contactCooldowns.Duplicate();
            return copy;
        }
    }

    [Header("Visuals")]
    [SerializeField] private Transform _windupIndicator;
    [SerializeField] private WindupFillPD _windupFill;
    // Whichever renderers should tint to this life's tier accent color (2026-08-21 — was a hardcoded
    // Color.red; now the same color permanentReadyMaterialRenderers below get pushed as an actual material,
    // and the same color the owning Shielder's own _accentRenderers show — see SetReadyMaterial's own doc
    // comment) the instant this Shield is actually able to trigger Shieldburst again (reverting to their own
    // original authored color otherwise — on cooldown or mid-attack; the renderer's own authored material is
    // already black in the Editor, no black-specific code needed here), and yellow while a true stun is
    // propagated from the owning Shielder's body (see LateSimulate()'s own doc comment) — EXACT same
    // "instance .material once, tint .color, revert to a cached original" mechanism EnemyBasePD's own hit-
    // flash/stun-tint system already uses for its own `renderers`/`_originalColors` (this class just can't
    // literally inherit that code, being deliberately not IEnemyPD/EnemyBasePD — see class doc comment).
    [SerializeField] private Renderer[] readyMaterialRenderers;
    // Separate from readyMaterialRenderers above — these grab whatever SetReadyMaterial() last pushed and
    // never revert, for decorative pieces (e.g. the spike clusters) that should just permanently match the
    // ready color rather than toggling with attack/stun state. Untouched by the tint system above — a
    // plain material swap, not a per-instance tint, since there's nothing here to ever revert to.
    [SerializeField] private Renderer[] permanentReadyMaterialRenderers;

    private Color[] _originalColors;
    private Material _shieldReadyMaterial;

    private enum ShieldTintState { Original, Ready, Stunned }
    // Plain view-layer bookkeeping, not State — edge-detects the toggle so this only writes colors on an
    // actual state transition, not every frame.
    private ShieldTintState _appliedTintState = ShieldTintState.Original;

    private PredictedRigidbody _rigidbody;
    private Collider _contactDamageCollider;
    // Every renderer on this object, not just readyMaterialRenderers/permanentReadyMaterialRenderers (those
    // two are tint-purpose subsets, not guaranteed to cover every mesh) — the set SetHidden() below actually
    // needs to hide/show everything when the owning ShielderGloomPD's own body hides/reveals.
    private Renderer[] _allRenderers;

    // Plain, not State — this Shield is a 1:1, whole-life companion to exactly one ShielderGloomPD instance,
    // same "spawned effect stores its owner as a plain reference, not a re-resolved PredictedObjectID"
    // convention this codebase already uses for effects that never outlive or get reassigned away from their
    // owner (see AreaEffectPD/PhantomBladeSummonHelper's own precedent).
    private ShielderGloomPD _owner;

    // The SPECIFIC player this Windup committed to, captured once at the FreeRotating->Windup transition —
    // same "commit once, keep reading that same reference" contract BlitzGloomPD._windupTrackedTarget/
    // KingSpikePD._chargeWindupTrackedTarget use (immune to _owner.TargetStats getting reassigned to someone
    // else mid-windup via taunt/invisible-retarget). Only ever read while actually in Windup.
    private IFriendlyPD _windupTrackedTarget;
    // Companion to _windupTrackedTarget — its position, recorded every tick it's actually alive+visible,
    // frozen otherwise (dead or invisible). Once a Windup starts, nothing stops it (2026-08-22, was:
    // skip dealing damage entirely if the target became invalid) — the burst still fires at wherever this
    // was last legitimately seen, exactly the same principle as KingOrbiterPD's own shot tracking.
    private Vector3 _windupTrackedLastKnownPosition;

    // Every tunable is pushed in once via Configure() — the Shielder owns all of these (see its own class
    // doc comment), this class just holds the copy it was configured with.
    private float _orbitRadius;
    // Applied ONLY during the Windup phase's own MoveTowardsAngle call below — see
    // ShielderGloomPD.windupRotationRateMultiplier's own doc comment for the full reasoning. Not tier-scaled
    // (unlike rotation rate itself) — set once here via Configure() and never touched again.
    private float _windupRotationRateMultiplier;
    // Full width, not a half-width — see InAttackRectangle's own comment for the halving done at the
    // actual check.
    private float _attackWidth;
    private float _windupDuration;
    private float _releaseDuration;
    // Inherited from the Shielder's own EnemyBasePD.size — see Configure()'s own comment.
    private float _size = 1f;
    // Multipliers, not flat amounts — actual damage is _owner.GetDamage() * these, read LIVE at the moment
    // each hit resolves (not cached here at Configure()-time), same reasoning ExeAxeThrowProjectilePD's own
    // ProcessHit() already uses for Strength: reflects whatever the Shielder's own damage stat currently
    // is (including EnemySpawnerPD.ConfigureSpawn's own damageMultiplier scaling, which runs AFTER this
    // Shield's own Configure() call, so caching a value here would miss it).
    private float _shieldburstDamageMultiplier;
    private float _shieldContactDamageMultiplier;
    private float _contactCooldownSeconds;

    // Cached at the top of Simulate() (before the physics pass runs), same reasoning as EnemyBasePD's own
    // _currentDelta — OnShieldContactTrigger fires from the physics callback, which happens between
    // Simulate() and LateSimulate() (confirmed tick order: Simulate() on every identity, then physics
    // integrates, then LateSimulate() on every identity), so it needs a fresh delta already cached before
    // that pass runs, not whatever LateSimulate() would only get around to setting afterward.
    private float _currentDelta;

    private void Awake()
    {
        _rigidbody = GetComponent<PredictedRigidbody>();
        // Always kinematic, set once, never toggled — this object's own position is entirely scripted every
        // tick (orbit math), never physics-driven, unlike e.g. SpearGloomPD's body which only goes kinematic
        // during its own Windup/Stabbing.
        if (_rigidbody != null) _rigidbody.isKinematic = true;

        // No physics collider on this object (removed 2026-08-12 — it was pushing players around in buggy
        // ways); positioned close enough to the owning ShielderGloomPD body that a player can't stand
        // between them without already being in the ContactDamageCollider below. Only the child-name lookup
        // convention EnemyBasePD.Awake() uses for its own ContactDamageCollider carries over here.
        var contactChild = transform.Find("ContactDamageCollider");
        if (contactChild != null) _contactDamageCollider = contactChild.GetComponent<Collider>();

        _allRenderers = GetComponentsInChildren<Renderer>(true);

        // Captured exactly once per real GameObject instance (Awake(), not LateAwake()/Configure(), neither
        // of which are guaranteed to run before any pooled reuse might already have swapped these to a
        // tinted color) — same "cache the true prefab-authored value before anything can touch it" reasoning
        // WeaponRangedProjectilePD's own _baseScale/EnemyBasePD's own _originalColors already use. .material
        // (not sharedMaterial) auto-instances a per-renderer copy the FIRST time it's touched, so tinting
        // here never mutates the shared material asset every other Shield instance also uses — same
        // established convention as EnemyBasePD's own hit-flash/stun-tint system.
        if (readyMaterialRenderers != null)
        {
            _originalColors = new Color[readyMaterialRenderers.Length];
            for (int i = 0; i < readyMaterialRenderers.Length; i++)
                if (readyMaterialRenderers[i] != null && readyMaterialRenderers[i].material != null)
                    _originalColors[i] = readyMaterialRenderers[i].material.color;
        }
    }

    protected override void LateAwake()
    {
        // Same wiring EnemyBasePD.LateAwake() uses for its own ContactDamageCollider — generic
        // PurrNet component, not EnemyBasePD-specific, so this class can attach to it directly.
        if (_contactDamageCollider != null && _contactDamageCollider.TryGetComponent(out PredictedPhysicsCallbacks callbacks))
        {
            callbacks.onTriggerEnter += OnShieldContactTrigger;
            callbacks.onTriggerStay += OnShieldContactTrigger;
        }
    }

    // contactCooldowns must be a real, Create()'d DisposableList from the moment this identity first exists
    // (a default DisposableList<T> has no valid backing list) — GetInitialState() only ever runs on true
    // first creation, never on pooled reuse, so Configure()'s own contactCooldowns.Clear() below is what
    // keeps a REUSED instance's stale entries from a previous life from leaking into a new one.
    protected override State GetInitialState() => new()
    {
        contactCooldowns = DisposableList<ContactCooldown>.Create(4),
    };

    // Called once, synchronously, by ShielderGloomPD.LateAwake() immediately after hierarchy.Create()
    // returns — same "mutate currentState right after Create()" pattern as RemoteSpikeBurstPD.Launch().
    // No longer takes the ready material (2026-08-20) — see SetReadyMaterial's own doc comment for why that
    // has to be pushed separately, later, instead of here.
    public void Configure(ShielderGloomPD owner, float orbitRadius, float rotationRateDegPerSecond,
        float windupRotationRateMultiplier, float attackRange, float attackWidth, float windupDuration,
        float releaseDuration, float attackCooldown, float shieldburstDamageMultiplier,
        float shieldContactDamageMultiplier, float contactCooldownSeconds)
    {
        _owner = owner;
        _orbitRadius = orbitRadius;
        // Baseline only — ShielderGloomPD.OnConfigureSpawn() (runs moments later, same tick, on whichever
        // machine actually made the spawn decision) pushes the real tier-scaled values via
        // SetRotationRateAndAttackRange()/SetAttackCooldown() below, into State rather than a plain field
        // here — see State.scaledRotationRateDegPerSecond's own doc comment for why.
        currentState.scaledRotationRateDegPerSecond = rotationRateDegPerSecond;
        currentState.scaledAttackRange = attackRange;
        currentState.scaledAttackCooldown = attackCooldown;
        _windupRotationRateMultiplier = windupRotationRateMultiplier;
        _attackWidth = attackWidth;
        _windupDuration = windupDuration;
        _releaseDuration = releaseDuration;
        _shieldburstDamageMultiplier = shieldburstDamageMultiplier;
        _shieldContactDamageMultiplier = shieldContactDamageMultiplier;
        _contactCooldownSeconds = contactCooldownSeconds;

        // Inherits the Shielder's own generic per-enemy size scalar (EnemyBasePD.size) directly — an Elite
        // variant is just a bigger Shielder prefab (size tuned up in the Editor, same as any other enemy),
        // and this is what makes the Shield visually/physically scale up right along with it, uniformly,
        // same "transform.localScale = Vector3.one * size" convention CharacterStatsPD.AddSizePercent
        // already uses. Cached (not re-read every tick) since it's a fixed per-prefab Inspector value, not
        // something that changes mid-life — but still re-applied here on every Configure() call (not just
        // once in Awake()) since a pooled Shield instance could in principle be reused by a
        // differently-sized Shielder across lives.
        _size = owner.size;
        transform.localScale = Vector3.one * _size;
        // Same 1:1 treatment as attackRange/_attackWidth (see InAttackRectangle's own comment) — a bigger
        // Shield model orbiting at an unchanged radius would clip closer into the body as it scales up, so
        // baking size into the radius here avoids needing to hand-retune orbitRadius per Elite variant too.
        _orbitRadius *= _size;

        float initialAngle = 0f;
        if (owner.Target != null)
        {
            Vector3 toTarget = owner.Target.position - owner.transform.position;
            initialAngle = Mathf.Atan2(toTarget.z, toTarget.x) * Mathf.Rad2Deg;
        }
        currentState.facingAngle = initialAngle;
        currentState.lockedAngle = initialAngle;
        currentState.phaseId = (int)ShieldPhase.FreeRotating;
        currentState.phaseTimer = 0f;
        // Guaranteed at least one full cooldown before the first attack (2026-08-12 fix — this used to be
        // GameRandom.instance.NextFloat(0f, attackCooldown) alone, which could roll near-zero and attack
        // almost immediately).
        currentState.attackCooldownTimer = attackCooldown;
        // Reset, not left alone — this instance may be a pooled reuse carrying a previous life's stale
        // per-player cooldown entries (see GetInitialState()'s own comment).
        currentState.contactCooldowns.Clear();
        // Same pooled-reuse hygiene as every other _windupTrackedTarget-style field in this codebase.
        _windupTrackedTarget = null;
        _windupTrackedLastKnownPosition = default;

        if (_windupFill != null) _windupFill.Hide();
        if (_windupIndicator != null) _windupIndicator.gameObject.SetActive(false);

        // Force back to original materials immediately — avoids a one-frame stale "ready"/"stunned" flash on
        // a pooled Shield whose PREVIOUS life ended mid-state (UpdateView's own edge-detect would
        // self-correct next frame regardless, this just skips that visible frame).
        ApplyRendererTint(ShieldTintState.Original);

        // Snap the transform immediately to the freshly-configured orbit position instead of waiting a tick
        // for LateSimulate() to first run — avoids a one-frame pop/flash at the old (pre-reuse) position for
        // a pooled Shield reactivating with a brand new owner.
        WriteTransform(owner.transform.position, initialAngle);
    }

    private void WriteTransform(Vector3 origin, float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        // Same parametric form as WeaponBasePD.OrbitalPosition() (x=cos, z=sin) — kept consistent so this
        // angle convention never drifts from the rest of the codebase's own orbit math.
        Vector3 dir = new(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
        transform.SetPositionAndRotation(origin + dir * _orbitRadius, Quaternion.LookRotation(dir));
    }

    protected override void Simulate(ref State state, float delta)
    {
        // See _currentDelta's own doc comment for why this is cached here (before the physics pass) rather
        // than in LateSimulate(), even though all the actual gameplay logic below lives there.
        _currentDelta = delta;
    }

    // All real per-tick logic runs here, not Simulate() — same convention WeaponMeleePD/WeaponRangedPD/
    // RemoteSpikeBurstPD already use for exactly this reason: LateSimulate() runs only after EVERY
    // identity's own Simulate() (plus the physics pass) has already completed for this tick, so reading
    // _owner.transform.position here is always guaranteed fresh for this tick regardless of which identity
    // was created/registered first.
    protected override void LateSimulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (_owner == null) return;

        Vector3 origin = _owner.transform.position;

        var phase = (ShieldPhase)state.phaseId;
        // Only ticks while actually FreeRotating (2026-08-19 — uniform across every multi-phase enemy's own
        // attack-cadence cooldown now, see OrbiterGloomPD's own fix) — frozen through the whole
        // Windup/Releasing sequence rather than ticking (and going increasingly negative) throughout it.
        if (phase == ShieldPhase.FreeRotating)
            state.attackCooldownTimer -= delta;

        // Same decrement/remove shape as EnemyBasePD.Simulate()'s own contactCooldowns loop.
        for (int i = state.contactCooldowns.Count - 1; i >= 0; i--)
        {
            var entry = state.contactCooldowns[i];
            entry.remaining--;
            if (entry.remaining <= 0)
                state.contactCooldowns.RemoveAt(i);
            else
                state.contactCooldowns[i] = entry;
        }

        // True-stun propagation from the owning Shielder's own body (owner.IsTrueStunned — a live read,
        // same "cross-identity Simulate()-context read" convention _owner.Target already relies on; safe
        // because LateSimulate() is deterministic and runs identically on every machine, and by this point
        // the owner's own Simulate() has already updated it for this same tick). Only true stuns propagate
        // (owner.Stun() sets this; a knockback-derived micro-stun never does — see IEnemyPD.Stun/
        // AddKnockbackVelocity's own ApplyStun(isTrueStun:...) split), only knocking the OWNER back doesn't
        // freeze the Shield at all.
        //
        // Windup AND Releasing are both excluded (2026-08-22 — used to be just Windup) — the whole committed
        // attack sequence, trigger through recovery, is this Shield's own "attacking state" (matches
        // CanBeInterrupted's IsWindingUp gate) — per explicit design a mere stun NEVER stops/pauses ANY part
        // of it (rotation keeps tracking live through both phases, contact collider stays enabled), only a
        // real Interrupt() (ShielderGloomPD.OnInterrupted -> CancelWindup()) can cut it short — which resets
        // phaseId straight back to FreeRotating, so THAT'S what makes the Shield read as stunned afterward
        // (falls right out of this same check, no separate handling needed for the "interrupted mid-attack"
        // case). Only FreeRotating (genuinely idle/searching) ever actually freezes on a stun.
        bool stunFrozen = _owner.IsTrueStunned && phase == ShieldPhase.FreeRotating;
        // ownerDead added (2026-10-15) — this Shield has no death/isDead concept of its own (class doc
        // comment), so without this it kept dealing contact damage from a Shielder body that's already
        // predicted-dead and hidden, right up until the much-later verified-isDead deletion in
        // Destroyed() actually ran. Reading _owner._isDead here is deterministic/safe the same way
        // _owner.IsTrueStunned one line above already is — this runs in Simulate-context, reading another
        // identity's replicated State field, not a view-layer/per-machine value (see
        // feedback_no_race_conditions_or_outside_simulation_state).
        bool ownerDead = _owner._isDead;
        bool wantDisabled = stunFrozen || ownerDead;
        if (_contactDamageCollider != null && _contactDamageCollider.enabled == wantDisabled)
            _contactDamageCollider.enabled = !wantDisabled;

        float effectiveAngle;

        switch (phase)
        {
            case ShieldPhase.FreeRotating:
            {
                // Frozen: skip both rotation-tracking and the attack-trigger check for this tick — holds
                // whatever facingAngle it last had, and can't START a new windup while stunned (matches
                // EnemyBasePD's own isStunnable-gated freeze blocking a new attack from beginning, even
                // though — per the class-level design here — an ALREADY-committed one never gets paused).
                if (!stunFrozen)
                {
                    Transform target = _owner.Target;
                    if (target != null)
                    {
                        Vector3 toTarget = target.position - origin;
                        toTarget.y = 0f;
                        if (toTarget != Vector3.zero)
                        {
                            float targetAngle = Mathf.Atan2(toTarget.z, toTarget.x) * Mathf.Rad2Deg;
                            state.facingAngle = WrapAngle(Mathf.MoveTowardsAngle(state.facingAngle, targetAngle, state.scaledRotationRateDegPerSecond * _owner.EffectiveAttackSpeed * delta));
                        }
                    }

                    if (state.attackCooldownTimer <= 0f && IsTargetValid(_owner.TargetStats))
                    {
                        Vector3 forward = AngleToDir(state.facingAngle);
                        if (InAttackRectangle(origin, forward, _owner.TargetStats.transform.position, state.scaledAttackRange))
                        {
                            // Commits to this SPECIFIC target for the whole Windup — see
                            // _windupTrackedTarget's own doc comment for why this is a frozen reference, not
                            // a live re-read of _owner.TargetStats (which could get reassigned mid-windup).
                            _windupTrackedTarget = _owner.TargetStats;
                            _windupTrackedLastKnownPosition = _owner.TargetStats.transform.position;
                            state.phaseId = (int)ShieldPhase.Windup;
                            state.phaseTimer = _windupDuration;
                        }
                    }
                }

                effectiveAngle = state.facingAngle;
                break;
            }

            case ShieldPhase.Windup:
            {
                // Scaled by EffectiveAttackSpeed (2026-08-22) — a Slow now correctly stretches the windup
                // out, not just the rotation tracking below and the cooldown between attacks. The fill
                // progress UpdateView shows (viewState.phaseTimer / _windupDuration) stays correct
                // unscaled — it's a ratio of however much of the original countdown range is left, which
                // means the same thing regardless of how fast phaseTimer itself is actually ticking down.
                state.phaseTimer -= delta * _owner.EffectiveAttackSpeed;

                // Spins/tracks live during Windup now (2026-08-22 — used to lock facingAngle instantly at
                // Windup entry instead) — same rate-capped MoveTowardsAngle shape as FreeRotating above,
                // just against _windupTrackedTarget (the specific player this windup committed to) instead
                // of the Shielder's own live _owner.Target. Freezes (no-op) the instant that target dies or
                // goes invisible-without-taunting — same "once a windup starts, nothing stops it" principle
                // BlitzGloomPD.TrackWindupTarget/KingSpikePD.TrackChargeWindupTarget already use: the burst
                // still fires at wherever facingAngle was last legitimately aimed, it just stops sweeping
                // further. _windupTrackedLastKnownPosition mirrors the same freeze for the strike's own
                // position check below.
                if (_windupTrackedTarget != null && !_windupTrackedTarget.IsDead &&
                    (!_windupTrackedTarget.IsInvisible || _windupTrackedTarget.IsTaunting))
                {
                    _windupTrackedLastKnownPosition = _windupTrackedTarget.transform.position;
                    Vector3 toTarget = _windupTrackedLastKnownPosition - origin;
                    toTarget.y = 0f;
                    if (toTarget != Vector3.zero)
                    {
                        float targetAngle = Mathf.Atan2(toTarget.z, toTarget.x) * Mathf.Rad2Deg;
                        // Rate halved (windupRotationRateMultiplier) only for this Windup-phase tracking —
                        // see ShielderGloomPD.windupRotationRateMultiplier's own doc comment. FreeRotating
                        // above and Releasing below both use the full, unscaled rate.
                        state.facingAngle = WrapAngle(Mathf.MoveTowardsAngle(state.facingAngle, targetAngle, state.scaledRotationRateDegPerSecond * _windupRotationRateMultiplier * _owner.EffectiveAttackSpeed * delta));
                    }
                }

                if (state.phaseTimer <= 0f)
                {
                    // Locks in the angle the burst actually fires at, for Releasing to hold afterward — see
                    // State.lockedAngle's own doc comment.
                    state.lockedAngle = state.facingAngle;

                    // No longer skips damage outright if the target's since died/gone invisible (2026-08-22)
                    // — fires at _windupTrackedLastKnownPosition regardless, same "once started, nothing
                    // stops it" principle as the live-tracking above. _windupTrackedTarget itself (not
                    // _owner.TargetStats) is who actually takes the damage — this Windup committed to THEM
                    // specifically, not whoever the Shielder happens to be targeting by the time it resolves.
                    if (_windupTrackedTarget != null)
                    {
                        Vector3 forward = AngleToDir(state.lockedAngle);
                        if (InAttackRectangle(origin, forward, _windupTrackedLastKnownPosition, state.scaledAttackRange))
                            _windupTrackedTarget.TakeDamage(_owner.GetDamage() * _shieldburstDamageMultiplier, false, _owner.ObjectId);
                    }
                    state.phaseId = (int)ShieldPhase.Releasing;
                    state.phaseTimer = _releaseDuration;
                }
                // Live during Windup now, not a frozen lockedAngle — see this case's own comment above.
                effectiveAngle = state.facingAngle;
                break;
            }

            default: // Releasing
            {
                state.phaseTimer -= delta;

                // Live now too (2026-08-22 — used to hold state.lockedAngle motionless through the whole
                // recovery window). Tracks the Shielder's own live _owner.Target, not the frozen
                // _windupTrackedTarget the just-fired attack committed to — the attack already resolved, so
                // there's no more reason to stay locked onto whichever specific player it fired at. Never
                // stunFrozen-gated, same "part of the attacking state" reasoning as Windup above.
                Transform releaseTarget = _owner.Target;
                if (releaseTarget != null)
                {
                    Vector3 toTarget = releaseTarget.position - origin;
                    toTarget.y = 0f;
                    if (toTarget != Vector3.zero)
                    {
                        float targetAngle = Mathf.Atan2(toTarget.z, toTarget.x) * Mathf.Rad2Deg;
                        state.facingAngle = WrapAngle(Mathf.MoveTowardsAngle(state.facingAngle, targetAngle, state.scaledRotationRateDegPerSecond * _owner.EffectiveAttackSpeed * delta));
                    }
                }

                if (state.phaseTimer <= 0f)
                {
                    state.phaseId = (int)ShieldPhase.FreeRotating;
                    state.attackCooldownTimer = state.scaledAttackCooldown;
                }
                effectiveAngle = state.facingAngle;
                break;
            }
        }

        WriteTransform(origin, effectiveAngle);
    }

    // Lets ShielderGloomPD (a totally separate class hierarchy — this class isn't IEnemyPD) know whether it
    // should currently be interruptible — see its own CanBeInterrupted override. Read directly off
    // currentState, same as every other cross-identity live-state read in this codebase.
    public bool IsWindingUp => (ShieldPhase)currentState.phaseId == ShieldPhase.Windup;

    // Called by ShielderGloomPD.OnInterrupted() — cancels this Shield's own windup outright (no damage, no
    // Releasing follow-through), same "reset straight back to idle" shape every other enemy's own
    // OnInterrupted() uses. No-op if not currently winding up (defensive — CanBeInterrupted already gates
    // Interrupt() itself on IsWindingUp, so this should never actually be called otherwise).
    public void CancelWindup()
    {
        if ((ShieldPhase)currentState.phaseId != ShieldPhase.Windup) return;

        currentState.phaseId = (int)ShieldPhase.FreeRotating;
        currentState.attackCooldownTimer = currentState.scaledAttackCooldown;
        _windupTrackedTarget = null;
        _windupTrackedLastKnownPosition = default;

        // Predicted-instant hide, not left for UpdateView's own phaseId-driven redraw to eventually catch up
        // — this is invoked out-of-band from a DIFFERENT identity's own Simulate() (ShielderGloomPD's
        // Interrupt() call), same reasoning EnemyBasePD.Interrupt()'s own doc comment gives for why every
        // other enemy's own OnInterrupted() hides its windup indicator explicitly instead of waiting.
        if (_windupIndicator != null) _windupIndicator.gameObject.SetActive(false);
        if (_windupFill != null) _windupFill.Hide();
    }

    private static float WrapAngle(float angle) => ((angle % 360f) + 360f) % 360f;

    private static Vector3 AngleToDir(float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
    }

    private static bool IsTargetValid(IFriendlyPD stats) =>
        stats != null && !stats.IsDead && (!stats.IsInvisible || stats.IsTaunting) && !stats.IsInvincible;

    // Same forward-rectangle math SpearGloomPD.EnterStab() uses, scoped to exactly one candidate instead of
    // sweeping CharacterStatsPD.All — the user was explicit that this should only ever consider the
    // Shielder's own current target, never any nearby player.
    private bool InAttackRectangle(Vector3 origin, Vector3 forward, Vector3 targetPosition, float attackRange)
    {
        // Both terms scaled 1:1 by _size (the Shielder's own inherited size, see Configure()'s own
        // comment) — per explicit design, the real hitbox itself grows with an Elite Shielder, not just the
        // model. This also makes the indicator's own inherited-via-parent-scale growth (see UpdateView(),
        // no counter-scaling needed there) accurate again, since both now scale the same way.
        Vector3 offset = targetPosition - origin;
        offset.y = 0f;
        float forwardDist = Vector3.Dot(offset, forward);
        if (forwardDist < 0f || forwardDist > attackRange * _size) return false;

        // _attackWidth is a full width (see its own doc comment) — lateralDist is a one-sided distance from
        // the centerline, so the actual check needs half of it, same as SpearGloomPD's own
        // `stabWidth * 0.5f * size`.
        float lateralDist = (offset - forward * forwardDist).magnitude;
        return lateralDist <= _attackWidth * 0.5f * _size;
    }

    // Direct adaptation of EnemyBasePD.OnContactTrigger's body — a deliberately separate, always-on "anyone
    // touching me" mechanism, fully independent of the single-target windup/release attack above. No
    // isDead/isStunnable gating (none of that exists for this class). isPassiveContact: true, always
    // (2026-08-24 fix, was incorrectly false) — this IS the ambient body-touch case EnemyBasePD.
    // ContactDamageIsPassive's own doc comment describes as the default-true, non-attack case: brushing past
    // the shield as it orbits isn't a telegraphed attack, so it shouldn't register Dodge (confirmed live —
    // dashing past a Shielder was triggering Dodge just from grazing the orbiting shield). The Shield's own
    // REAL attack is the separate windup->release rectangle sweep above (TakeDamage at line ~368, still
    // correctly dodge-eligible via TakeDamage's own default isPassiveContact — that one's untouched).
    private void OnShieldContactTrigger(GameObject other)
    {
        // IFriendlyPD (2026-08-30, was CharacterStatsPD + the !stats.owner.HasValue guard) — mirrors
        // EnemyBasePD.OnContactTrigger's own identical 2026-08-30 fix; see that method's own doc comment for
        // why the old guard is dropped entirely rather than replaced.
        var friendly = other.GetComponent<IFriendlyPD>();
        if (friendly == null || friendly.IsDead) return;

        var target = friendly.ObjectId;
        var cooldowns = currentState.contactCooldowns;
        int ticks = Mathf.Max(1, Mathf.RoundToInt(_contactCooldownSeconds / _currentDelta));

        for (int i = 0; i < cooldowns.Count; i++)
        {
            if (!cooldowns[i].target.Equals(target)) continue;
            if (cooldowns[i].remaining > 0) return;
            cooldowns[i] = new ContactCooldown { target = target, remaining = ticks };
            friendly.TakeDamage(_owner.GetDamage() * _shieldContactDamageMultiplier, false, _owner.ObjectId, _owner.SpawnCategory == EnemySpawnCategory.Pleb, true);
            return;
        }

        cooldowns.Add(new ContactCooldown { target = target, remaining = ticks });
        friendly.TakeDamage(_owner.GetDamage() * _shieldContactDamageMultiplier, false, _owner.ObjectId, _owner.SpawnCategory == EnemySpawnCategory.Pleb, true);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        var phase = (ShieldPhase)viewState.phaseId;
        bool showIndicator = phase == ShieldPhase.Windup;

        if (_windupIndicator != null)
        {
            // _attackWidth is already a full world-space width — ShowRectangle applies the same
            // 0.1-world-units-per-scale-unit conversion internally that ApplyLineTransform used to apply to
            // length only, now uniformly to both (see WindupFillPD.ApplyFill's own doc comment). No
            // counter-scaling against this Shield's own size-driven root scale needed here — the actual
            // hit-check (InAttackRectangle) scales attackRange/attackWidth by _size too, so passing those
            // same already-size-scaled values through is exactly correct, not a double-count.
            // _windupIndicator's own current position IS the near edge — centerForwardOffset shifts the true
            // center half the distance forward from there, computed PURELY in WindupFillPD's own local space
            // (see ShowRectangle's own doc comment) rather than a caller-computed world point.
            if (_windupFill != null)
                _windupFill.ShowRectangle(viewState.scaledAttackRange, _attackWidth, viewState.scaledAttackRange * 0.5f);

            if (_windupIndicator.gameObject.activeSelf != showIndicator)
                _windupIndicator.gameObject.SetActive(showIndicator);
        }

        if (_windupFill != null)
        {
            if (showIndicator)
                _windupFill.SetProgress(1f - Mathf.Clamp01(viewState.phaseTimer / _windupDuration));
            else
                _windupFill.Hide();
        }

        // Instant/predicted (viewState, not verified) same as every other purely-cosmetic view sync in this
        // codebase. Shows the ready color for ready-to-trigger, Windup, AND Releasing alike (per explicit
        // design) — only reverts to original while genuinely on cooldown (FreeRotating with
        // attackCooldownTimer still counting down). Framed as the on-cooldown check specifically (not an
        // "isReady" check covering all three other cases) since attackCooldownTimer keeps ticking
        // (increasingly negative) unchanged all through Windup/Releasing too and only resets to a positive
        // value once Releasing ends — a naive "> 0f means not ready" read would have to explicitly exclude
        // Windup/Releasing anyway, so checking the ONE case that should revert is simpler than checking the
        // three that shouldn't.
        bool onCooldown = phase == ShieldPhase.FreeRotating && viewState.attackCooldownTimer > 0f;
        bool showReady = !onCooldown;

        // Stunned tint takes priority over ready, and EXCLUDES both Windup AND Releasing (2026-08-22 — used
        // to be just Windup) — matches LateSimulate()'s own stunFrozen exactly (per explicit design: a stun
        // never pauses/interrupts any part of the attacking state, and the tint doesn't show for any of it
        // either, unlike a normal enemy's own always-unconditional trueStunTimer tint). owner.IsTrueStunned
        // is a live read — see LateSimulate()'s own doc comment for why that's safe/consistent here.
        bool isStunned = _owner != null && _owner.IsTrueStunned && phase == ShieldPhase.FreeRotating;
        ShieldTintState wantState = isStunned ? ShieldTintState.Stunned
            : showReady ? ShieldTintState.Ready
            : ShieldTintState.Original;
        if (wantState != _appliedTintState)
            ApplyRendererTint(wantState);
    }

    // Called by ShielderGloomPD.OnConfigureSpawn() (2026-08-20), not Configure() — Configure() itself runs
    // from the Shielder's own LateAwake(), which fires BEFORE ConfigureSpawn() (and therefore before
    // EnemyBasePD.spawnTier is finalized for this life — see EnemyBasePD.TierAccentMaterial's own doc
    // comment), so a tier-derived material can't be resolved correctly that early. Re-applies immediately —
    // matters on a pooled reuse, where permanentReadyMaterialRenderers would otherwise still show whichever
    // tier's material this exact Shield instance happened to be showing at the end of its PREVIOUS life.
    public void SetReadyMaterial(Material material)
    {
        _shieldReadyMaterial = material;
        ApplyPermanentReadyMaterial();
    }

    // Called by ShielderGloomPD.OnHiddenStateChanged() — toggles every renderer on this Shield in lockstep
    // with the owning body's own predicted hide/verified-revert (2026-10-15 fix: a body-only hide left the
    // Shield, a separate PredictedIdentity the base hide system can't see, fully visible on a "dead"
    // Shielder). View-layer only, renderers alone — NOT the windup indicator (Die()->CancelWindup() already
    // hides that deterministically the instant the Shielder actually dies, in Simulate-context) and NOT the
    // contact-damage collider (LateSimulate()'s own _owner._isDead check handles that safely in
    // Simulate-context instead — toggling a Simulate-driven collider from here, a view-layer call, would
    // just get fought/overwritten the very next deterministic tick, same class of bug as
    // feedback_onawake_kinematic_lock_insufficient).
    public void SetHidden(bool hidden)
    {
        if (_allRenderers == null) return;
        foreach (var r in _allRenderers)
            if (r != null) r.enabled = !hidden;
    }

    // Called by ShielderGloomPD.OnConfigureSpawn() (2026-08-20), same reasoning/timing as SetReadyMaterial
    // above — Configure() already ran (from LateAwake(), before spawnTier was finalized) with the UN-scaled
    // baseline, so the actual tier-scaled values get pushed here afterward instead. _size is NOT reapplied
    // to attackRange here — InAttackRectangle already multiplies _attackRange * _size live at check-time
    // (see its own comment), so baking size in here would double-apply it.
    public void SetRotationRateAndAttackRange(float rotationRateDegPerSecond, float attackRange)
    {
        currentState.scaledRotationRateDegPerSecond = rotationRateDegPerSecond;
        currentState.scaledAttackRange = attackRange;
    }

    // Called by ShielderGloomPD.OnConfigureSpawn() (Threat Level 5), same reasoning/timing as
    // SetRotationRateAndAttackRange above — Configure() already ran (from LateAwake(), before
    // attackCooldownMultiplier was applied) with the UN-scaled baseline, so the scaled value gets pushed
    // here afterward instead. Re-rolls the pending attackCooldownTimer with the same formula Configure()
    // used, same "set again once the real scaling lands" reasoning as every other enemy's OnConfigureSpawn.
    public void SetAttackCooldown(float attackCooldown)
    {
        currentState.scaledAttackCooldown = attackCooldown;
        currentState.attackCooldownTimer = attackCooldown;
    }

    private void ApplyPermanentReadyMaterial()
    {
        if (permanentReadyMaterialRenderers == null || _shieldReadyMaterial == null) return;
        foreach (var r in permanentReadyMaterialRenderers)
            if (r != null) r.sharedMaterial = _shieldReadyMaterial;
    }

    // Priority: Stunned (yellow) > Ready (tier accent color) > Original — see UpdateView's own doc comment
    // for why. Same per-renderer .material.color tint EnemyBasePD's own hit-flash/stun-tint system uses.
    // Ready reads _shieldReadyMaterial.color (2026-08-21 — was a hardcoded Color.red) so this tint always
    // matches whatever SetReadyMaterial last pushed (this life's TierAccentMaterial), same color
    // permanentReadyMaterialRenderers show as an actual material and _accentRenderers show on the Shielder's
    // own body — falls back to Color.red only in the narrow window before SetReadyMaterial has ever been
    // called (shouldn't be visibly reachable — see SetReadyMaterial's own doc comment for why it's always set
    // before this Shield could actually reach the Ready state).
    private void ApplyRendererTint(ShieldTintState wantState)
    {
        _appliedTintState = wantState;
        if (readyMaterialRenderers == null || _originalColors == null) return;

        for (int i = 0; i < readyMaterialRenderers.Length; i++)
        {
            if (readyMaterialRenderers[i] == null || readyMaterialRenderers[i].material == null) continue;
            readyMaterialRenderers[i].material.color = wantState switch
            {
                ShieldTintState.Stunned => Color.yellow,
                ShieldTintState.Ready   => _shieldReadyMaterial != null ? _shieldReadyMaterial.color : Color.red,
                _                       => _originalColors[i],
            };
        }
    }
}
