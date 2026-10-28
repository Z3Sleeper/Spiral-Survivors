using PurrNet.Prediction;
using UnityEngine;

// Self-owned hazard orb spawned by OrbiterGloomPD (2026-10-20 rework) — replaces the old
// grab-a-live-Spike-pleb-and-orbit-it mechanic entirely. Mirrors EnemyProjectilePD's own shape (a short-lived,
// pooled PredictedIdentity set up once via Launch()) rather than inventing a new spawn/pool pattern.
//
// Position is self-driven — this orb reads its OWNER Orbiter's live position itself every tick (via
// hierarchy.GetGameObject(owner), same shape the OLD held-pleb orbit system used — IEnemyPD.transform is
// safely interface-exposed for this). Stun-freeze and despawn are PUSH-driven instead, deliberately NOT
// self-read: IEnemyPD never actually exposes IsTrueStunned/isStunnable/a "wiped" check (confirmed — only
// _isDead is on the interface), and the codebase's own established pattern for exactly this ("tell a held/
// owned object about the owner's stun state") is IEnemyPD.SetOrbitFrozen — the OWNER computes
// IsTrueStunned && isStunnable itself (it has direct field access, no interface needed) and pushes the
// result via a method call. SetStunned()/Despawn() below mirror that shape. Despawn is called from
// OrbiterGloomPD's Die()/OnSilentDespawn() overrides specifically (both confirmed Simulate()-context, safe
// to mutate this orb's predicted State from) — never from OnDeathStop(), which is called from inside
// UpdateView() and would be exactly the "mutate predicted state from view-layer code" bug class already
// fixed elsewhere this session.
public class OrbiterOrbPD : PredictedIdentity<OrbiterOrbPD.State>
{
    public struct State : IPredictedData<State>
    {
        public PredictedObjectID owner;
        public float orbitAngleDeg;
        public float orbitRadius;
        public float orbitAngularSpeed;
        // Counts up from 0 to spawnDuration while rising — see UpdateView's own yAxisMovers-Y-lerp use. Flight
        // parameters (orbitRadius/orbitAngularSpeed/spawnDuration/damage), same as EnemyProjectilePD's own
        // speed/maxRange/damage, live in State rather than a plain field for the identical reason: a client
        // reconciling this object via PredictedHierarchy rather than its own local Launch() call would
        // otherwise see these stuck at their C# default (0) forever.
        public float spawnElapsed;
        public float spawnDuration;
        public float damage;
        // True once the spawn (windup) phase has finished and this orb has actually started orbiting — gates
        // BOTH the orbit-angle advance AND the contact-damage collider in the same instant (explicit design:
        // deals no damage at all while rising, see Launch/BeginOrbiting's own comments).
        public bool  isOrbiting;
        // True once BeginFlight() has been called (2026-10-20, KingOrbiterPD rework) — this orb has left
        // orbit/spawn-slot motion entirely and is now flying in a straight line (Shoot-phase ring orbs, or a
        // Persistent Volley orb that skipped orbiting altogether). Takes priority over isOrbiting in
        // LateSimulate — once flying, the owner's own position is never read again.
        public bool  isFlying;
        public Vector3 flightDirection;
        public float flightSpeed;
        public float flightMaxDistance;
        public float flightTraveled;
        // True while a genuine owner true-stun is active (pushed via SetStunned()) — halts orbit-angle
        // advance, shows the yellow tint, AND blocks contact damage. Deliberately separate from
        // knockbackFrozen below (2026-10-21 split — a KingOrbiterPD Knockback windup also needs to halt
        // motion, but should neither tint yellow nor stop dealing damage, since it's the Orbiter's own attack
        // sequence pausing them on purpose, not an externally-imposed CC effect).
        public bool  stunned;
        // True while KingOrbiterPD's Knockback windup is holding every ring in place (pushed via
        // SetKnockbackFrozen()) — halts orbit-angle advance ONLY, no tint, no damage change. See `stunned`
        // above for the split reasoning.
        public bool  knockbackFrozen;
        // Pushed by Launch() — true for a Persistent Volley orb (KingOrbiterPD), which should sit at the
        // fixed world point it spawned at for its whole pre-flight life, never re-tracking the owner's own
        // live position the way an orbiting ring orb does.
        public bool  fixedPosition;
        // Single value, not a per-target dictionary like EnemyBaseState.contactCooldowns — an orb only ever
        // needs to avoid re-hitting the SAME player on consecutive ticks, not track multiple targets
        // independently the way a long-lived enemy's own body does.
        public float contactCooldownRemaining;
        public bool  despawned;
        public void Dispose() { }
    }

