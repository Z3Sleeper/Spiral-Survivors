using PurrNet.Prediction;
using UnityEngine;

// Mechanically almost identical to SpikeGloomPD ("Spiky") — no HandleActiveState override needed at all,
// the base EnemyBasePD's own default (if (_target != null) HandleMovement(...)) already IS the whole body
// behavior. Every bit of this enemy's actual complexity lives on the separate SpikeShieldPD it carries — see
// that file's own class doc comment for the full attack/tracking design. The body itself never freezes for
// the Shield's own attack (deliberately, unlike SpearGloomPD's own body going kinematic during its
// Windup/Stabbing) — that's the whole point of this enemy feeling different from Spear: the shield is mobile
// because it's attached to a body that keeps walking regardless of what the shield is doing.
//
// contactColliderEnabled should be set to false on this prefab's own ContactDamageCollider (Inspector-side,
// not code) — the body deals no contact damage of its own; that's now exclusively the Shield's job (see
// SpikeShieldPD's own always-on contact-damage trigger, kept fully separate from its single-target
// windup/release attack).
public class ShielderGloomPD : EnemyBasePD
{
    [Header("Shield")]
    [SerializeField] private GameObject shieldPrefab;

    // Every tunable the Shield needs is owned here, not on the Shield itself — the Shielder is the single
    // source of tuning truth (per explicit design), pushed into the Shield once via Configure() right after
    // it spawns. See SpikeShieldPD.Configure()'s own doc comment for what each parameter does.
    [Header("Shield Tuning")]
    [SerializeField] private float orbitRadius = 0.75f;
    // Tier1-authored, pristine read-only baseline forever (2026-09-22) — OnConfigureSpawn scales it fresh
    // into the Shield's own State.scaledRotationRateDegPerSecond (via SetRotationRateAndAttackRange) instead
    // of mutating this field in place, so no _authoredX snapshot/reset is needed anymore (see
    // EnemyBaseState.scaledMaxHealth's own doc comment for the full reasoning).
    [SerializeField] private float rotationRateDegPerSecond = 45f;
    // Multiplies rotationRateDegPerSecond ONLY while the Shield is in its Windup phase (2026-09-17) — makes
    // the telegraph read as more committed/predictable right before the burst fires, same spirit as every
    // other windup-slows-things convention in this codebase. Back to the full rate the instant Windup ends
    // (Releasing and FreeRotating both use the unscaled rate) — see SpikeShieldPD.Simulate()'s own Windup
    // case for where this is actually applied.
    [SerializeField] private float windupRotationRateMultiplier = 0.5f;
    // Same pristine-baseline treatment as rotationRateDegPerSecond above.
    [SerializeField] private float attackRange = 4f;
    // Full width of the forward-facing rectangular attack zone (same convention SpearGloomPD.stabWidth
    // uses), not a half-width — easier to reason about from the Inspector.
    [SerializeField] private float attackWidth = 1.4f;
    [SerializeField] private float windupDuration = 1f;
    [SerializeField] private float releaseDuration = 0.1f;
    // Multipliers against this Shielder's own inherited `damage` stat — same shape as every other enemy
    // attack (e.g. SpearGloomPD.stabDamageMultiplier * damage), not a flat number — so these scale
    // correctly with whatever health/damage multiplier EnemySpawnerPD.SpawnEnemy's own ConfigureSpawn call
    // applies (elite/coop scaling, etc.) the same way every other enemy's attack already does.
    [SerializeField] private float shieldburstDamageMultiplier = 1f;
    [SerializeField] private float shieldContactDamageMultiplier = 0.5f;
    [SerializeField] private float contactCooldownSeconds = 0.5f;

    [Header("Attack Cooldown")]
    // Same pristine-baseline treatment as rotationRateDegPerSecond above — scaled fresh into the Shield's
    // own State.scaledAttackCooldown (via SetAttackCooldown) in OnConfigureSpawn().
    [SerializeField] private float attackCooldown = 5f;

