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
    [SerializeField] private float orbitRadius = 1f;
    [SerializeField] private float rotationRateDegPerSecond = 180f;
    [SerializeField] private float attackRange = 3f;
    // Full width of the forward-facing rectangular attack zone (same convention SpearGloomPD.stabWidth
    // uses), not a half-width — easier to reason about from the Inspector.
    [SerializeField] private float attackWidth = 2f;
    [SerializeField] private float windupDuration = 0.5f;
    [SerializeField] private float releaseDuration = 0.15f;
    [SerializeField] private float attackCooldown = 3f;
    // Multipliers against this Shielder's own inherited `damage` stat — same shape as every other enemy
    // attack (e.g. SpearGloomPD.stabDamageMultiplier * damage), not a flat number — so these scale
    // correctly with whatever health/damage multiplier EnemySpawnerPD.SpawnEnemy's own ConfigureSpawn call
    // applies (elite/coop scaling, etc.) the same way every other enemy's attack already does.
    [SerializeField] private float shieldburstDamageMultiplier = 2f;
    [SerializeField] private float shieldContactDamageMultiplier = 1f;
    [SerializeField] private float contactCooldownSeconds = 0.5f;
    // Swapped onto whichever of the Shield's own renderers are configured to show it (see
    // SpikeShieldPD.readyMaterialRenderers) the instant it's actually able to trigger Shieldburst again —
    // reverts to each renderer's own original material while on cooldown or mid-attack. Current tier colors:
    // T1 red, T2 blue, T3 yellow — set per-prefab to match whichever tier this Shielder spawns in.
    [SerializeField] private Material shieldReadyMaterial;

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
            shield.Configure(this, orbitRadius, rotationRateDegPerSecond, attackRange, attackWidth,
                windupDuration, releaseDuration, attackCooldown, shieldburstDamageMultiplier,
                shieldContactDamageMultiplier, contactCooldownSeconds, shieldReadyMaterial);
        }
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