    [SerializeField] private float contactCooldownDuration = 0.5f;
    // Local-space radius only, scaled live by transform.lossyScale.x at query time below — NOT a Collider
    // (2026-10-20, deliberate design correction after review): this codebase's own "Combat hitbox
    // convention" is ALL attacks use Physics.Overlap*/BoxCast/SphereCast against explicit queries run from
    // Simulate()/LateSimulate(), specifically because a Unity trigger CALLBACK fires off Unity's own
    // internal physics step timing, not guaranteed to line up with PurrDiction's own resimulation passes the
    // way an explicit, synchronous query does. The one confirmed exception, EnemyBasePD.OnContactTrigger, is
    // tied to an enemy's own pre-existing physical body collider (there for movement collision regardless);
    // this orb has no such justification for a purpose-built damage collider, so it uses the dominant
    // convention instead, not the exception.
    [SerializeField] private float damageRadius = 0.5f;
    // Deliberately a dedicated "Y-Axis Movers" child, one level BELOW "Visuals" (2026-10-20, live-confirmed
    // fix) — PredictedTransform apparently doesn't play nice with something else directly driving the
    // "Visuals" transform's own local position (root cause not fully diagnosed, just confirmed empirically:
    // moving Visuals itself silently didn't animate, moving a plain child one level under it works). Model
    // meshes live under THIS, not under Visuals directly.
    [SerializeField] private Transform yAxisMovers;
    // Rise height — local Y yAxisMovers lerps between over the spawn window. Not multiplied by size here —
    // the orb ROOT's own transform is expected to be scaled by the owner's size (same convention AreaEffectPD's
    // own spawned effects already use), so this local position already scales for free via ordinary
    // parent-child transform composition.
    [SerializeField] private float riseHeightLow = -0.5f;
    [SerializeField] private float riseHeightHigh = 0.5f;
    // Tier-colored — TierAccentMaterial pushed here once at Launch(), same "accent vs other" split
    // EnemyBasePD._accentRenderers/_otherRenderers already use (see their own doc comment on EnemyBasePD).
    [SerializeField] private Renderer[] accentRenderers;
    // Hidden in lockstep with accentRenderers on despawn, never recolored.
    [SerializeField] private Renderer[] otherRenderers;

    // Plain view-layer bookkeeping, not predicted state — same instant-predicted-hide pattern as
    // EnemyProjectilePD's own _hiddenOnExpire.
    private bool _hiddenOnDespawn;
    // Cached right after SetRenderersMaterial applies the tier material in Launch() (2026-10-20 fix) — the
    // yellow stun tint's own revert MUST restore THIS, never a hardcoded Color.white: for an unlit/flat-
    // colored material (what a tier accent material actually is), .color IS the whole visible color, not a
    // multiplicative tint over a texture, so writing white there doesn't mean "no tint," it means "render
    // solid white," silently erasing the real tier color every single frame this ran. Same reasoning
    // EnemyBasePD._originalColors already exists to prevent for its own flash/stun tint — this orb just
    // needed the same fix, re-derived instead of reused since this is a different renderer array.
    private Color[] _originalAccentColors;

    protected override State GetInitialState() => default;

    // Called immediately after hierarchy.Create() returns, same "mutate currentState right after Create()"
    // pattern as EnemyProjectilePD.Launch(). slotAngleDeg is an ABSOLUTE world angle (already includes the
    // Orbiter's own facing-direction offset — see OrbiterGloomPD's own slot-angle math) — this orb holds
    // still at that angle for the whole spawn/rise window, only starting to actually orbit once
    // BeginOrbiting() is called.
    public void Launch(PredictedObjectID owner, float slotAngleDeg, float orbitRadius, float orbitAngularSpeed,
        float spawnDuration, float damage, Material tierAccentMaterial, bool fixedPosition = false)
    {
        currentState.owner = owner;
        currentState.orbitAngleDeg = slotAngleDeg;
        currentState.orbitRadius = orbitRadius;
        currentState.orbitAngularSpeed = orbitAngularSpeed;
        currentState.spawnElapsed = 0f;
        currentState.spawnDuration = Mathf.Max(0.0001f, spawnDuration);
        currentState.damage = damage;
        currentState.isOrbiting = false;
        currentState.stunned = false;
        currentState.knockbackFrozen = false;
        currentState.fixedPosition = fixedPosition;
        currentState.contactCooldownRemaining = 0f;
        currentState.despawned = false;

        if (tierAccentMaterial != null) SetRenderersMaterial(accentRenderers, tierAccentMaterial);
        CacheOriginalAccentColors();

        // A pooled instance's previous life ends hidden — a fresh launch needs to be visible again.
        _hiddenOnDespawn = false;
        SetRenderersEnabled(accentRenderers, true);
        SetRenderersEnabled(otherRenderers, true);
    }

