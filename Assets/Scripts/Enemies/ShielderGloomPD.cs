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
    // Tier1-authored baseline, scaled per tier by rotationRateDegPerSecondTierGrowth below (applied in
    // OnConfigureSpawn(), then pushed into the Shield via SetRotationRateAndAttackRange) — see
    // _authoredRotationRateDegPerSecond's own doc comment for why this needs the snapshot-and-reset
    // treatment instead of just multiplying this field directly.
    [SerializeField] private float rotationRateDegPerSecond = 45f;
    // Same tier1-authored-baseline treatment as rotationRateDegPerSecond above.
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
    [SerializeField] private float attackCooldown = 5f;
    // Captured once in OnAwake(), reset back onto attackCooldown every pooled reuse in LateAwake() (before
    // Configure() reads it), scaled by attackCooldownMultiplier (Threat Level 5) in OnConfigureSpawn() and
    // pushed into the Shield via SpikeShieldPD.SetAttackCooldown() — same "_authoredX snapshot" pattern as
    // _authoredRotationRateDegPerSecond/_authoredAttackRange below.
    private float _authoredAttackCooldown;

    [Header("Tier Scaling")]
    // Linear per tier, multiplying the tier1-authored base — NOT compounding/exponential (e.g. tier3 is
    // base * (1 + 2*growth), not base * (1+growth)^2). Default 0.2 means tier2 = base * 1.2, matching the
    // "*1.2" the user specified as the tier2 shape; tier3/4 continue linearly (1.4x/1.6x), not accelerating.
    [SerializeField] private float rotationRateDegPerSecondTierGrowth = 0.2f;
    [SerializeField] private float attackRangeTierGrowth = 0.2f;

    // Captured once in OnAwake(), reset back onto rotationRateDegPerSecond/attackRange every pooled reuse in
    // LateAwake() (BEFORE Configure() reads them) — otherwise OnConfigureSpawn's scaling would keep
    // compounding across reuses, same "_authoredX snapshot" pattern established for the attack-count fields
    // on Blitz/Dagger/Mortar/Spear/Orbiter. Configure() itself (called from LateAwake(), below) always runs
    // BEFORE spawnTier is finalized for this life (same ordering hazard TierAccentMaterial's own doc comment
    // describes), so the tier-scaled values get pushed into the already-spawned Shield separately, later, in
    // OnConfigureSpawn() — see SetRotationRateAndAttackRange's own doc comment.
    private float _authoredRotationRateDegPerSecond;
    private float _authoredAttackRange;

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
    public CharacterStatsPD TargetStats => _targetStats;

    protected override void OnAwake()
    {
        base.OnAwake();
        _authoredRotationRateDegPerSecond = rotationRateDegPerSecond;
        _authoredAttackRange = attackRange;
        _authoredAttackCooldown = attackCooldown;
    }

    // Re-fires on every pooled reuse (same as every other LateAwake() override in this codebase), so a
    // fresh logical Shielder life always spawns and configures a fresh Shield — no extra bookkeeping needed
    // to keep the pair in sync across pooled respawns.
    protected override void LateAwake()
    {
        base.LateAwake();

        // Reset to the tier1-authored baseline before Configure() reads them below (and before
        // OnConfigureSpawn scales them fresh for this life) — see _authoredRotationRateDegPerSecond's own
        // doc comment.
        rotationRateDegPerSecond = _authoredRotationRateDegPerSecond;
        attackRange = _authoredAttackRange;
        attackCooldown = _authoredAttackCooldown;

        if (shieldPrefab == null) return;
        var id = hierarchy.Create(shieldPrefab, transform.position, transform.rotation);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out SpikeShieldPD shield))
        {
            _shield = shield;
            shield.Configure(this, orbitRadius, rotationRateDegPerSecond, attackRange, attackWidth,
                windupDuration, releaseDuration, attackCooldown, shieldburstDamageMultiplier,
                shieldContactDamageMultiplier, contactCooldownSeconds);
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

        rotationRateDegPerSecond = _authoredRotationRateDegPerSecond * (1f + (spawnTier - 1) * rotationRateDegPerSecondTierGrowth);
        attackRange = _authoredAttackRange * (1f + (spawnTier - 1) * attackRangeTierGrowth);
        _shield?.SetRotationRateAndAttackRange(rotationRateDegPerSecond, attackRange);

        // Threat Level 5 — halves attackCooldown (see EnemyBasePD.ConfigureSpawn's own doc comment).
        attackCooldown = _authoredAttackCooldown * attackCooldownMultiplier;
        _shield?.SetAttackCooldown(attackCooldown);
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