    [Header("Tier Scaling")]
    // Linear per tier, multiplying the tier1-authored base — NOT compounding/exponential (e.g. tier3 is
    // base * (1 + 2*growth), not base * (1+growth)^2). Default 0.2 means tier2 = base * 1.2, matching the
    // "*1.2" the user specified as the tier2 shape; tier3/4 continue linearly (1.4x/1.6x), not accelerating.
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;
    [SerializeField] private float attackRangeTierGrowth = 0.2f;

    // Plain, not State — a 1:1, whole-life companion object, same "spawned effect stores its owner as a
    // plain reference, not a re-resolved PredictedObjectID" convention AreaEffectPD/PhantomBladeSummonHelper
    // already use (reserved for references that need to survive pooling ambiguity, which this doesn't: the
    // Shield is destroyed alongside this exact Shielder life, never outlives or gets reassigned).
    private SpikeShieldPD _shield;

    // Exposes the inherited protected _target/_targetStats so SpikeShieldPD (a totally separate class
    // hierarchy, not IEnemyPD) can poll them directly every tick — this is how "the Shield inherits the
    // Shielder's own target" is implemented: the Shield always reads whatever this currently holds, rather
    // than the Shielder pushing a value in on some separate cadence. Same outcome, simpler code shape.
    public Transform Target => _target;
    public IFriendlyPD TargetStats => _targetStats;