    // Called once by OrbiterGloomPD the instant its own Spawning phase ends — flips this orb from "holding
    // still at its slot, rising, harmless" into "actually orbiting, able to deal damage." One-shot, not a
    // per-tick push. Deals no damage at all before this — the overlap query in LateSimulate below is itself
    // gated on state.isOrbiting.
    public void BeginOrbiting() => currentState.isOrbiting = true;

    // Called once by KingOrbiterPD (2026-10-20) — either after BeginOrbiting() (a Shoot-phase ring orb
    // leaving orbit to fly at its ring's shared target) or directly from the end of Spawning, skipping
    // orbiting entirely (a Persistent Volley orb, launched with orbit radius/speed 0). direction/speed are
    // baked in by the caller at call time (same "snapshot, not live" convention the old IEnemyPD.LaunchAlong
    // primitive this replaces already used — see KingOrbiterPD.FireShotRing's own doc comment), so nothing
    // here needs to re-read EffectiveAttackSpeed. From this point on this orb moves independently of its
    // owner's position — see LateSimulate's own flying branch.
    // damage is re-specified here rather than reusing whatever Launch() baked in — KingOrbiterPD's ring orbs
    // deal orbitDamageMultiplier-scaled damage while orbiting but shootingDamageMultiplier-scaled damage once
    // fired (two genuinely different, independently-tuned values on that boss), so the flight damage needs
    // its own explicit value at the moment flight actually begins.
    public void BeginFlight(Vector3 direction, float maxDistance, float speed, float damage)
    {
        currentState.isFlying = true;
        currentState.flightDirection = direction.normalized;
        currentState.flightSpeed = speed;
        currentState.flightMaxDistance = maxDistance;
        currentState.flightTraveled = 0f;
        currentState.damage = damage;
    }

    // Lets an owning boss know whether this orb has already finished (predicted-side) — used by KingOrbiterPD
    // to gate ring release on real per-orb completion instead of a IEnemyPD.IsLaunching-style interface poll
    // (orbs aren't IEnemyPD). True the instant LateSimulate sets state.despawned, same tick as a flight/orbit
    // completion — does not wait for view-layer verification, matching every other direct-state gate already
    // used elsewhere.
    public bool IsDespawned => currentState.despawned;

    // Pushed every tick by the owner while Orbiting — see State.stunned's own doc comment for the split from
    // SetKnockbackFrozen below, and this class's own doc comment for why this is push-driven rather than
    // self-read off the owner's IsTrueStunned/isStunnable. Safe/cheap to call unconditionally every tick
    // (same "never a one-shot transition, re-assert regardless" reasoning EnemyBaseState's own equivalent
    // flag doc comment already establishes).
    public void SetStunned(bool stunned) => currentState.stunned = stunned;

    // Pushed by KingOrbiterPD for the whole duration of its own Knockback windup — halts orbit motion only,
    // no tint, no damage change (see State.knockbackFrozen's own doc comment).
    public void SetKnockbackFrozen(bool frozen) => currentState.knockbackFrozen = frozen;

    // Called by OrbiterGloomPD from Die()/OnSilentDespawn() (death/wipe) or a natural orbit-duration expiry —
    // all three Simulate()-context, safe to mutate this orb's own predicted State from directly.
    public void Despawn() => currentState.despawned = true;

    protected override void Simulate(ref State state, float delta) { }

    // Post-physics-pass, same reasoning as EnemyProjectilePD's own LateSimulate split — this orb needs the
    // owner's already-moved-this-tick position, not last tick's.
    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.despawned) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        var ownerGo = hierarchy.GetGameObject(state.owner);
        // _isDead is safely interface-exposed (unlike wiped/stun) — a cheap bonus backstop on top of the
        // owner's own explicit Despawn() push, not the primary mechanism (see class doc comment).
        if (ownerGo == null || !ownerGo.TryGetComponent(out IEnemyPD owner) || owner._isDead)
        {
            state.despawned = true;
            return;
        }

        if (state.isFlying)
        {
            // Flying (KingOrbiterPD's Shoot phase / Persistent Volley) — moves independently of the owner's
            // position from here on, in a straight line at a speed already baked in by BeginFlight(). Keeps
            // running the same damage query the orbit branch below uses (same single contactCooldownRemaining
            // gate) so a flying orb can tag the same target again once the cooldown clears, matching the old
            // LaunchAlong-launched pleb's own "flies through, damages whoever it touches the whole way"
            // behavior — it does NOT stop/despawn on first hit, only once it's traveled its full range.
            transform.position += state.flightDirection * state.flightSpeed * delta;
            state.flightTraveled += state.flightSpeed * delta;

            if (state.contactCooldownRemaining > 0f)
                state.contactCooldownRemaining -= delta;

            if (state.contactCooldownRemaining <= 0f)
            {
                int flightHitCount = Physics.OverlapSphereNonAlloc(transform.position,
                    damageRadius * transform.lossyScale.x, _overlapResults, CombatLayerMasks.FriendlyHurtboxMask);
                bool flightHitAnyone = false;
                for (int i = 0; i < flightHitCount; i++)
                {
                    var target = _overlapResults[i].GetComponent<IFriendlyPD>();
                    if (target == null || target.IsDead) continue;
                    target.TakeDamage(state.damage, false, state.owner);
                    flightHitAnyone = true;
                }
                if (flightHitAnyone)
                    state.contactCooldownRemaining = contactCooldownDuration;
            }

            if (state.flightTraveled >= state.flightMaxDistance)
                state.despawned = true;
            return;
        }

        if (!state.isOrbiting)
        {
            // Rising/spawning — holds its slot angle, no orbit advance yet. spawnElapsed/visual-rise progress
            // is read directly in UpdateView off this same State, nothing else to do here. Scaled by the
            // owner's own EffectiveAttackSpeed to stay in lockstep with OrbiterGloomPD's identically-scaled
            // phaseTimer (the thing that actually decides when Spawning ends) — otherwise a non-1x attack
            // speed desyncs the two clocks and this orb's own rise freezes at whatever fraction it reached.
            state.spawnElapsed = Mathf.Min(state.spawnDuration, state.spawnElapsed + delta * owner.EffectiveAttackSpeed);
            // Fixed-position orbs (KingOrbiterPD's Persistent Volley) hold exactly where they were spawned —
            // never re-anchoring to the owner's own live position, unlike an orbiting ring orb.
            if (!state.fixedPosition)
                transform.position = owner.transform.position + SlotDirection(state.orbitAngleDeg) * state.orbitRadius;
            return;
        }

        // Orbit-angle advance halts for EITHER reason (a genuine stun or a Knockback windup) — see
        // State.stunned/knockbackFrozen's own doc comments for why damage/tint below only ever key off
        // `stunned` specifically.
        if (!state.stunned && !state.knockbackFrozen)
            state.orbitAngleDeg = WrapAngle(state.orbitAngleDeg + state.orbitAngularSpeed * delta * owner.EffectiveAttackSpeed);

        transform.position = owner.transform.position + SlotDirection(state.orbitAngleDeg) * state.orbitRadius;

        if (state.contactCooldownRemaining > 0f)
            state.contactCooldownRemaining -= delta;

        // Deals no damage while rising (state.isOrbiting false, handled by the early-return above) or while
        // genuinely STUNNED — a Knockback-frozen orb (knockbackFrozen true, stunned false) keeps dealing
        // damage the whole time, by explicit design (it's the Orbiter's own attack sequence pausing it, not
        // an externally-imposed CC effect that should make it harmless). Explicit Physics.OverlapSphereNonAlloc
        // query, not a trigger collider — see damageRadius's own field comment for the full "Combat hitbox
        // convention" reasoning. Radius scaled live by this orb's own current world scale.
        if (state.stunned || state.contactCooldownRemaining > 0f) return;

        int hitCount = Physics.OverlapSphereNonAlloc(transform.position, damageRadius * transform.lossyScale.x,
            _overlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        bool hitAnyone = false;
        for (int i = 0; i < hitCount; i++)
        {
            var target = _overlapResults[i].GetComponent<IFriendlyPD>();
            if (target == null || target.IsDead) continue;
            target.TakeDamage(state.damage, false, state.owner);
            hitAnyone = true;
        }
        if (hitAnyone)
            state.contactCooldownRemaining = contactCooldownDuration;
    }

    // Scratch buffer for the overlap query above — zero-alloc convention, same shape as every other enemy's
    // own scratch list in this codebase (e.g. ShooterGloomPD._multishotAngles).
    private readonly Collider[] _overlapResults = new Collider[8];

    private static float WrapAngle(float deg)
    {
        deg %= 360f;
        return deg < 0f ? deg + 360f : deg;
    }

    private static Vector3 SlotDirection(float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
    }

    private static void SetRenderersEnabled(Renderer[] renderers, bool value)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null) renderers[i].enabled = value;
    }

    // sharedMaterial, not .material — this is a pooled object launched repeatedly, same reasoning as
    // EnemyProjectilePD.SetRenderersMaterial.
    private static void SetRenderersMaterial(Renderer[] renderers, Material material)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null) renderers[i].sharedMaterial = material;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Instant, predicted hide/show — same pattern as EnemyProjectilePD's own UpdateView, simplified (no
        // revert-margin tracking) since a despawned orb never has gameplay consequence worth re-showing for
        // (unlike a projectile that might still need to hit something) — an orb popping back a frame late is
        // a non-issue, it was already done dealing damage.
        bool shouldHide = viewState.despawned;
        if (shouldHide != _hiddenOnDespawn)
        {
            _hiddenOnDespawn = shouldHide;
            SetRenderersEnabled(accentRenderers, !shouldHide);
            SetRenderersEnabled(otherRenderers, !shouldHide);
        }

        if (!shouldHide && yAxisMovers != null)
        {
            var local = yAxisMovers.localPosition;
            // Once orbiting, the rise is permanently done — snap to the exact target instead of trusting the
            // lerp's natural endpoint. spawnElapsed is clamped to spawnDuration but the owner's own phaseTimer
            // (the thing that actually flips isOrbiting) isn't, so a tick-ordering edge could in principle
            // flip isOrbiting one tick before spawnElapsed reads exactly spawnDuration — this guarantees the
            // visual always lands cleanly regardless.
            if (viewState.isOrbiting)
            {
                local.y = riseHeightHigh;
            }
            else
            {
                float t = viewState.spawnDuration > 0f ? Mathf.Clamp01(viewState.spawnElapsed / viewState.spawnDuration) : 1f;
                local.y = Mathf.Lerp(riseHeightLow, riseHeightHigh, t);
            }
            yAxisMovers.localPosition = local;
        }

        // Yellow stun tint — ONLY for a genuine stun, never for a Knockback-only freeze (see
        // State.stunned/knockbackFrozen's own doc comments). Trimmed version of EnemyBasePD's own tri-state
        // body-tint block (dead/flash take priority there too), since an orb has no death-flash/hit-flash of
        // its own to arbitrate against.
        if (!shouldHide)
            SetStunTint(viewState.stunned);

        // State-critical — only act on server-confirmed despawn, same reasoning as every other spawned
        // one-shot effect's deletion.
        if (verified.HasValue && verified.Value.despawned)
            hierarchy.Delete(this);
    }

    // See _originalAccentColors' own field comment for why this exists — captures each renderer's TRUE
    // current color (the tier material's own authored color, already applied by SetRenderersMaterial just
    // before this is called) so the stun-tint revert has something real to restore instead of guessing white.
    private void CacheOriginalAccentColors()
    {
        if (accentRenderers == null) return;
        _originalAccentColors = new Color[accentRenderers.Length];
        for (int i = 0; i < accentRenderers.Length; i++)
            _originalAccentColors[i] = accentRenderers[i] != null && accentRenderers[i].material != null
                ? accentRenderers[i].material.color
                : Color.white;
    }

    // stunned: true paints yellow; false restores each renderer's own cached original color (see
    // _originalAccentColors' own field comment for why this is never a hardcoded Color.white).
    private void SetStunTint(bool stunned)
    {
        if (accentRenderers == null || _originalAccentColors == null) return;
        for (int i = 0; i < accentRenderers.Length; i++)
        {
            if (accentRenderers[i] == null || accentRenderers[i].material == null) continue;
            accentRenderers[i].material.color = stunned
                ? Color.yellow
                : (i < _originalAccentColors.Length ? _originalAccentColors[i] : Color.white);
        }
    }
}