    // Re-fires on every pooled reuse (same as every other LateAwake() override in this codebase), so a
    // fresh logical Shielder life always spawns and configures a fresh Shield — no extra bookkeeping needed
    // to keep the pair in sync across pooled respawns.
    protected override void LateAwake()
    {
        base.LateAwake();

        if (shieldPrefab == null) return;
        var id = hierarchy.Create(shieldPrefab, transform.position, transform.rotation);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out SpikeShieldPD shield))
        {
            _shield = shield;
            shield.Configure(this, orbitRadius, rotationRateDegPerSecond, windupRotationRateMultiplier,
                attackRange, attackWidth, windupDuration, releaseDuration, attackCooldown,
                shieldburstDamageMultiplier, shieldContactDamageMultiplier, contactCooldownSeconds);
        }
    }

    // TierAccentMaterial isn't resolvable correctly yet at LateAwake()-time above (spawnTier isn't finalized
    // for this life until ConfigureSpawn sets it, which runs AFTER LateAwake() — see TierAccentMaterial's own
    // doc comment), so the Shield's ready material is pushed here instead, once tier is known. Same reasoning
    // for rotationRateDegPerSecond/attackRange's own tier scaling — Configure() above already ran with the
    // UN-scaled baseline, so the final tier-scaled values get pushed into the already-spawned Shield here.
    protected override void OnConfigureSpawn()
    {
        base.OnConfigureSpawn();
        _shield?.SetReadyMaterial(TierAccentMaterial);

        float scaledRotationRate = rotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);
        float scaledAttackRange = attackRange * (1f + (spawnTier - 1) * attackRangeTierGrowth);
        _shield?.SetRotationRateAndAttackRange(scaledRotationRate, scaledAttackRange);

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment).
        _shield?.SetAttackCooldown(attackCooldown * attackCooldownMultiplier);
    }

    // Self-healing mirror of the SetReadyMaterial() push above — SpikeShieldPD can't read TierAccentMaterial
    // itself (it's a protected EnemyBasePD member, and the Shield isn't a subclass), so it only ever learns
    // its tier color via this push, which OnConfigureSpawn only makes on whichever machine actually spawned
    // this Shielder. On a machine where that never runs (PredictedHierarchy reconciling a misprediction —
    // see EnemyBaseState.spawnCategoryRaw's own doc comment), the Shield's ready color would silently stay
    // at SpikeShieldPD's own null-material fallback (a hardcoded red) forever. Edge-detected on spawnTier,
    // same "re-apply in UpdateView, which runs on every machine every frame" pattern base.UpdateView() now
    // uses for this enemy's own renderers/name.
    private int _lastPushedShieldTier = int.MinValue;

    // Forces the very first UpdateView() this life to re-push the Shield's ready material regardless of
    // what _lastPushedShieldTier was left at by whatever life last used this exact pooled GameObject — same
    // reasoning as EnemyBasePD._lastAppliedTier's own OnRespawnReset-adjacent reset (see its doc comment).
    protected override void OnRespawnReset()
    {
        base.OnRespawnReset();
        _lastPushedShieldTier = int.MinValue;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        int tier = viewState.b.spawnTier;
        if (tier != _lastPushedShieldTier)
        {
            _lastPushedShieldTier = tier;
            _shield?.SetReadyMaterial(TierAccentMaterial);
        }
    }

    // Interruptible exactly while the Shield is winding up — the Shield tells the Shielder its own commitment
    // state (SpikeShieldPD.IsWindingUp) since the Shielder's body has no windup/attack state of its own to
    // read (see class doc comment: the body never freezes for the Shield's attack, it's a fully separate
    // system). Deliberately does NOT also flip isStunnable — the body stays independently stunnable/mobile
    // regardless of what the Shield is doing, matching this enemy's whole "feels different from Spear"
    // design (a plain Stun() should still be able to freeze the body's own movement during a Shield windup;
    // only a real Interrupt() reaches in and cancels the Shield's own attack specifically).
    protected override bool CanBeInterrupted => _shield != null && _shield.IsWindingUp;

    // Tells the Shield it got interrupted so IT cancels its own windup — the Shielder's own state machine
    // has nothing of its own to reset (see class doc comment), so this is the entire override.
    protected override void OnInterrupted() => _shield?.CancelWindup();

    // Same fix as MortarGloomPD's own Die() override (2026-08-29, found via the same audit) — SpikeShieldPD.
    // LateSimulate() only guards on `_owner == null`, never `_owner`'s death state, so a Shielder dying
    // mid-Windup left its Shield still winding up (and eventually landing a hit) from a dead Shielder's own
    // position — Destroyed() below eventually cleans the Shield up, but only much later (deferred,
    // verified-isDead-gated), well after this gap. CancelWindup() is the exact same cross-identity
    // predicted-state mutation OnInterrupted() above already calls, and Die() runs synchronously from
    // TakeDamage() (Simulate()-context), so this is equally safe here.
    protected override void Die()
    {
        base.Die();
        _shield?.CancelWindup();
    }

    // Same CancelWindup() call as Die() above, for the difficulty-pacing wipe path (2026-10-16 fix) — see
    // EnemyBasePD.OnSilentDespawn's own doc comment. This is the time-sensitive "stop an in-progress
    // charge/windup from completing" call, same urgency as Die()'s own copy — distinct from Destroyed()'s
    // cleanup below, which already fires for either path but only much later, at actual verified deletion,
    // too late to stop a wipe-mid-windup Shield from still finishing its charge and detonating in the
    // meantime.
    protected override void OnSilentDespawn()
    {
        base.OnSilentDespawn();
        _shield?.CancelWindup();
    }

    // Hides/reveals the Shield's own renderers in lockstep with this body's predicted hide/verified-revert —
    // see EnemyBasePD.OnHiddenStateChanged's own doc comment. Without this, the body (this class's own
    // _renderers/_accentRenderers) would hide instantly on death while the Shield — a fully separate
    // PredictedIdentity, invisible to the base class's own hide system — kept rendering and (until the
    // LateSimulate()-side _owner._isDead fix) kept dealing contact damage (2026-10-15 fix).
    protected override void OnHiddenStateChanged(bool hidden)
    {
        base.OnHiddenStateChanged(hidden);
        _shield?.SetHidden(hidden);
    }

    // Destroyed() fires on every removal path this identity can go through (real death via UpdateView's
    // verified-isDead delete, SilentDespawn/wipe, or scene teardown) — hooking Shield cleanup here instead
    // of some death-specific callback guarantees it never orphans regardless of WHY the Shielder went away.
    protected override void Destroyed()
    {
        base.Destroyed();
        if (_shield != null)
        {
            hierarchy.Delete(_shield);
            _shield = null;
        }
    }
}
