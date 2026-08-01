using System;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

public class CharacterStatsPD : PredictedIdentity<CharacterStatsPD.State>
{
    public static readonly Dictionary<PlayerID, CharacterStatsPD> All = new();

    // Static, not per-instance PredictedEvent — fired from EnemyBasePD.UpdateView() off verified state with
    // its own edge-detection (isDead && !_wasDeadLastFrame), the same hand-rolled pattern already
    // established as functionally equivalent to PredictedEvent (see GameManagerPD.OnGameEnded), so it's
    // already safe to fire directly here without needing the PredictedEvent machinery itself. Fires for
    // every enemy death on every machine, regardless of killer (killer may be null if it doesn't resolve to
    // a living player) — a "my own kills only" augment filters by comparing killer against its own Stats
    // instance, which evaluates identically on every machine since killer/enemy are resolved the same way
    // everywhere. (A separate OnEnemyKill used to exist, gated to only firing on the killer's own local
    // machine — removed since it had zero subscribers and that owner-only gating would have desynced any
    // effect that needs to mutate shared/enemy state, like OnKillLaunchEnemiesAugment's corpse launch.)
    public static event Action<CharacterStatsPD, IEnemyPD> OnEnemyDeath;
    public static void RaiseOnEnemyDeath(CharacterStatsPD killer, IEnemyPD enemy) => OnEnemyDeath?.Invoke(killer, enemy);

    public enum CharacterClass { Knight, Rogue, Archer, Bladeweaver, Executioner }
    public CharacterClass characterClass;

    [SerializeField] private ClassPassiveData classPassive;
    // Read by AugmentManagerPD.RegisterClassPassiveAugments — see ClassPassiveData.passiveAugments' own
    // doc comment for why this is registered as a real pick rather than spawned directly here.
    public AugmentData[] ClassPassiveAugments => classPassive != null ? classPassive.passiveAugments : null;
    // Read by CharacterSelectUI.RefreshCharacterDetails — flavor text only, no gameplay effect.
    public string ClassDescription => classPassive != null ? classPassive.classDescription : string.Empty;

    // -1 = AugmentManagerPD.RegisterClassPassiveAugments() hasn't run for this player yet, so the real
    // count isn't known — deliberately NOT "ready" in this state (unlike 0), so PlayerDataPD's readiness
    // gate can't slip through during the one-tick lag between this player existing and that registration
    // pass actually running (see RegisterClassPassiveAugments' own doc comment on that lag). Once it calls
    // BeginWaitingForClassPassiveAugments with the real spawned count (0 if this class has no unique-effect
    // passives at all), this only ever counts down as each spawned effect confirms via NotifyAugmentApplied.
    private int _pendingClassPassiveAugments = -1;

    // True once every class-passive augment this player was granted has actually applied its OnApply()
    // effect — not just been spawned. Distinct from the plain-stat block ApplyClassPassives() already
    // applies synchronously; this covers the passiveAugments[] escape hatch specifically, whose effects
    // resolve asynchronously (spawn -> verified state -> OnApply). Used to hold PlayerDataPD's "player
    // ready" gate open long enough that the loading screen can't drop before those stats are actually live
    // (fixes a visible "stat pops in after load" flash, e.g. a class-passive max-health bonus).
    public bool ClassPassiveAugmentsReady => _pendingClassPassiveAugments == 0;

    // Called once by AugmentManagerPD.RegisterClassPassiveAugments() with the exact number of this
    // player's class-passive augment effects it actually spawned this pass — the one place that knows
    // that count for certain (SpawnAugmentEffect can silently no-op, e.g. a database-only augment with no
    // effectPrefab, so the count must come from confirmed spawns, not just array length).
    public void BeginWaitingForClassPassiveAugments(int count)
    {
        _pendingClassPassiveAugments = count;
    }

    // Called by AugmentEffectPD once its own OnApply() has actually run (confirmed off verified state) —
    // every augment effect reports here, not just class-passive ones, but the guard means a normal
    // level-up pick's effect (always well after game start, once this is already at 0) is a harmless
    // no-op.
    public void NotifyAugmentApplied()
    {
        if (_pendingClassPassiveAugments > 0) _pendingClassPassiveAugments--;
    }

    // ── Serialized base values ─────────────────────────────────────────────

    [Header("Movement")]
    public float baseMoveSpeed = 10f;

    [Header("Weapon")]
    public float strengthPerLevel = 2.5f;
    public float baseStrength = 15f;
    public float baseWeaponRadius = 0f;
    public float baseAttackSpeed = 0.5f;
    public int baseWeaponCount = 1;
    // Extra weapon "tracks" (e.g. the planned OuterWeaponTrackAugment), feeding into Strength's own
    // auto-divide below. Deliberately a single plain int, not a base/flat/multiplier scaffold like
    // baseWeaponCount — this is intentionally-unused-elsewhere bookkeeping, directly += a small integer
    // whenever tracks are added or removed (see AddWeaponTracks), never multiplied.
    public int weaponTracks = 1;
    public float baseWeaponRange = 10f;

    [Header("Crit")]
    public float baseCritChance = 0f;
    public float baseCritDamage = 2f;

    [Header("Ranged Weapons")]
    public float baseRicochet = 0f;
    public float baseMultishot = 0f;
    public float baseMultishotSpreadAngle = 75f;
    public float baseProjectileDamageDropoff = 0.1f;
    public float baseProjectileOnHitDropoff = 0.1f;
    // Neutral 1f baseline, same as every other "base" stat here — WeaponRangedPD reads FireRate/
    // ProjectileSpeed off this instead of carrying its own local fields, so a class passive (e.g. Archer's
    // fireRateFlat/projectileSpeedFlat) can tune a specific weapon's feel without a weapon-specific field.
    public float baseFireRate = 1f;
    public float baseProjectileSpeed = 1f;

    [Header("Health")]
    public float baseMaxHealth = 50f;
    public float baseHealthRegen = 0f;

    [Header("Shield")]
    public float baseMaxShieldPercent = 0.05f;
    public float shieldRechargeTime = 5f;

    [Header("Energy")]
    public float baseMaxEnergy = 50f;
    public float baseEnergyRegen = 4f;

    [Header("Surge")]
    public float baseSurgeCost = 2f;
    public float surgeMoveSpeedMulti = 1f;
    public float surgeMoveSpeedPenalty = 0.5f;
    public float surgeAttackSpeedMulti = 2f;

    [Header("Dash")]
    public int dashCharges = 1;
    public float baseDashCooldown = 5f;
    public int levelUpOptions = 3;

    [Header("Luck")]
    public float baseLuck = 0f;
    // Worst/best-case roll multipliers for anything that wants a "luck-influenced random roll" (weapon
    // damage, future augments, etc.) — roll against these shared stats instead of hardcoding a variance
    // constant, so a single augment can move the floor or ceiling and affect every roller at once. Split
    // into two independent multipliers (not a single symmetric +-variance) since a floor that can be
    // pushed past 100% would allow zero/negative rolls; the ceiling has no such risk, so it isn't capped.
    public float baseLuckFloor = 0.5f;
    public float baseLuckCeiling = 1.5f;
    // The "floor" of a highroll — a roll landing in the top (1 - Highroll) fraction of its own window
    // snaps up to the max and invokes the hitting player's own CharacterStatsPD._onHighroll (a PredictedEvent,
    // rollback-safe, called via NotifyHighroll from whatever weapon/effect actually rolled the hit). Same
    // base+bonus+penalty
    // convention as every other stat here, so a future augment/passive can push it (e.g. toward 1.0 = never
    // highrolls, or lower = highrolls more often) without touching WeaponDamageMath itself.
    public float baseHighroll = 0.95f;
    // Bonus damage applied WHEN a hit highrolls — separate from baseHighroll (which only decides whether a
    // roll counts as one). Applies as (1 + HighrollBonus) before crit, so the two stack multiplicatively
    // rather than either replacing the other.
    public float baseHighrollBonus = 0.5f;

    [Header("Knockback")]
    public float baseKnockback = 10f;

    [Header("Misc")]
    public float baseOnHitPower = 1f;
    public float baseXPRadius = 5f;
    [SerializeField] private PlayerColorPalette _colorPalette;

    [Header("Audio")]
    [SerializeField] private AudioClip _damagedSfx;
    public AudioClip hitSfx;

    [Header("Visuals")]
    [SerializeField] private GameObject _invincibilityShell;
    // Dedicated to the model only — deliberately NOT routed through PlayerColorApplicator, which also
    // drives the minimap icon and the ground circle indicator, neither of which should fade with invisibility.
    [SerializeField] private Renderer _modelRenderer;
    [SerializeField, Range(0f, 1f)] private float invisibleModelAlpha = 0.3f;
    // Continuously spun in UpdateView() at the same rate weapons orbit (WeaponOrbitAngle) — purely cosmetic
    // flair, always on for every character, not gated to any ability. A burst/effect that also wants the
    // model tipped sideways (e.g. the burst leap's "somersault" look) calls SetModelSidewaysTipped rather
    // than writing this transform directly — with the continuous Y-axis spin already running, tipping the
    // whole thing 90 degrees on Z turns that spin into a forward-tumbling motion for free, and keeps this
    // the single owner of the transform instead of two systems fighting over it each frame.
    [SerializeField] private Transform _modelTransform;
    private bool _modelSidewaysTipped;
    // Sign must always match WeaponBasePD.OrbitFlipTip/ModelTipRotation's own sign — see the latter's doc
    // comment for why this is negated rather than reversing weaponOrbitAngle's own rotation direction
    // (shared with ordinary idle spin, so flipping that would also reverse normal orbiting).
    private static readonly Quaternion ModelSidewaysTipRotation = Quaternion.Euler(0f, 0f, -90f);
    public void SetModelSidewaysTipped(bool value) => _modelSidewaysTipped = value;

    // World-space yaw (degrees) this class's own model spin/tip is additionally rotated by — lets an effect
    // like the spin ability's leap make the body face a travel direction while it tumbles, instead of always
    // facing a fixed world +Z. 0 = no extra rotation. Weapons don't read this at all (never have) — this only
    // ever drives the player's own model tip/spin.
    public float OrbitFacingYaw { get; private set; }
    public void SetOrbitFacingYaw(float yawDegrees) => OrbitFacingYaw = yawDegrees;

    [Header("Revive")]
    public float reviveTime = 3f;

    // ── Augment/bonus modifiers ────────────────────────────────────────────

    // bonusMoveSpeedFlat/Percent/moveSpeedPenalty are the "Bonus" tier — class passives
    // (ClassPassiveData/ApplyClassPassives) and meta-progression are the only writers, so bonusMoveSpeedPercent
    // only ever scales bonusMoveSpeedFlat (see MoveSpeed below), never base directly. totalMoveSpeedPercent is
    // the NEW "Total" tier — the one in-run stat augments (MoveSpeedAugment and friends) grant, scaling the
    // whole (base + bonus) sum uniformly regardless of how big base itself is. See
    // project_bonus_total_stat_tiers memory for the full reasoning/formula.
    [HideInInspector] public float bonusMoveSpeedFlat = 0f;
    [HideInInspector] public float bonusMoveSpeedPercent = 1f;
    [HideInInspector] public float moveSpeedPenalty = 1f;
    [HideInInspector] public float totalMoveSpeedPercent = 1f;
    [HideInInspector] public float totalMoveSpeedPenalty = 1f;

    // bonusStrengthPercent/bonusStrengthPenalty are Bonus tier (class passives/meta-progression only).
    // totalStrengthPercent/totalStrengthPenalty are the Total tier — what the weapon-count/multishot/track
    // "compensate with a damage bonus" augments (BladeweaviestBladeweaverAugment, MultiShotAugment,
    // OuterTrackAugment, SurgeWeaponCountAugment, WeaponCountAugment) grant instead, now that they're not
    // "the 9" but still needed redirecting per the same-stat-being-changed rule.
    [HideInInspector] public float bonusStrengthFlat = 0f;
    [HideInInspector] public float bonusStrengthPercent = 1f;
    [HideInInspector] public float bonusStrengthPenalty = 1f;
    [HideInInspector] public float totalStrengthPercent = 1f;
    [HideInInspector] public float totalStrengthPenalty = 1f;

    [HideInInspector] public float bonusWeaponRadiusFlat = 0f;
    [HideInInspector] public float bonusWeaponRadiusPercent = 1f;
    [HideInInspector] public float totalWeaponRadiusPercent = 1f;
    [HideInInspector] public float totalWeaponRadiusPenalty = 1f;

    // Despite the old name, this is a flat, in-units addition to baseAttackSpeed, not a genuine percent
    // multiplier — see AddAttackSpeedFlat's own comment. Renamed from attackSpeedPercent.
    [HideInInspector] public float attackSpeedFlat = 0f;
    [HideInInspector] public float attackSpeedTotalPercent = 1f;
    [HideInInspector] public float attackSpeedPenalty = 1f;

    [HideInInspector] public int bonusWeaponCountFlat = 0;
    [HideInInspector] public int weaponCountMultiplier = 1;

    [HideInInspector] public float bonusWeaponRangeFlat = 0f;
    [HideInInspector] public float bonusWeaponRangePercent = 1f;
    [HideInInspector] public float weaponRangePenalty = 1f;
    [HideInInspector] public float totalWeaponRangePercent = 1f;
    [HideInInspector] public float totalWeaponRangePenalty = 1f;

    // Character-size augments (Titan, and any future ones) add their delta here rather than each
    // multiplying transform.localScale directly — additive, so multiple size augments stack (+25% +
    // +30% = +55%) instead of compounding multiplicatively (1.25 * 1.30 = 1.625x). Every weapon's own
    // root localScale reads this directly too (see WeaponManagerPD's own RegisterWeapon/LateUpdate), so
    // the weapon grows with the character without a separate per-augment multiply call. WeaponRadius
    // (orbit distance) deliberately does not — see its own comment for why.
    [HideInInspector] public float bonusSizePercent = 1f;

    [HideInInspector] public float bonusCritChanceFlat = 0f;
    [HideInInspector] public float bonusCritChancePercent = 1f;
    [HideInInspector] public float totalCritChancePercent = 1f;
    [HideInInspector] public float totalCritChancePenalty = 1f;

    [HideInInspector] public float bonusCritDamageFlat = 0f;
    [HideInInspector] public float bonusCritDamagePercent = 1f;

    [HideInInspector] public float bonusRicochetFlat = 0f;
    [HideInInspector] public float bonusRicochetPercent = 1f;
    [HideInInspector] public float totalRicochetPercent = 1f;
    [HideInInspector] public float totalRicochetPenalty = 1f;

    [HideInInspector] public float bonusMultishotFlat = 0f;
    [HideInInspector] public float bonusMultishotPercent = 1f;
    [HideInInspector] public float bonusMultishotSpreadPercent = 1f;
    [HideInInspector] public float totalMultishotPercent = 1f;
    [HideInInspector] public float totalMultishotPenalty = 1f;

    [HideInInspector] public float projectileDamageDropoffReduction = 1f;
    [HideInInspector] public float projectileOnHitDropoffReduction = 1f;

    // Same (base + flat) * totalPercent * totalPenalty shape as AttackSpeed — moved off WeaponRangedPD's own
    // local fields so any future ranged weapon can share the same class-passive-tunable stats.
    [HideInInspector] public float fireRateFlat = 0f;
    [HideInInspector] public float fireRateTotalPercent = 1f;
    [HideInInspector] public float fireRateTotalPenalty = 1f;

    [HideInInspector] public float projectileSpeedFlat = 0f;
    [HideInInspector] public float projectileSpeedTotalPercent = 1f;
    [HideInInspector] public float projectileSpeedTotalPenalty = 1f;

    [HideInInspector] public float bonusMaxHealthFlat = 0f;
    [HideInInspector] public float bonusMaxHealthPercent = 1f;
    [HideInInspector] public float totalMaxHealthPercent = 1f;
    [HideInInspector] public float totalMaxHealthPenalty = 1f;

    [HideInInspector] public float bonusHealthRegenFlat = 0f;
    [HideInInspector] public float bonusHealthRegenPercent = 1f;
    [HideInInspector] public float totalHealthRegenPercent = 1f;
    [HideInInspector] public float totalHealthRegenPenalty = 1f;

    // Additive against baseMaxShieldPercent (both in the same "percent of Max Health" units, e.g. +0.05 =
    // +5% Max Health as shield) — no separate multiplier tier between base and bonus, unlike most other
    // stats' bonusFlat*bonusPercent shape (see MaxShield's own comment for why Shield's shape is simpler).
    // Deliberately NOT named bonusMaxShieldPercent — that name used to belong to the OLD multiplier tier
    // (default 1f) this replaced, and 0BaseCharacter.prefab still had a stale `bonusMaxShieldPercent: 1`
    // serialized under the old meaning; reusing the same name would have silently loaded that stale 1 into
    // this new additive-pool field instead of the intended 0 default (exactly the bug that shipped once,
    // inflating shield by a stray +100% of Max Health) — a fresh name sidesteps the collision entirely.
    [HideInInspector] public float bonusMaxShieldAdditive = 0f;
    [HideInInspector] public float totalMaxShieldPercent = 1f;
    [HideInInspector] public float totalMaxShieldPenalty = 1f;

    [HideInInspector] public float bonusMaxEnergyFlat = 0f;
    [HideInInspector] public float bonusMaxEnergyPercent = 1f;

    [HideInInspector] public float bonusEnergyRegenFlat = 0f;
    [HideInInspector] public float bonusEnergyRegenPercent = 1f;
    [HideInInspector] public float energyRegenPenalty = 1f;
    [HideInInspector] public float totalEnergyRegenPercent = 1f;
    [HideInInspector] public float totalEnergyRegenPenalty = 1f;

    [HideInInspector] public float bonusSurgeCostFlat = 0f;
    [HideInInspector] public float bonusSurgeCostPercent = 1f;
    [HideInInspector] public float surgeAttackSpeedPenalty = 1f;

    [HideInInspector] public float dashHaste = 0f;
    // Multiplicative on top of dashHaste (see DashCooldown) — same "additive against a 1f baseline"
    // pattern as bonusWeaponRangePercent/bonusCritChancePercent, added alongside dashHaste's existing flat
    // grant so DashHaste-family augments can have the same flat+percent dual structure Range/CritChance do.
    // dashHaste has no separate serialized "base" field (it starts at 0 and is purely additive, unlike
    // every other stat here), so there's no distinct base/bonus split to make for this one — totalDashHastePercent
    // is simply appended as one more multiplier. Nothing but DashHasteAugment ever wrote bonusDashHastePercent
    // anyway (ClassPassiveData/progression only ever add to dashHaste itself), so redirecting it to Total is
    // a clean, zero-risk swap.
    [HideInInspector] public float bonusDashHastePercent = 1f;
    [HideInInspector] public float totalDashHastePercent = 1f;
    [HideInInspector] public float totalDashHastePenalty = 1f;

    // Same shape as dashHaste immediately above (see its own comment for the full reasoning) — for a
    // future ability-cooldown-haste formula, not yet consumed by any ability. No separate base field,
    // purely additive, bonusAbilityHastePercent/totalAbilityHastePercent both multiply on top the same way.
    [HideInInspector] public float abilityHaste = 0f;
    [HideInInspector] public float bonusAbilityHastePercent = 1f;
    [HideInInspector] public float totalAbilityHastePercent = 1f;
    [HideInInspector] public float totalAbilityHastePenalty = 1f;

    [HideInInspector] public float bonusLuckFlat = 0f;
    [HideInInspector] public float bonusLuckPercent = 1f;
    [HideInInspector] public float luckPenalty = 1f;
    [HideInInspector] public float totalLuckPercent = 1f;
    [HideInInspector] public float totalLuckPenalty = 1f;
    [HideInInspector] public float bonusLuckFloorPercent = 1f;
    [HideInInspector] public float luckFloorPenalty = 1f;
    [HideInInspector] public float bonusLuckCeilingFlat = 0f;
    [HideInInspector] public float bonusLuckCeilingPercent = 1f;
    [HideInInspector] public float luckCeilingPenalty = 1f;
    [HideInInspector] public float bonusHighrollPercent = 1f;
    [HideInInspector] public float highrollPenalty = 1f;
    [HideInInspector] public float bonusHighrollBonusPercent = 1f;
    [HideInInspector] public float highrollBonusPenalty = 1f;

    [HideInInspector] public float knockbackMultiplier = 1f;
    [HideInInspector] public float knockbackPenalty = 1f;

    [HideInInspector] public float bonusOnHitPowerMultiplier = 1f;
    [HideInInspector] public float bonusOnHitPowerPenalty = 1f;
    // Total tier — bonusOnHitPowerMultiplier is already a genuine multiplier (no base/flat addition to
    // split here), written only by ClassPassiveData/meta-progression now (Bonus tier, same as every other
    // stat's bonus term). The weapon-count/multishot "compensate" augments (BladeweaviestBladeweaverAugment,
    // MultiShotAugment, OuterTrackAugment, SurgeWeaponCountAugment, WeaponCountAugment) all call
    // AddOnHitPowerTotalPercent below instead — same Total-tier treatment as their Strength grant, not the
    // old MultiplyOnHitPowerMultiplier compounding.
    [HideInInspector] public float totalOnHitPowerPercent = 1f;
    [HideInInspector] public float totalOnHitPowerPenalty = 1f;

    [HideInInspector] public float stackPowerPercent = 1f;
    [HideInInspector] public float stackPowerPenalty = 1f;

    // Same Percent*Penalty shape as StackPower — interacts with each ability in its own unique way (see
    // each IAbilityPD implementer's own doc comment for how it reads this), but generally scales ability
    // damage/effect strength/buff duration up (or down, via the penalty term).
    [HideInInspector] public float abilityPowerMultiplier = 1f;
    [HideInInspector] public float abilityPowerPenalty = 1f;

    // "AoE Effects" tag — modifiers for any augment tagged as an area effect (DashExplosionAugment,
    // DashHealAugment, etc.). Percent+Penalty pair for the two floats, same shape as StackPower
    // (AreaEffectSize/AreaEffectPower below = Percent * Penalty). Count mirrors WeaponCount's own int
    // Flat*Multiplier shape instead of a float Percent/Penalty pair, since it's a discrete repeat count, not
    // a continuous scale.
    [HideInInspector] public float areaEffectSizePercent  = 1f;
    [HideInInspector] public float areaEffectSizePenalty  = 1f;
    [HideInInspector] public float areaEffectPowerPercent = 1f;
    [HideInInspector] public float areaEffectPowerPenalty = 1f;
    [HideInInspector] public int   areaEffectCountFlat    = 1;
    [HideInInspector] public int   areaEffectCountPenalty = 1;

    public float AreaEffectSize  => areaEffectSizePercent * areaEffectSizePenalty;
    // Auto-divided by AreaEffectCount, same reasoning as Strength's auto-divide by WeaponCount — picking up
    // more casts per trigger shouldn't multiply total output for free. AreaEffectCountAugment compensates
    // with its own flat AreaEffectPower grant, same "extra copies, compensate with a straight bonus" pattern
    // WeaponCountAugment already uses for Strength.
    public float AreaEffectPower => areaEffectPowerPercent * areaEffectPowerPenalty / AreaEffectCount;
    public int   AreaEffectCount => areaEffectCountFlat * areaEffectCountPenalty;

    // Character-level multiplier on top of whatever a weapon's own outer-zone extent is authored at
    // (currently only WeaponAxePD.outerXZExtent — the outer/blade capsule's reach in the X/Z plane, i.e.
    // everything except the up/down axis; see its own ComputeZones/UpdateOuterBladeVisual for where this
    // actually gets read). Named XZ, not X — it scales the capsule's side-to-side spread AND its
    // forward/back+sideways radius together (a proportionally bigger axehead), deliberately excluding
    // height/thickness (Y). Single-tier, not a Percent+Penalty pair like AreaEffectPower — no known debuff
    // source for this exists yet, so no Penalty term to multiply against; add one later if something
    // concretely needs it. Generic here on CharacterStatsPD (not baked into WeaponAxePD itself) so any
    // future weapon type that grows its own "outer zone" concept can read the same stat for free, same
    // reasoning AreaEffectPower/AreaEffectSize already follow.
    [HideInInspector] public float outerXZMultiplierPercent = 1f;
    public float OuterXZMultiplier => outerXZMultiplierPercent;

    [HideInInspector] public float xpMultiplier = 1f;
    [HideInInspector] public float xpPenalty = 1f;

    [HideInInspector] public float bonusXPRadiusFlat = 0f;
    [HideInInspector] public float bonusXPRadiusPercent = 1f;

    [HideInInspector] public float reviveTimePenalty = 1f;
    [HideInInspector] public float reviveTimePenaltyReduction = 1f;
    [HideInInspector] public float revivePower = 1f;

    [HideInInspector] public float damageReductionPercent = 1f;
    [HideInInspector] public float damageReductionPenalty = 1f;

    // ── Computed properties ────────────────────────────────────────────────

    // Bonus/Total split: bonusMoveSpeedPercent only scales bonusMoveSpeedFlat (the "Bonus" sub-term, still
    // written only by ClassPassiveData/meta-progression), then totalMoveSpeedPercent scales the WHOLE
    // (base + bonus) sum — so an in-run "+X% total move speed" augment is worth the same relative bump
    // regardless of how big base/bonus already are, instead of being multiplied only against whatever tiny
    // bonus pool happens to exist. See project_bonus_total_stat_tiers memory for the full formula.
    public float MoveSpeed => (baseMoveSpeed + bonusMoveSpeedFlat * bonusMoveSpeedPercent * moveSpeedPenalty) * totalMoveSpeedPercent * totalMoveSpeedPenalty * (currentState.isSurging ? SurgeMoveSpeedMulti : 1f);

    // Auto-divided by total simultaneous damage instances (weapon tracks * weapon copies * shots per
    // attack) so picking up more weapons/multishot no longer needs its own hand-tuned damage penalty per
    // augment — total output stays roughly constant and augments granting more copies can just give a
    // straight damage bonus on top instead. (1f + Multishot) because Multishot itself is "bonus shots
    // beyond the first" (base 0), matching the same "+1" WeaponRangedPD.Fire() already applies when turning
    // it into an actual shot count — dividing by raw Multishot would divide by zero for anyone without it.
    // UnpenalizedStrength deliberately excludes only this divide — Phantom Blade damage reads this baseline
    // and isn't one of the "extra simultaneous copies" this divide exists to counterbalance. It shares the
    // exact same (base + bonus) * total construction as Strength otherwise, penalties included.
    // Same Bonus/Total split as MoveSpeed — bonusStrengthPenalty is folded into the Bonus sub-term
    // (only ever written by ClassPassiveData/meta-progression, same as every other stat's bonus penalty),
    // totalStrengthPenalty is the Total-tier counterpart.
    public float Strength => (baseStrength + bonusStrengthFlat * bonusStrengthPercent * bonusStrengthPenalty) * totalStrengthPercent * totalStrengthPenalty
        / (TotalWeaponCount * (1f + Multishot));
    public float UnpenalizedStrength => (baseStrength + bonusStrengthFlat * bonusStrengthPercent * bonusStrengthPenalty) * totalStrengthPercent * totalStrengthPenalty;
    // Deliberately doesn't factor in bonusSizePercent, unlike a weapon's own hitbox/visual scale —
    // Bladeweaver is the only class that uses this for actual orbit distance (hovering blades), and a size
    // augment pushing that distance out further makes it harder for them to hit anything nearby, which
    // isn't the intent of a size augment. The weapon's own root scale still scales with size; this
    // doesn't need to.
    // totalWeaponRadiusPercent: no in-run augment grants this yet (only ClassPassiveData touches
    // bonusWeaponRadiusPercent) — added for future use per the same Bonus/Total split as every other stat.
    public float WeaponRadius => (baseWeaponRadius + bonusWeaponRadiusFlat * bonusWeaponRadiusPercent) * totalWeaponRadiusPercent * totalWeaponRadiusPenalty;

    // Already implements the target Bonus/Total shape and needs no change: attackSpeedFlat IS this stat's
    // "bonus" term already — added directly to base in absolute units, same role bonusMoveSpeedFlat plays
    // elsewhere — and attackSpeedTotalPercent already is the genuine Total-tier multiplier AttackSpeedAugment
    // grants. attackSpeedPenalty deliberately keeps its own "multiplies everything, outside both tiers"
    // position — unlike Strength's bonusStrengthPenalty, no in-run system currently needs this reconsidered.
    public float AttackSpeed => (baseAttackSpeed + attackSpeedFlat) * attackSpeedTotalPercent * attackSpeedPenalty * (currentState.isSurging ? SurgeAttackSpeedMultiplier : 1f);
    public float WeaponSpeed => AttackSpeed * 360f;
    // Overrides WeaponSpeed for the shared weaponOrbitAngle tick below when set — lets a special action
    // (e.g. the burst) spin weapons at a fixed, snapshotted rate instead of the live AttackSpeed-driven one,
    // without touching AttackSpeed itself (which would also affect FireRate and anything else derived from
    // it). Same degrees/sec units as WeaponSpeed. Plain fields, not predicted State — set/cleared from
    // whichever effect's own Simulate() is driving them, same "safe cross-identity currentState mutation
    // from a deterministic context" reasoning as StartSurge()/GrantInvincibility(), just for a plain field
    // instead of currentState here since nothing about this needs rollback (it's fully re-derived identically
    // every resimulation pass from whatever set it, same as _burstFacingYaw's own reasoning).
    [HideInInspector] public float specialSpinRate;
    [HideInInspector] public bool  useSpecialSpinRate;
    public void SetSpecialSpinRate(float degreesPerSecond) => specialSpinRate = degreesPerSecond;
    public void SetUseSpecialSpinRate(bool value) => useSpecialSpinRate = value;
    public float EffectiveWeaponSpeed => useSpecialSpinRate ? specialSpinRate : WeaponSpeed;
    // Whether WeaponBasePD.OrbitalPosition should sweep a vertical (tipped) plane instead of the normal
    // horizontal one — purely a VISUAL orbit-shape switch, deliberately separate from IsWeaponsDisabled
    // (which only ever gates damage application, nothing else — see its own doc comment). Kept as its own
    // flag rather than folded into IsWeaponsDisabled specifically so a future effect can disable weapon
    // damage without also flipping the orbit, or vice versa. Plain field, not predicted State, same
    // reasoning as specialSpinRate/useSpecialSpinRate above: fully re-derived identically every resimulation
    // pass from whichever effect's own Simulate() is driving it, nothing here needs rollback.
    [HideInInspector] public bool weaponOrbitFlipped;
    public bool IsOrbitFlipped => weaponOrbitFlipped;
    public void SetOrbitFlipped(bool value) => weaponOrbitFlipped = value;
    // Set by ArcherFocusFireAbilityPD for its own active window — read by WeaponRangedPD.LateSimulate() to
    // switch its firing behavior: normally a shot requires FindTarget() to first confirm something's
    // actually in the raycast's path before firing; while true, it skips that requirement entirely and just
    // fires straight forward (wherever the weapon is currently aimed) every time its normal fire-rate timer
    // allows, target or not. Same plain-field, set-from-Simulate() convention as weaponOrbitFlipped above —
    // fully re-derived identically every resimulation pass from whichever effect is currently driving it.
    [HideInInspector] public bool continuousFire;
    public bool ContinuousFire => continuousFire;
    public void SetContinuousFire(bool value) => continuousFire = value;
    // The shared OrbitalPosition baseAngle every weapon on this character should use INSTEAD of the normal,
    // ever-incrementing WeaponOrbitAngle while ContinuousFire is active — same Atan2-based convention
    // OrbitalPosition's own flatOffset math uses (X = cos(rad), Z = sin(rad)), so setting this to
    // Atan2(cursorDir.z, cursorDir.x) * Rad2Deg makes the SlotIndex-0 weapon point exactly at the cursor.
    // Every other weapon still applies its own normal 360/total*SlotIndex offset on top of this shared base
    // exactly as it always does during ordinary orbiting — only the shared base itself changes source, so
    // the whole ring freezes and re-aims as one rigid formation instead of each weapon trying to face the
    // cursor independently. Same plain-field, set-from-Simulate() convention as weaponOrbitFlipped above.
    [HideInInspector] public float focusFireAimAngle;
    public float FocusFireAimAngle => focusFireAimAngle;
    public void SetFocusFireAimAngle(float degrees) => focusFireAimAngle = degrees;
    // Overrides the point every weapon orbits (WeaponMeleePD/WeaponAxePD/WeaponRangedPD each read this
    // instead of transform.position directly for their own LateSimulate origin) — lets a special action
    // relocate the whole orbit somewhere other than the player (e.g. BWBladeDanceAbilityPD sending its
    // blades out to a target point, still orbiting normally, just around that point instead of the player)
    // without touching WeaponOrbitAngle/EffectiveWeaponSpeed at all — weapons keep spinning at their normal
    // rate, just around a different center. Same plain-field, set-from-Simulate() convention as
    // specialSpinRate/weaponOrbitFlipped above: fully re-derived identically every resimulation pass from
    // whichever effect is currently driving it, nothing here needs rollback.
    [HideInInspector] public bool    weaponOrbitOriginOverridden;
    [HideInInspector] public Vector3 weaponOrbitOriginOverride;
    public Vector3 EffectiveOrbitOrigin => weaponOrbitOriginOverridden ? weaponOrbitOriginOverride : transform.position;
    // null clears the override (weapons revert to orbiting transform.position directly next tick).
    public void SetOrbitOriginOverride(Vector3? origin)
    {
        weaponOrbitOriginOverridden = origin.HasValue;
        if (origin.HasValue) weaponOrbitOriginOverride = origin.Value;
    }
    // Extra world Y added to WeaponBasePD.OrbitalPosition's result — lets a special action's own vertical
    // arc (e.g. the spin ability's leap height) carry weapons along with it, since the player's REAL
    // transform never actually moves in Y for that leap (only a cosmetic Visuals-child offset does — see
    // RogueSpinAbilityPD.UpdateView's own arcY). Same plain-field, set-from-Simulate() convention as
    // specialSpinRate/weaponOrbitFlipped above.
    [HideInInspector] public float specialHeightOffset;
    public float SpecialHeightOffset => specialHeightOffset;
    public void SetSpecialHeightOffset(float value) => specialHeightOffset = value;
    // Public read access to the shared orbit angle (see State.weaponOrbitAngle's own doc comment) — every
    // weapon (WeaponMeleePD/WeaponAxePD/WeaponRangedPD) reads this instead of ticking its own copy.
    public float WeaponOrbitAngle => currentState.weaponOrbitAngle;
    public float AttackSpeedPercent => attackSpeedTotalPercent * attackSpeedPenalty;

    public int WeaponCount => (baseWeaponCount + bonusWeaponCountFlat) * weaponCountMultiplier;
    // Total simultaneous weapon-equivalent count across every track — weaponTracks >= 2 (OuterTrackAugment)
    // mirrors WeaponCount onto a second ring (see WeaponManagerPD.weaponTracks' own doc comment), so the
    // real total a per-weapon effect should scale by is this product, not WeaponCount alone. Extracted here
    // since Strength/OnHitPower's own auto-divide below already needed this exact product — any future
    // per-weapon-count scaling (e.g. RogueSpinAbilityPD's hit-count-per-weapon) should read this instead of
    // reimplementing the same multiply.
    public int TotalWeaponCount => weaponTracks * WeaponCount;
    // Same Bonus/Total split as MoveSpeed — totalWeaponRangePercent is the new Total tier RangeAugment (and
    // its outlier siblings OuterTrackAugment/TitanAugment) grant; weaponRangePenalty is folded into the
    // Bonus sub-term (ClassPassiveData is its only writer, same as moveSpeedPenalty/energyRegenPenalty).
    public float WeaponRange => (baseWeaponRange + bonusWeaponRangeFlat * bonusWeaponRangePercent * weaponRangePenalty) * totalWeaponRangePercent * totalWeaponRangePenalty * 0.2f;

    // Same Bonus/Total split — totalCritChancePercent is the new Total tier CritChanceAugment grants.
    public float CritChance => (baseCritChance + bonusCritChanceFlat * bonusCritChancePercent) * totalCritChancePercent * totalCritChancePenalty;
    public float CritDamage => ((baseCritDamage + bonusCritDamageFlat) * bonusCritDamagePercent) + Mathf.Max(0f, CritChance - 1f) * 0.5f;

    // totalRicochetPercent/totalMultishotPercent: no in-run augment grants these yet (RicochetAugment/
    // MultiShotAugment only ever touch the Flat side) — added for future use per the same Bonus/Total split.
    public float Ricochet => (baseRicochet + bonusRicochetFlat * bonusRicochetPercent) * totalRicochetPercent * totalRicochetPenalty;
    public float Multishot => (baseMultishot + bonusMultishotFlat * bonusMultishotPercent) * totalMultishotPercent * totalMultishotPenalty;
    public float MultishotSpreadAngle => baseMultishotSpreadAngle * bonusMultishotSpreadPercent;
    public float ProjectileDamageDropoff => baseProjectileDamageDropoff * projectileDamageDropoffReduction;
    public float ProjectileOnHitDropoff => baseProjectileOnHitDropoff * projectileOnHitDropoffReduction;
    public float FireRate => (baseFireRate + fireRateFlat) * fireRateTotalPercent * fireRateTotalPenalty;
    public float ProjectileSpeed => (baseProjectileSpeed + projectileSpeedFlat) * projectileSpeedTotalPercent * projectileSpeedTotalPenalty;

    // Same Bonus/Total split — totalMaxHealthPercent is the new Total tier HealthAugment (and outlier
    // TitanAugment) grant.
    public float MaxHealth => (baseMaxHealth + bonusMaxHealthFlat * bonusMaxHealthPercent) * totalMaxHealthPercent * totalMaxHealthPenalty;
    // How much of MaxHealth currently comes from bonuses (augments, meta-progression, class passives, etc.)
    // rather than the character's raw baseMaxHealth — e.g. OnHitDamageHealthAugment scales its own damage
    // off this instead of the full MaxHealth, so a character who hasn't picked up any health-boosting
    // bonuses doesn't deal damage off their unmodified starting health. Floored at 0 in case a future
    // penalty tier ever pushes MaxHealth below baseMaxHealth.
    public float BonusHealth => Mathf.Max(0f, MaxHealth - baseMaxHealth);
    // Same split — totalHealthRegenPercent is the new Total tier HealthRegenAugment grants.
    public float HealthRegen => (baseHealthRegen + bonusHealthRegenFlat * bonusHealthRegenPercent) * totalHealthRegenPercent * totalHealthRegenPenalty;

    // Simpler shape than most other stats — just base+bonus (both already "percent of Max Health" units)
    // then the usual Total tier, no separate bonusFlat*bonusPercent multiplier step in between. ShieldAugment
    // is the first in-run augment to grant bonusMaxShieldAdditive; totalMaxShieldPercent/totalMaxShieldPenalty
    // still have no augment-facing caller (AddMaxShieldTotalPercent exists for a future outlier effect).
    public float MaxShield => (baseMaxShieldPercent + bonusMaxShieldAdditive) * totalMaxShieldPercent * totalMaxShieldPenalty * MaxHealth;

    public float MaxEnergy => (baseMaxEnergy + bonusMaxEnergyFlat) * bonusMaxEnergyPercent;
    // Same Bonus/Total split as MoveSpeed/WeaponRange — energyRegenPenalty folded into the Bonus sub-term
    // (ClassPassiveData-only writer), totalEnergyRegenPercent is the new Total tier EnergyRegenAugment grants.
    public float EnergyRegen => (baseEnergyRegen + bonusEnergyRegenFlat * bonusEnergyRegenPercent * energyRegenPenalty) * totalEnergyRegenPercent * totalEnergyRegenPenalty;

    public float SurgeCost => (baseSurgeCost + bonusSurgeCostFlat) * bonusSurgeCostPercent;
    public float SurgeActivationThreshhold => SurgeCost * 5f;
    public float SurgeMoveSpeedMulti => surgeMoveSpeedMulti * surgeMoveSpeedPenalty;
    public float SurgeAttackSpeedMultiplier => surgeAttackSpeedMulti * surgeAttackSpeedPenalty;

    // The actual effective dash haste value DashCooldown's formula uses — exposed separately so UI (or
    // anything else) can display the real number instead of the pre-multiplier raw dashHaste field.
    // dashHaste has no separate base field (see its own declaration comment) — totalDashHastePercent (the
    // new Total tier DashHasteAugment grants) is simply appended as one more multiplier alongside the
    // existing bonusDashHastePercent, rather than restructured into a base/bonus split.
    public float EffectiveDashHaste => dashHaste * bonusDashHastePercent * totalDashHastePercent * totalDashHastePenalty;
    public float DashCooldown => baseDashCooldown / (1f + EffectiveDashHaste / 100f);

    // Same shape as EffectiveDashHaste — whatever future Bursts/Abilities cooldown formula gets built will
    // divide its own base cooldown by (1f + EffectiveAbilityHaste / 100f), same as DashCooldown does.
    public float EffectiveAbilityHaste => abilityHaste * bonusAbilityHastePercent * totalAbilityHastePercent * totalAbilityHastePenalty;

    // Same Bonus/Total split as MoveSpeed/WeaponRange — luckPenalty folded into the Bonus sub-term
    // (ClassPassiveData-only writer), totalLuckPercent is the new Total tier LuckAugment grants.
    public float Luck => (baseLuck + bonusLuckFlat * bonusLuckPercent * luckPenalty) * totalLuckPercent * totalLuckPenalty;
    public float LuckChance => Luck / (Luck + 100f);
    // Both clamped to a minimum of 0 here — the one source every consumer reads from — so no
    // debuff/penalty stacking can ever push either one negative and cause a roll below zero. No upper
    // clamp on the ceiling; an arbitrarily high one is just a bigger reward, not a bug.
    public float LuckFloor   => Mathf.Max(0f, baseLuckFloor * bonusLuckFloorPercent * luckFloorPenalty);
    public float LuckCeiling => Mathf.Max(0f, (baseLuckCeiling + bonusLuckCeilingFlat) * bonusLuckCeilingPercent * luckCeilingPenalty);
    // The floor a roll is actually bounded by once LuckChance has raised it off the raw LuckFloor — same
    // Lerp(rawMin, max, LuckChance) WeaponDamageMath.RollDamage itself uses, just expressed in the
    // [LuckFloor, LuckCeiling] multiplier space instead of actual damage numbers so UI can display it
    // without a damage value on hand. LuckCeiling is untouched here — LuckChance only raises the floor
    // toward the ceiling, it never moves the ceiling itself.
    public float EffectiveLuckFloor => Mathf.Lerp(LuckFloor, LuckCeiling, LuckChance);
    // Clamped to [0,1] here — it's a Mathf.Lerp t-value downstream (WeaponDamageMath.RollDamage), which
    // would clamp it internally anyway, but keeping the source itself sane means anything else that reads
    // Highroll directly later doesn't have to know that.
    public float Highroll => Mathf.Clamp01(baseHighroll * bonusHighrollPercent * highrollPenalty);
    // Derived from WeaponDamageMath.RollDamage's own uniform-roll math: a roll is uniform across
    // [min, max] where min = Lerp(rawMin, max, LuckChance) and the highroll threshold =
    // Lerp(rawMin, max, Highroll) — both lerps share the same [rawMin, max] window, so the "chance the
    // roll lands at/above the threshold" reduces to (1 - Highroll) / (1 - LuckChance), independent of the
    // actual damage/rawMin/max values. Once LuckChance has caught up to (or passed) Highroll, min is
    // already at or above the threshold, so every roll highrolls (100%) — matches RollDamage's own
    // "as luckChance climbs... every roll highrolls" comment. Clamped defensively in case LuckChance ever
    // gets close enough to 1 to make the division blow up past 100%.
    public float HighrollChance => LuckChance >= Highroll ? 1f : Mathf.Clamp01((1f - Highroll) / (1f - LuckChance));
    // Clamped to >= 0 — same reasoning as LuckFloor/LuckCeiling: a highroll should never actively reduce
    // damage below its already-rolled value, worst case is +0%.
    public float HighrollBonus => Mathf.Max(0f, baseHighrollBonus * bonusHighrollBonusPercent * highrollBonusPenalty);

    public float Knockback => baseKnockback * knockbackMultiplier * knockbackPenalty;

    // Same auto-divide as Strength and for the same reason — onHitPower feeds every onhit-triggered
    // effect (OnHitDamageAugment, PhantomBladeOnHitAugment, OnStrikeHealAllyAugment, etc.), so without this
    // an augment that grants more simultaneous weapons/multishot would silently multiply every one of
    // those effects' output too, blowing past the 20/30-point budget guideline those augments were tuned
    // against. bonusOnHitPowerMultiplier is already a genuine multiplier (no base/flat addition to split,
    // unlike most other stats here) — totalOnHitPowerPercent is simply appended as one more independent
    // multiplier per the same Bonus/Total convention.
    public float OnHitPower => baseOnHitPower * bonusOnHitPowerMultiplier * bonusOnHitPowerPenalty * totalOnHitPowerPercent * totalOnHitPowerPenalty
        / (TotalWeaponCount * (1f + Multishot));
    // Same "excludes only the auto-divide" shape as UnpenalizedStrength, for the same reason — a special
    // action (e.g. Rogue Spin Ability) that already snapshots its own effective hit count separately
    // shouldn't have OnHitPower's divide applied twice.
    public float UnpenalizedOnHitPower => baseOnHitPower * bonusOnHitPowerMultiplier * bonusOnHitPowerPenalty * totalOnHitPowerPercent * totalOnHitPowerPenalty;
    public float StackPower => stackPowerPercent * stackPowerPenalty;
    public float AbilityPower => abilityPowerMultiplier * abilityPowerPenalty;
    public float XPModifier => xpMultiplier * xpPenalty;
    public float XPRadius => (baseXPRadius + bonusXPRadiusFlat) * bonusXPRadiusPercent;

    public float ReviveTimePenalty => reviveTimePenalty * reviveTimePenaltyReduction;
    public float DamageReduction => damageReductionPercent * damageReductionPenalty;

    // Simulation state reads (use in Simulate / gameplay logic only)
    public float CurrentHealth => currentState.health;
    public float CurrentEnergy => currentState.energy;
    public float CurrentShield => currentState.shield;
    public bool  IsDead        => currentState.isDead;
    public bool  IsSurging     => currentState.isSurging;
    public bool  IsInvincible  => currentState.invincible;
    public bool  IsTaunting    => currentState.taunting;
    public bool  IsInvisible   => currentState.invisible;
    // Gates weapon hit/damage application only — weapons keep orbiting/spinning visually regardless (see
    // WeaponMeleePD/WeaponAxePD/WeaponRangedPD's own read sites). Currently only set by the spin ability's
    // leap (RogueSpinAbilityPD), but generic/persistent so any future effect can reuse it.
    public bool  IsWeaponsDisabled => currentState.weaponsDisabled;
    // Counter, not a bool — more than one active ability could plausibly hold a block at once (unlike
    // IsWeaponsDisabled, ActiveSurgeBlock isn't gated behind ForcesSpecialAction's single-active-special-
    // action mutex, so e.g. a non-locking ability could theoretically overlap with another). Each holder
    // increments on its own Activate() and decrements on its own end-of-active — see IAbilityPD.
    // ActiveSurgeBlock's own doc comment for the full contract. Surge is still allowed to be already-running
    // and get force-ended by whichever ability actually calls ForceEndSurge() itself; this counter only
    // gates STARTING a new one (see PlayerControllerPD.Simulate()'s own surge-start check).
    public bool  IsSurgeBlocked => currentState.surgeBlockCount > 0;
    public float CurrentReviveTime => currentState.currentReviveTime;

    // UI-facing state (set in UpdateView — safe to read from MonoBehaviour.Update)
    // Health/shield use verified state to avoid prediction-rollback jumps.
    // Energy/surge use viewState since the owner predicts these accurately.
    public float UIHealth              { get; private set; }
    public float UIShield              { get; private set; }
    public float UIShieldRechargeTimer { get; private set; }
    public float UIEnergy              { get; private set; }
    public bool  UIIsSurging           { get; private set; }

    // ── State ──────────────────────────────────────────────────────────────
    public struct State : IPredictedData<State>
    {
        public float health;
        public float energy;
        public float shield;
        public bool isDead;
        public bool isSurging;
        public bool invincible;
        // Enemy-targeting only (2026-07-26, ahead of the Bursts/Abilities Taunt/Invisibility abilities that
        // will actually set these) — see EnemyBasePD.FindClosestTarget for how these are consumed.
        public bool taunting;
        public bool invisible;
        // Weapon hits become purely visual while true — see IsWeaponsDisabled's own doc comment.
        public bool weaponsDisabled;
        // How many currently-active abilities are holding a Surge block right now — see IsSurgeBlocked's
        // own doc comment.
        public int surgeBlockCount;
        // These four are ticks, not seconds — see EnemyBaseState.ContactCooldown.remaining's own doc comment for why
        // (PurrNet's delta-packer costs a full 33 bits for any float that changed since last snapshot; a
        // changed int only costs a cheap zigzag-varint diff). healthRegenTimer is seeded to -1 by
        // GetInitialState() as a "not yet converted" sentinel — see Simulate()'s own comment on it, since
        // GetInitialState() has no delta available yet to convert its seconds-based initial value.
        public int invincibleTimer;
        public int surgeTickTimer;
        public int surgeMinDurationTimer;
        public int colorIndex;
        public int healthRegenTimer;
        public int shieldRechargeTimer;
        public uint damageEventId;
        public float currentReviveTime;
        // Single shared orbit angle for EVERY weapon this player owns (melee, axe, ranged alike) — ticked
        // once here instead of each weapon independently decrementing its own copy. A weapon's own SlotIndex
        // * (360/WeaponCount) offset (see WeaponBasePD.OrbitalPosition) only produces evenly-spaced
        // positions if every weapon reads the exact same base angle; when each weapon tracked its own
        // independently-ticking copy, a newly spawned weapon started fresh at 0 while existing weapons had
        // already drifted from spawn, so the two were never actually 360/count degrees apart — this fixes
        // that by construction, since there's only one angle value for every weapon to ever read.
        // CompressedFloat (PurrNet built-in, 0.001-precision quantized float with implicit float
        // conversions both ways) instead of a plain float — this field changes almost every tick for
        // every living player, so it never earns PurrNet's cheap "unchanged" 1-bit case; CompressedFloat's
        // delta-packer costs an integer diff of its internal rounded value instead of a full 33-bit float
        // rewrite. Wrapped to 0-360 every tick in Simulate() (see that comment) both to keep this tidy and
        // to stop CompressedFloat's internal int accumulator from eventually overflowing over a very long
        // session (int overflows past ~2.1 million accumulated degrees at this precision).
        public CompressedFloat weaponOrbitAngle;
        public void Dispose() { }
    }

    protected override State GetInitialState() => new()
    {
        health = baseMaxHealth,
        energy = baseMaxEnergy,
        shield = 0f,
        // Sentinel meaning "not yet converted from seconds to ticks" — GetInitialState() has no delta
        // available yet. Simulate() converts this to the real tick count exactly once, the first time it
        // ever runs for this character — see its own comment. -1 is safe/unambiguous since the normal
        // countdown logic always reassigns to a valid positive count before a Simulate() call ends, so a
        // legitimately-running countdown can never be negative when read back.
        healthRegenTimer = -1,
        shieldRechargeTimer = 0,
        currentReviveTime = reviveTime
    };

    // ── Events ─────────────────────────────────────────────────────────────
    // Per-instance (not static): each character's events are its own, not a shared global channel.
    // PredictedEvent handles view-safety itself — server always fires, the owner fires once on its
    // own live (non-replay) pass, and non-owners fire once the tick is verified — so callers don't
    // need to reason about replay/ownership at the invoke site.
    private PredictedEvent<float> _onHeal;
    // Fires whenever any hit this player lands highrolls — moved here from being owned by whichever weapon
    // actually landed the hit (see feedback_predictedevent_anchor_persistent_identity memory / the hit-
    // feedback despawn-race fix this mirrors): a weapon can despawn (WeaponManagerPD.DespawnWeapons, e.g.
    // SurgeWeaponCountAugment ending) shortly after landing a hit, and a non-owner client only ever fires a
    // PredictedEvent once its tick verifies, which requires the OWNING identity to still exist to be
    // resimulated — a despawned weapon can never satisfy that. No subscribers yet (currently unused), but
    // any future "on highroll, do X" augment should subscribe here, not to a weapon instance.
    private PredictedEvent<float> _onHighroll;
    private PredictedEvent _onSurgeStarted;
    private PredictedEvent _onSurgeEnded;
    // Fires the tick shield crosses from >0 to <=0 inside TakeDamage — see that method's own 0-crossing
    // check. No payload (matches _onSurgeStarted/_onSurgeEnded) since "which player" is already implicit
    // per-instance and there's no other useful data to carry (overflow damage already went to health in
    // the same call, before this fires).
    private PredictedEvent _onShieldBreak;
    // Fires the tick shield crosses from <=0 back up to >0 — the natural Simulate()-driven regen (see that
    // method's own shield-regen block) and the instant RechargeShield() both funnel through this same
    // detection so "reapply while shielded"-style effects (e.g. ShieldDamageReductionAugment) never need to
    // care which path actually refilled the shield.
    private PredictedEvent _onShieldRecharge;
    // Fires from inside TakeDamage the moment it determines the incoming hit WOULD reduce health to <=0,
    // before that reduction is actually applied — gives a death-prevention effect a chance to call
    // PreventLethalDamage() synchronously (see that method's own doc comment) before TakeDamage finalizes
    // the outcome. No payload — a listener that wants to react conditionally (e.g. a cooldown) tracks that
    // itself; the incoming damage amount isn't needed to decide whether to intervene.
    private PredictedEvent _onLethalDamageTaken;
    // Fires from inside TakeDamage whenever incoming damage actually reduces shield and/or health by a
    // positive amount (post-DamageReduction, before it's split between shield/health) — e.g.
    // PhantomBladeOnDamagedAugment. Excludes a killing blow, same reasoning as _onShieldBreak: a "reacted to
    // taking damage" effect firing on a player who's already dead doesn't make sense.
    private PredictedEvent<float> _onDamaged;
    // Called directly by whichever weapon/projectile actually landed the crit (WeaponMeleePD.Hit(),
    // WeaponAxePD.Hit(), WeaponRangedProjectilePD.ProcessHit()) via NotifyCritStrike — not relayed through
    // WeaponManagerPD or any per-weapon event (see feedback_predictedevent_anchor_persistent_identity memory
    // for why: a weapon can despawn before its own event's verification window closes, silently dropping the
    // notification on other clients). The one channel downstream per-player systems like
    // PhantomBladeOnCritAugment subscribe to, instead of each augment needing to know about individual
    // weapons at all. Deliberately per-instance like every other event here, not static — this is what
    // makes cross-player interaction structurally impossible rather than just filtered against.
    private PredictedEvent<WeaponHitData> _onCritStrike;
    // Same direct-call shape as _onCritStrike, but fires for every weapon hit (crit or not) — e.g.
    // CritTriggerOnHitAugment subscribes to both and calls NotifyHit again from its own crit listener to
    // make a crit count as two hits.
    private PredictedEvent<WeaponHitData> _onHit;
    // Fires once per REAL weapon connection with an enemy — called directly alongside _onHit/_onCritStrike
    // exactly like them, but deliberately never touched by anything that re-invokes _onHit/NotifyHit
    // (e.g. CritTriggerOnHitAugment calling Stats.NotifyHit again from its own crit listener). Effects that
    // must fire exactly once per genuine strike, not once-or-twice depending on other augments (e.g.
    // OnStrikeEnergyRegenAugment), subscribe here instead of to _onHit.
    private PredictedEvent<WeaponHitData> _onStrike;
    // Fires whenever any of this player's weapons touches (not damages) an ally — called directly by each
    // weapon (same direct-call convention as _onHit/_onCritStrike/_onStrike, since "touched an ally"
    // isn't a per-weapon-type concept anything else needs to distinguish). Generic on purpose: multiple
    // "hit ally, do something" augments (OnStrikeHealAllyAugment and future family members) can each
    // subscribe independently and decide what to do with the touch themselves, rather than this class
    // baking in any one specific effect.
    private PredictedEvent<AllyStrikeData> _onAllyStrike;
    // Fires once per "primary" Phantom Blade summon (every SummonAtTarget call whose notifySummon param is
    // left true — i.e. every real trigger: crit, passive tick, dash, onhit), carrying the already-fully-
    // resolved damage/size so a listener like PhantomBladeCarnivalAugment can spawn an exact duplicate
    // without re-rolling luck variance. Never fires for a copy spawned in reaction to this same event —
    // SummonCopyAtTarget doesn't call SummonAtTarget at all, so there's no path back into this event from
    // the copy itself, structurally ruling out recursion rather than needing a guard flag to prevent it.
    private PredictedEvent<PhantomBladeSummonData> _onPhantomBladeSummon;
    // Fire whenever SetTaunting/SetInvisible actually flips the flag (never on a no-op call — see those
    // methods' own guard) — the enemy-targeting hub (EnemySpawnerPD) subscribes to these once per player and
    // fans out to every active enemy, so a taunt/invisibility change reacts immediately instead of enemies
    // needing to poll for it. bool payload is the new value, matching the "which player" being implicit
    // per-instance convention every other event here already uses.
    private PredictedEvent<bool> _onTauntChanged;
    private PredictedEvent<bool> _onInvisibleChanged;
    // Fires the instant an ability's own AbilityIsActive flips true/false (see IAbilityPD.AbilityIsActive's
    // own doc comment) — generic across both slots, for augments that want to react to "an ability started/
    // ended" regardless of which one, same "owned by the persistent CharacterStatsPD, not the (non-transient
    // but still not the natural augment-facing hub) ability component itself" convention as every other
    // augment-facing event here. Payload is the live IAbilityPD reference itself (not just its AbilitySlot)
    // — safe to pass directly, unlike a pooled/transient IEnemyPD, since every ability component is a
    // permanent one-per-character fixture for the whole match (same "pass the live reference" precedent as
    // HitFeedbackData.attacker carrying a live CharacterStatsPD). Lets a listener read Slot, Name,
    // BaseCooldownDuration, or anything else IAbilityPD exposes directly off the payload instead of caching
    // its own GetComponents<IAbilityPD>() lookup just to resolve which instance fired.
    private PredictedEvent<IAbilityPD> _onAbilityStart;
    private PredictedEvent<IAbilityPD> _onAbilityEnd;

    // Owned here rather than by whichever weapon/projectile/blade/augment-effect actually landed the hit —
    // every one of those emitters (WeaponBasePD, WeaponRangedProjectilePD, PhantomBladePD, AugmentEffectPD)
    // is transient (despawns/gets deleted on its own schedule, independent of the hit it just landed),
    // whereas this player's own CharacterStatsPD is persistent for the whole match. PredictedEvent<T> only
    // actually fires for a non-owner, non-server client once the tick becomes verified, which requires the
    // OWNING identity to still exist to be resimulated when that confirmation lands — a hit fired from a
    // since-despawned weapon (e.g. SurgeWeaponCountAugment's temporary extra weapon, or any short-lived
    // projectile/blade) could permanently lose its feedback on every other client if it depended on the
    // weapon's own event instead of this one. Every emitter already carries `attacker` (this identity) in
    // its HitFeedbackData payload, so routing the dispatch itself through here instead costs nothing.
    private PredictedEvent<HitFeedbackData> _onHitFeedback;

    public void AddHealListener(Action<float> listener) => _onHeal.AddListener(listener);
    public void RemoveHealListener(Action<float> listener) => _onHeal.RemoveListener(listener);
    public void AddHighrollListener(Action<float> listener) => _onHighroll.AddListener(listener);
    public void RemoveHighrollListener(Action<float> listener) => _onHighroll.RemoveListener(listener);
    public void NotifyHighroll(float damage) => _onHighroll.Invoke(damage);
    public void AddSurgeStartedListener(Action listener) => _onSurgeStarted.AddListener(listener);
    public void RemoveSurgeStartedListener(Action listener) => _onSurgeStarted.RemoveListener(listener);
    public void AddSurgeEndedListener(Action listener) => _onSurgeEnded.AddListener(listener);
    public void RemoveSurgeEndedListener(Action listener) => _onSurgeEnded.RemoveListener(listener);
    public void AddShieldBreakListener(Action listener) => _onShieldBreak.AddListener(listener);
    public void RemoveShieldBreakListener(Action listener) => _onShieldBreak.RemoveListener(listener);
    public void AddShieldRechargeListener(Action listener) => _onShieldRecharge.AddListener(listener);
    public void RemoveShieldRechargeListener(Action listener) => _onShieldRecharge.RemoveListener(listener);
    public void AddLethalDamageTakenListener(Action listener) => _onLethalDamageTaken.AddListener(listener);
    public void RemoveLethalDamageTakenListener(Action listener) => _onLethalDamageTaken.RemoveListener(listener);
    public void AddDamagedListener(Action<float> listener) => _onDamaged.AddListener(listener);
    public void RemoveDamagedListener(Action<float> listener) => _onDamaged.RemoveListener(listener);
    public void AddCritStrikeListener(Action<WeaponHitData> listener) => _onCritStrike.AddListener(listener);
    public void RemoveCritStrikeListener(Action<WeaponHitData> listener) => _onCritStrike.RemoveListener(listener);
    public void AddTauntChangedListener(Action<bool> listener) => _onTauntChanged.AddListener(listener);
    public void RemoveTauntChangedListener(Action<bool> listener) => _onTauntChanged.RemoveListener(listener);
    public void AddInvisibleChangedListener(Action<bool> listener) => _onInvisibleChanged.AddListener(listener);
    public void RemoveInvisibleChangedListener(Action<bool> listener) => _onInvisibleChanged.RemoveListener(listener);
    public void AddAbilityStartListener(Action<IAbilityPD> listener) => _onAbilityStart.AddListener(listener);
    public void RemoveAbilityStartListener(Action<IAbilityPD> listener) => _onAbilityStart.RemoveListener(listener);
    public void NotifyAbilityStart(IAbilityPD ability) => _onAbilityStart.Invoke(ability);
    public void AddAbilityEndListener(Action<IAbilityPD> listener) => _onAbilityEnd.AddListener(listener);
    public void RemoveAbilityEndListener(Action<IAbilityPD> listener) => _onAbilityEnd.RemoveListener(listener);
    public void NotifyAbilityEnd(IAbilityPD ability) => _onAbilityEnd.Invoke(ability);
    public void NotifyCritStrike(WeaponHitData data) => _onCritStrike.Invoke(data);
    public void AddHitListener(Action<WeaponHitData> listener) => _onHit.AddListener(listener);
    public void RemoveHitListener(Action<WeaponHitData> listener) => _onHit.RemoveListener(listener);
    public void NotifyHit(WeaponHitData data) => _onHit.Invoke(data);
    public void AddStrikeListener(Action<WeaponHitData> listener) => _onStrike.AddListener(listener);
    public void RemoveStrikeListener(Action<WeaponHitData> listener) => _onStrike.RemoveListener(listener);
    // _onStrike itself fires exactly once per real strike (never doubled by CritTriggerOnHitAugment
    // re-invoking _onHit) — NotifyStrike is always called synchronously from within the striking weapon's
    // own LateSimulate(), a deterministic context, same as every other direct currentState-mutating call
    // in this class.
    public void NotifyStrike(WeaponHitData data)
    {
        _onStrike.Invoke(data);
    }
    public void AddAllyStrikeListener(Action<AllyStrikeData> listener) => _onAllyStrike.AddListener(listener);
    public void RemoveAllyStrikeListener(Action<AllyStrikeData> listener) => _onAllyStrike.RemoveListener(listener);
    public void AddPhantomBladeSummonListener(Action<PhantomBladeSummonData> listener) => _onPhantomBladeSummon.AddListener(listener);
    public void RemovePhantomBladeSummonListener(Action<PhantomBladeSummonData> listener) => _onPhantomBladeSummon.RemoveListener(listener);
    public void NotifyPhantomBladeSummon(PhantomBladeSummonData data) => _onPhantomBladeSummon.Invoke(data);

    // No Add/RemoveListener wrappers — nothing outside this class needs to subscribe, it's purely a
    // despawn-safe relay from "whatever landed the hit" to this identity's own TriggerHitFeedback dispatch.
    public void NotifyHitFeedback(HitFeedbackData data) => _onHitFeedback.Invoke(data);

    private void OnHitFeedback(HitFeedbackData data)
    {
        if (data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy))
            enemy.TriggerHitFeedback(data.damage, data.isCrit, data.isHighroll, data.attacker, data.isPriority);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────
    protected override void LateAwake()
    {
        _onHeal          = new PredictedEvent<float>(predictionManager, this);
        _onHighroll      = new PredictedEvent<float>(predictionManager, this);
        _onSurgeStarted  = new PredictedEvent(predictionManager, this);
        _onSurgeEnded    = new PredictedEvent(predictionManager, this);
        _onShieldBreak   = new PredictedEvent(predictionManager, this);
        _onShieldRecharge = new PredictedEvent(predictionManager, this);
        _onLethalDamageTaken = new PredictedEvent(predictionManager, this);
        _onDamaged       = new PredictedEvent<float>(predictionManager, this);
        _onCritStrike    = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onHit           = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onStrike        = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onAllyStrike    = new PredictedEvent<AllyStrikeData>(predictionManager, this);
        _onPhantomBladeSummon = new PredictedEvent<PhantomBladeSummonData>(predictionManager, this);
        _onHitFeedback   = new PredictedEvent<HitFeedbackData>(predictionManager, this);
        _onHitFeedback.AddListener(OnHitFeedback);
        _onTauntChanged     = new PredictedEvent<bool>(predictionManager, this);
        _onInvisibleChanged = new PredictedEvent<bool>(predictionManager, this);
        _onAbilityStart     = new PredictedEvent<IAbilityPD>(predictionManager, this);
        _onAbilityEnd       = new PredictedEvent<IAbilityPD>(predictionManager, this);

        // bonuses applied by PlayerDataPD.Simulate after spawn
        GetComponent<WeaponManagerPD>()?.Init(this);
        // TODO: GetComponent<RogueClassPassive>()?.Init(this);

        UIHealth = baseMaxHealth;
        UIEnergy = baseMaxEnergy;

        if (isOwner && LocalUIManagerPD.instance != null)
            LocalUIManagerPD.instance.SetPlayer(this);
    }

    private const float SurgeTickInterval    = 0.1f;
    private const float SurgeMinDuration     = 0.5f;
    private const float HealthRegenInterval  = 1f;

    // Cached every tick (before the pause early-return, so it's always fresh) — GrantInvincibility(),
    // StartSurge(), and TakeDamage() all set tick-based countdown fields from seconds-based durations, but
    // are called from OTHER identities' Simulate()-context (dash input, weapon Hit(), contact damage),
    // not this one's own, so they don't receive this tick's delta as a parameter directly. Safe to read:
    // delta is a constant tick duration now that nothing manipulates Time.timeScale, so it's identical
    // regardless of exactly when within a tick it's read.
    private float _currentDelta;

    protected override void Simulate(ref State state, float delta)
    {
        _currentDelta = delta;

        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Energy recharges even while dead so the player has energy on revive. Normally suppressed while
        // Surging — EnergyRegenWhileSurgingAugment punches a partial-rate hole in that via
        // energyRegenWhileSurgingPercent (0 = fully suppressed, the default/no-augment case).
        if (!state.isSurging)
            state.energy = Mathf.Min(state.energy + EnergyRegen * delta, MaxEnergy);
        else if (energyRegenWhileSurgingPercent > 0f)
            state.energy = Mathf.Min(state.energy + EnergyRegen * energyRegenWhileSurgingPercent * delta, MaxEnergy);

        if (state.isDead) return;

        // Weapon orbit — same "don't rotate while dead" gating the individual weapons used to apply
        // themselves before this became a shared value. EffectiveWeaponSpeed instead of WeaponSpeed
        // directly so a special action (useSpecialSpinRate) can override the spin rate — see its own doc
        // comment.
        state.weaponOrbitAngle -= EffectiveWeaponSpeed * delta;

        // Wrapped to 0-360 every tick — keeps CompressedFloat's internal int accumulator from eventually
        // overflowing over a very long session, and is safe regardless of magnitude (including a single
        // tick's rotation exceeding a full 360° lap): OrbitalPosition() only ever reads this through
        // Cos/Sin (exactly periodic every 360°), and WeaponMeleePD/WeaponAxePD's own sub-arc sweep
        // reconstruction (startAngle = currentAngle + angleDelta) is recomputed fresh every tick from
        // whatever this wrapped value currently is, so a wrap boundary crossed mid-tick can't corrupt it —
        // confirmed by WeaponBasePD.ComputeSubArcCount() already explicitly capping its effective sweep
        // coverage at 360° for exactly this "more than a full lap in one tick" case.
        float wrappedAngle = state.weaponOrbitAngle % 360f;
        if (wrappedAngle < 0f) wrappedAngle += 360f;
        state.weaponOrbitAngle = wrappedAngle;

        // Invincibility timer
        if (state.invincible && state.invincibleTimer > 0)
        {
            state.invincibleTimer--;
            if (state.invincibleTimer <= 0)
                state.invincible = false;
        }

        // One-time sentinel conversion — see GetInitialState()'s own comment. Only ever true on the very
        // first Simulate() call this character's whole life; healthRegenTimer is never negative again
        // after this, since the block below always reassigns it to a valid positive count before ending.
        if (state.healthRegenTimer < 0)
            state.healthRegenTimer = Mathf.Max(1, Mathf.RoundToInt(HealthRegenInterval / delta));

        // Health regen
        if (state.health < MaxHealth)
        {
            state.healthRegenTimer--;
            if (state.healthRegenTimer <= 0)
            {
                float healed = Mathf.Min(HealthRegen, MaxHealth - state.health);
                state.health += healed;
                state.healthRegenTimer = Mathf.Max(1, Mathf.RoundToInt(HealthRegenInterval / delta));
                if (healed > 0f) _onHeal.Invoke(healed);
            }
        }

        // Shield regen
        if (state.shield < MaxShield)
        {
            state.shieldRechargeTimer--;
            if (state.shieldRechargeTimer <= 0)
            {
                bool wasBroken = state.shield <= 0f;
                state.shield = MaxShield;
                // Safe to invoke directly from Simulate() — PredictedEvent fires exactly once per real
                // occurrence regardless of resimulation, same reasoning as _onHighroll being invoked
                // directly from weapon Hit() methods.
                if (wasBroken && state.shield > 0f)
                    _onShieldRecharge.Invoke();
            }
        }

        // Surge energy drain (can't be surging while dead, so this block is alive-only)
        if (state.isSurging)
        {
            if (state.surgeMinDurationTimer > 0)
                state.surgeMinDurationTimer--;

            state.surgeTickTimer--;
            if (state.surgeTickTimer <= 0)
            {
                state.surgeTickTimer = Mathf.Max(1, Mathf.RoundToInt(SurgeTickInterval / delta));
                state.energy -= SurgeCost;
                if (state.energy <= 0f)
                {
                    state.energy = 0f;
                    state.isSurging = false;
                    _onSurgeEnded.Invoke();
                }
            }
        }
    }

    private uint _lastDamageEventId;

    protected override void UpdateView(State viewState, State? verified)
    {
        // Health/shield use verified state, not viewState: viewState is the predicted/interpolated
        // value, which visibly snaps every time a rollback correction lands. Verified state only
        // moves forward once confirmed, so displaying it skips the correction jitter entirely.
        if (verified.HasValue)
        {
            UIHealth              = verified.Value.health;
            UIShield              = verified.Value.shield;
            // Converted back to seconds — UIShieldRechargeTimer is a UI-facing float consumed by
            // LocalUIManagerPD's fill-ratio math alongside shieldRechargeTime (also seconds), so it needs
            // to stay in seconds even though the underlying predicted field is ticks now.
            UIShieldRechargeTimer = verified.Value.shieldRechargeTimer * _currentDelta;
        }
        UIEnergy              = viewState.energy;
        UIIsSurging           = viewState.isSurging;

        if (isOwner && viewState.damageEventId != _lastDamageEventId)
        {
            _lastDamageEventId = viewState.damageEventId;
            if (SFXManager.instance != null) SFXManager.instance.Play(_damagedSfx, transform.position);
        }

        if (_invincibilityShell != null)
            _invincibilityShell.SetActive(viewState.invincible);

        // Driven generically off the invisible flag itself, not any specific ability — any current or
        // future invisibility source gets this same visual fade for free. Guarded on an actual change so
        // this doesn't touch the renderer every single frame. MaterialPropertyBlock, not a material
        // instance — avoids leaking a new Material per player, and the base material is already Transparent
        // surface type (see PlayerColorApplicator-adjacent art setup) so alpha actually has a visual effect.
        // RGB comes from PlayerColor, not a hardcoded white — PlayerColorApplicator tints this same
        // renderer's material directly (not via a property block), so overriding _BaseColor here must keep
        // that tint's RGB and only change alpha, or invisibility would blank the model back to white.
        if (_modelRenderer != null && viewState.invisible != _lastAppliedInvisible)
        {
            _lastAppliedInvisible = viewState.invisible;
            _modelPropertyBlock ??= new MaterialPropertyBlock();
            _modelRenderer.GetPropertyBlock(_modelPropertyBlock);
            Color c = PlayerColor;
            c.a = viewState.invisible ? invisibleModelAlpha : 1f;
            _modelPropertyBlock.SetColor(BaseColorId, c);
            _modelRenderer.SetPropertyBlock(_modelPropertyBlock);
        }

        // Continuous idle spin, always on — see _modelTransform's own doc comment for why the sideways tip
        // (tip * spin, not spin * tip) turns this same Y-axis spin into a tumble once tipped, instead of
        // needing separate spin logic for the burst case.
        if (_modelTransform != null)
        {
            Quaternion spin   = Quaternion.Euler(0f, -viewState.weaponOrbitAngle, 0f);
            Quaternion tip    = _modelSidewaysTipped ? ModelSidewaysTipRotation : Quaternion.identity;
            // Outermost — the body faces OrbitFacingYaw (e.g. the burst's travel direction) while the
            // tip/spin tumble happens relative to that facing, not a fixed world orientation.
            Quaternion facing = Quaternion.Euler(0f, OrbitFacingYaw, 0f);
            _modelTransform.localRotation = facing * tip * spin;
        }
    }

    private bool _lastAppliedInvisible;
    // Not constructed inline (a field initializer runs as part of the implicit constructor, and Unity
    // disallows MaterialPropertyBlock's own constructor there) — lazily created on first use instead.
    private MaterialPropertyBlock _modelPropertyBlock;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    protected override void Destroyed()
    {
        if (owner.HasValue)
            All.Remove(owner.Value);
    }

    // ── Init ──────────────────────────────────────────────────────────────
    public struct ProgressionBonuses
    {
        public float strengthFlat, moveSpeedFlat, attackSpeedPct, energyRegenFlat;
        public float dashHaste, critChanceFlat, xpRadiusFlat, weaponRangeFlat, maxEnergyFlat;
        public int   dashChargesBonus, levelUpOptionsBonus;
        public float revivePower, reviveTimePenaltyMult, xpMultiplier;
        public float maxHealthFlat, healthRegenFlat, strengthPct;
        public float luckFlat, strengthPerLevel, maxShieldFlat, shieldRechargeTime;
    }

    public static ProgressionBonuses ComputeProgressionBonuses() => new()
    {
        strengthFlat            = ProgressionManager.GetBaseStatBonus(StatType.Strength),
        moveSpeedFlat           = ProgressionManager.GetBaseStatBonus(StatType.MoveSpeed),
        attackSpeedPct          = ProgressionManager.GetBaseStatBonus(StatType.AttackSpeed) * 0.01f,
        energyRegenFlat         = ProgressionManager.GetBaseStatBonus(StatType.EnergyRegen),
        dashHaste               = ProgressionManager.GetBaseStatBonus(StatType.DashHaste),
        critChanceFlat          = ProgressionManager.GetBaseStatBonus(StatType.CritChance) * 0.01f,
        xpRadiusFlat            = ProgressionManager.GetBaseStatBonus(StatType.CollectionRadius),
        weaponRangeFlat         = ProgressionManager.GetBaseStatBonus(StatType.WeaponRange),
        maxEnergyFlat           = ProgressionManager.GetBaseStatBonus(StatType.MaxEnergy),
        dashChargesBonus        = (int)ProgressionManager.GetBaseStatBonus(StatType.DashCharges),
        levelUpOptionsBonus     = (int)ProgressionManager.GetBaseStatBonus(StatType.LevelUpOptions),
        revivePower             = ProgressionManager.GetBaseStatBonus(StatType.RevivePower),
        reviveTimePenaltyMult   = Mathf.Pow(0.75f, ProgressionManager.GetBaseStatTier(StatType.ReviveTimePenalty)),
        xpMultiplier            = ProgressionManager.GetBaseStatBonus(StatType.XPMultiplier) * 0.01f,
        maxHealthFlat           = ProgressionManager.GetBaseStatBonus(StatType.MaxHealth),
        healthRegenFlat         = ProgressionManager.GetBaseStatBonus(StatType.HealthRegen),
        strengthPct             = ProgressionManager.GetBaseStatBonus(StatType.StrengthPercent),
        luckFlat                = ProgressionManager.GetBaseStatBonus(StatType.Luck),
        strengthPerLevel        = ProgressionManager.GetBaseStatBonus(StatType.StrengthPerLevel),
        maxShieldFlat           = ProgressionManager.GetBaseStatBonus(StatType.MaxShield),
        shieldRechargeTime      = ProgressionManager.GetBaseStatBonus(StatType.ShieldRechargeTime),
    };

    // Wraps any stat mutation that might raise MaxHealth/MaxEnergy, and carries current resources up
    // by the same delta so the ceiling never rises while current stays pinned below it.
    // Shield is deliberately excluded from that same delta-catch-up treatment — it doesn't gradually regen
    // like health/energy, it snaps to full after a no-damage timer, so a max-shield INCREASE should only
    // raise that ceiling and let the existing regen naturally catch current shield up to it. A DECREASE is
    // different though: since MaxShield is itself derived from MaxHealth (see its own comment), any effect
    // that lowers MaxHealth (or directly lowers a Shield stat) can leave current shield sitting above the
    // new, lower ceiling — clamped down below, same "pull current down to the new max" intent as
    // KnightBigBuffAbilityPD's own ClampHealthToMax(), just built in here generically instead of needing
    // every such caller to remember its own explicit clamp.
    // Every future stat-granting effect (augments, etc.) should route through this rather than
    // mutating bonus fields directly, so refill-on-max-increase stays a single, shared behavior.
    private void ApplyStatChange(Action mutate)
    {
        float maxHealthBefore = MaxHealth;
        float maxEnergyBefore = MaxEnergy;

        mutate();

        // Floored rather than left to go negative, in case a future effect ever decreases a max stat.
        // Health floors at 1 so a max decrease alone can never be a kill; energy can bottom out at 0.
        currentState.health = Mathf.Max(1f, currentState.health + (MaxHealth - maxHealthBefore));
        currentState.energy = Mathf.Max(0f, currentState.energy + (MaxEnergy - maxEnergyBefore));
        if (currentState.shield > MaxShield) currentState.shield = MaxShield;
    }

    // Public entry point for augment effect components (e.g. HealthAugment) to grant a flat max health
    // bonus — routes through ApplyStatChange so current health rises with the new ceiling instead of
    // staying pinned below it. Augment effects apply identically on every machine (unlike the old
    // owner-only model), so this needs no isOwner gating here.
    public void AddMaxHealthFlat(float amount) => ApplyStatChange(() => bonusMaxHealthFlat += amount);

    // Same pattern as AddMaxHealthFlat, for augments that grant a percentage bonus instead (additive
    // against the 1f baseline, e.g. +0.2 = +20% total). Bonus tier — now only scales bonusMaxHealthFlat
    // (see MaxHealth), not the whole stat; kept for ClassPassiveData/meta-progression and any future
    // "outlier" augment, but HealthAugment itself now calls AddMaxHealthTotalPercent below instead.
    public void AddMaxHealthPercent(float amount) => ApplyStatChange(() => bonusMaxHealthPercent += amount);
    // NEW Total tier — scales the whole (base + bonus) MaxHealth sum. What HealthAugment (and TitanAugment,
    // redirected alongside it since both grant MaxHealth) actually calls now.
    public void AddMaxHealthTotalPercent(float amount) => ApplyStatChange(() => totalMaxHealthPercent += amount);
    public void AddWeaponRangeFlat(float amount) => ApplyStatChange(() => bonusWeaponRangeFlat += amount);
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddWeaponRangePercent(float amount) => ApplyStatChange(() => bonusWeaponRangePercent += amount);
    // NEW Total tier — what RangeAugment/OuterTrackAugment/TitanAugment all call now (every existing
    // WeaponRange-percent-granting augment, redirected together since they all target the same stat).
    public void AddWeaponRangeTotalPercent(float amount) => ApplyStatChange(() => totalWeaponRangePercent += amount);
    public void AddAttackSpeedTotalPercent(float amount) => ApplyStatChange(() => attackSpeedTotalPercent += amount);
    // attackSpeedFlat (renamed from attackSpeedPercent) is added directly in the same units as
    // baseAttackSpeed, not a raw fraction — matches the progression system's own conversion
    // (entry.value * 0.01f * baseAttackSpeed) so a caller here can pass an intuitive fraction (e.g. 0.25 for
    // +25%) same as every other percent Add, even though what it grants under the hood is flat.
    public void AddAttackSpeedFlat(float amount) => ApplyStatChange(() => attackSpeedFlat += amount * baseAttackSpeed);
    public void AddMoveSpeedFlat(float amount) => ApplyStatChange(() => bonusMoveSpeedFlat += amount);
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddMoveSpeedPercent(float amount) => ApplyStatChange(() => bonusMoveSpeedPercent += amount);
    // NEW Total tier — what MoveSpeedAugment/GremlinAugment/ShieldBreakFrenzyAugment/SurgeEndMoveSpeedAugment
    // all call now (every existing MoveSpeed-percent-granting augment, redirected together).
    public void AddMoveSpeedTotalPercent(float amount) => ApplyStatChange(() => totalMoveSpeedPercent += amount);
    // Direct override, not additive — surgeMoveSpeedPenalty (default 0.5f) is the actual reduction
    // multiplier itself (SurgeMoveSpeedMulti = surgeMoveSpeedMulti * surgeMoveSpeedPenalty * ...), not a
    // bonus-stacking pool, so "half the penalty"/"no penalty" augments each set it to their own fixed
    // absolute target (0.75f/1f) rather than accumulating a delta off the current value. This also makes
    // picking both of a mutually-exclusive pair harmless (whichever applies last simply wins) rather than
    // compounding into a nonsensical value.
    public void SetSurgeMoveSpeedPenalty(float value) => ApplyStatChange(() => surgeMoveSpeedPenalty = value);
    // "Additive against the 1f baseline" pattern, same as AddCritChancePercent — stackPowerPercent starts at
    // 1f (StackPower = stackPowerPercent * stackPowerPenalty), just had no augment-facing wrapper until now.
    public void AddStackPowerPercent(float amount) => ApplyStatChange(() => stackPowerPercent += amount);
    // Same "additive against the 1f baseline" convention as AddStackPowerPercent.
    public void AddAbilityPowerPercent(float amount) => ApplyStatChange(() => abilityPowerMultiplier += amount);
    // Multiplicative compounding, same convention as MultiplyStrengthPenalty/MultiplyOnHitPowerPenalty —
    // multiple penalty sources compound together rather than summing.
    public void MultiplyAbilityPowerPenalty(float factor) => ApplyStatChange(() => abilityPowerPenalty *= factor);
    // Plain +=, not ApplyStatChange — same reasoning as AddPhantomBladeDamageBonusPercent: doesn't affect
    // Max Health/Energy, so the before/after clamp recompute ApplyStatChange does would be pure overhead.
    public void AddAreaEffectSizePercent(float amount) => areaEffectSizePercent += amount;
    public void AddAreaEffectPowerPercent(float amount) => areaEffectPowerPercent += amount;
    // Additive-into-1f, same best-guess convention ClassPassiveData's own doc comment uses for every other
    // Penalty field (none have an established combination rule yet — this is consistent with that, not a
    // confirmed pattern).
    public void AddAreaEffectSizePenalty(float amount) => areaEffectSizePenalty += amount;
    public void AddAreaEffectPowerPenalty(float amount) => areaEffectPowerPenalty += amount;
    public void AddAreaEffectCount(int amount) => areaEffectCountFlat += amount;
    // Multiply/Divide pair, not additive — matches bonusWeaponCountMultiplier's own int-multiplier shape
    // (WeaponCount => (base + flat) * multiplier), the closest existing precedent for a discrete-count
    // penalty rather than a continuous float one.
    public void MultiplyAreaEffectCountPenalty(int factor) => areaEffectCountPenalty *= factor;
    public void DivideAreaEffectCountPenalty(int factor) => areaEffectCountPenalty /= factor;
    // Plain +=, not ApplyStatChange — same reasoning as AddAreaEffectSizePercent: doesn't affect Max
    // Health/Energy, so the before/after clamp recompute ApplyStatChange does would be pure overhead.
    public void AddOuterXZMultiplierPercent(float amount) => outerXZMultiplierPercent += amount;
    public void AddHealthRegenFlat(float amount) => ApplyStatChange(() => bonusHealthRegenFlat += amount);
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddHealthRegenPercent(float amount) => ApplyStatChange(() => bonusHealthRegenPercent += amount);
    // NEW Total tier — what HealthRegenAugment calls now.
    public void AddHealthRegenTotalPercent(float amount) => ApplyStatChange(() => totalHealthRegenPercent += amount);
    public void AddEnergyRegenFlat(float amount) => ApplyStatChange(() => bonusEnergyRegenFlat += amount);
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddEnergyRegenPercent(float amount) => ApplyStatChange(() => bonusEnergyRegenPercent += amount);
    // NEW Total tier — what EnergyRegenAugment calls now.
    public void AddEnergyRegenTotalPercent(float amount) => ApplyStatChange(() => totalEnergyRegenPercent += amount);
    public void AddLuckFlat(float amount) => ApplyStatChange(() => bonusLuckFlat += amount);
    // Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment calls this anymore.
    public void AddLuckPercent(float amount) => ApplyStatChange(() => bonusLuckPercent += amount);
    // NEW Total tier — what LuckAugment calls now.
    public void AddLuckTotalPercent(float amount) => ApplyStatChange(() => totalLuckPercent += amount);
    // Directly additive against the LuckCeiling number itself (base 1.5 + 0.25 = 1.75), same
    // (base + Flat) * Percent * Penalty shape as Strength — NOT AddLuckCeilingPercent below, which
    // instead scales bonusLuckCeilingPercent's own 1f-baseline multiplier and would compound against
    // baseLuckCeiling (1.5 * 1.25 = 1.875) rather than adding to it directly.
    public void AddLuckCeilingFlat(float amount) => ApplyStatChange(() => bonusLuckCeilingFlat += amount);
    // "Additive against the 1f baseline" pattern, same as AddCritChancePercent — bonusLuckCeilingPercent
    // starts at 1f (LuckCeiling = (baseLuckCeiling + bonusLuckCeilingFlat) * bonusLuckCeilingPercent *
    // luckCeilingPenalty), just had no augment-facing wrapper until now.
    public void AddLuckCeilingPercent(float amount) => ApplyStatChange(() => bonusLuckCeilingPercent += amount);
    // Inverted, same reasoning as AddDamageReductionPercent: Highroll = baseHighroll * bonusHighrollPercent
    // * highrollPenalty, and a LOWER Highroll threshold means MORE of a roll's window counts as a highroll
    // (see WeaponDamageMath.RollDamage's own doc comment) — so a lower highrollPenalty is better for the
    // player here. The parameter keeps this class's usual "positive = better for the player" convention
    // (e.g. 0.05 for a 5% easier highroll threshold); it's SUBTRACTED under the hood.
    public void AddHighrollPenalty(float amount) => ApplyStatChange(() => highrollPenalty -= amount);
    public void AddCritChanceFlat(float amount) => ApplyStatChange(() => bonusCritChanceFlat += amount);
    // Same "additive against the 1f baseline" pattern as AddMaxHealthPercent, for CritChance's own percent
    // multiplier. Bonus tier — see AddMaxHealthPercent's own comment; kept for ClassPassiveData, no augment
    // calls this anymore (CritChanceAugment, née CritAugment, now calls AddCritChanceTotalPercent below).
    public void AddCritChancePercent(float amount) => ApplyStatChange(() => bonusCritChancePercent += amount);
    // NEW Total tier — what CritChanceAugment calls now.
    public void AddCritChanceTotalPercent(float amount) => ApplyStatChange(() => totalCritChancePercent += amount);
    // Flat addition to CritDamage's own baseCritDamage term (CritDamage = (baseCritDamage +
    // bonusCritDamageFlat) * bonusCritDamagePercent + ...), e.g. +0.1 for +10% crit damage.
    public void AddCritDamageFlat(float amount) => ApplyStatChange(() => bonusCritDamageFlat += amount);
    // Inverted from every other "...Percent" wrapper above: damageReductionPercent starts at 1f and
    // multiplies DIRECTLY into incoming damage (TakeDamage's own `damage *= DamageReduction`), so a LOWER
    // value is better for the player here. The parameter still follows this class's usual "positive =
    // better for the player" convention (e.g. 0.4 for -40% damage taken) — it's SUBTRACTED under the hood so
    // the caller-facing sign stays consistent with every other Add*Percent method; pass a negative amount to
    // revert a previously-granted reduction.
    public void AddDamageReductionPercent(float amount) => ApplyStatChange(() => damageReductionPercent -= amount);
    public void AddDashHaste(float amount) => ApplyStatChange(() => dashHaste += amount);
    // Same "additive against the 1f baseline" pattern as AddMaxHealthPercent/AddCritChancePercent, for
    // DashHaste's own percent multiplier (bonusDashHastePercent starts at 1f, e.g. +0.15 = +15% total).
    // No other writer ever touched this field (see its own declaration comment) — DashHasteAugment now
    // calls AddDashHasteTotalPercent below instead; kept here only in case something else wants the
    // (functionally identical, since dashHaste has no separate base) Bonus-tier slot later.
    public void AddDashHastePercent(float amount) => ApplyStatChange(() => bonusDashHastePercent += amount);
    // NEW Total tier — what DashHasteAugment calls now.
    public void AddDashHasteTotalPercent(float amount) => ApplyStatChange(() => totalDashHastePercent += amount);
    // Same shape as the three DashHaste wrappers immediately above.
    public void AddAbilityHaste(float amount) => ApplyStatChange(() => abilityHaste += amount);
    public void AddAbilityHastePercent(float amount) => ApplyStatChange(() => bonusAbilityHastePercent += amount);
    public void AddAbilityHasteTotalPercent(float amount) => ApplyStatChange(() => totalAbilityHastePercent += amount);
    public void AddWeaponCountFlat(int amount) => ApplyStatChange(() => bonusWeaponCountFlat += amount);
    // Multiplicative compounding, same convention as MultiplyStrengthPenalty/MultiplyOnHitPowerMultiplier
    // — bonusWeaponCountMultiplier already sits in the WeaponCount formula (previously only ever touched by
    // ClassPassiveData aggregation), just had no augment-facing wrapper until now. factor is an exact
    // integer multiplier (e.g. 2 for SurgeWeaponCountAugment's x2), inverted with the same factor to revert
    // (integer *=/ /= pairs cleanly as long as they're applied/removed in matched pairs).
    public void MultiplyWeaponCountMultiplier(int factor) => ApplyStatChange(() => weaponCountMultiplier *= factor);
    // Paired revert for the above — an int can't be un-multiplied via 1f/factor the way the float
    // Multiply___ methods revert, so this divides back by the same exact factor instead. Only safe when
    // applied/removed in matched pairs with no other multiplicative change interleaved in between.
    public void DivideWeaponCountMultiplier(int factor) => ApplyStatChange(() => weaponCountMultiplier /= factor);
    // Unused for now — the planned OuterWeaponTrackAugment will call this. amount can be negative to
    // remove tracks (nothing currently does, but the direct += supports it symmetrically for free).
    public void AddWeaponTracks(int amount) => ApplyStatChange(() => weaponTracks += amount);
    // Multiplicative, not additive — bonusStrengthPenalty/bonusOnHitPowerPenalty both start at 1f and
    // apply as straight multipliers (Strength/OnHitPower), so stacking two -40% penalties correctly
    // compounds to 0.6 * 0.6 = 0.36 rather than clamping at 1 - 0.8 = 0.2. factor is what the penalty gets
    // multiplied by (e.g. 1f - 0.4f for a -40% penalty), matching the old system's identical
    // `bonusStrengthPenalty *= 1f - damagePenalty` call.
    public void MultiplyStrengthPenalty(float factor) => ApplyStatChange(() => bonusStrengthPenalty *= factor);
    public void MultiplyOnHitPowerPenalty(float factor) => ApplyStatChange(() => bonusOnHitPowerPenalty *= factor);
    // "Additive against the 1f baseline" pattern, same as AddCritChancePercent — bonusStrengthPercent
    // already sits in the Strength formula. Bonus tier — now only scales bonusStrengthFlat (see
    // Strength), not the whole stat; kept for ClassPassiveData/meta-progression. The weapon-count/
    // multishot/track "compensate with a damage bonus" augments below now call AddStrengthTotalPercent
    // instead (redirected together since they all target the same stat being restructured here).
    public void AddStrengthPercent(float amount) => ApplyStatChange(() => bonusStrengthPercent += amount);
    // NEW Total tier — what BladeweaviestBladeweaverAugment/MultiShotAugment/OuterTrackAugment/
    // SurgeWeaponCountAugment/WeaponCountAugment all call now, so their "extra copies, compensate with a
    // damage bonus" grant keeps scaling the whole stat instead of only the (usually-zero) Bonus sub-term.
    public void AddStrengthTotalPercent(float amount) => ApplyStatChange(() => totalStrengthPercent += amount);
    // Multiplicative compounding, same convention as MultiplyStrengthPenalty/MultiplyOnHitPowerPenalty
    // — bonusOnHitPowerMultiplier already sits in the OnHitPower formula (previously only ever touched by
    // ClassPassiveData aggregation), just had no augment-facing wrapper until now. Same
    // penalty-to-bonus swap as AddStrengthPercent above, for the same reason.
    public void MultiplyOnHitPowerMultiplier(float factor) => ApplyStatChange(() => bonusOnHitPowerMultiplier *= factor);
    public void AddRicochetFlat(float amount) => ApplyStatChange(() => bonusRicochetFlat += amount);
    // NEW Total tier — no augment grants this yet (RicochetAugment only ever grants Flat), reserved for a
    // future in-run Ricochet augment.
    public void AddRicochetTotalPercent(float amount) => ApplyStatChange(() => totalRicochetPercent += amount);
    public void AddMultishotFlat(float amount) => ApplyStatChange(() => bonusMultishotFlat += amount);
    // NEW Total tier — no augment grants this yet (MultiShotAugment only ever grants Flat), reserved for a
    // future in-run Multishot augment.
    public void AddMultishotTotalPercent(float amount) => ApplyStatChange(() => totalMultishotPercent += amount);
    public void AddFireRateFlat(float amount) => ApplyStatChange(() => fireRateFlat += amount);
    public void AddFireRateTotalPercent(float amount) => ApplyStatChange(() => fireRateTotalPercent += amount);
    public void AddProjectileSpeedFlat(float amount) => ApplyStatChange(() => projectileSpeedFlat += amount);
    public void AddProjectileSpeedTotalPercent(float amount) => ApplyStatChange(() => projectileSpeedTotalPercent += amount);
    public void AddWeaponRadiusFlat(float amount) => ApplyStatChange(() => bonusWeaponRadiusFlat += amount);
    // NEW Total tier — no augment grants WeaponRadius percent yet (only ClassPassiveData touches
    // bonusWeaponRadiusPercent) — reserved for a future in-run augment on this stat.
    public void AddWeaponRadiusTotalPercent(float amount) => ApplyStatChange(() => totalWeaponRadiusPercent += amount);
    // Total tier — no in-run augment grants this yet (ShieldAugment grants the Bonus tier below instead),
    // reserved for a future outlier effect.
    public void AddMaxShieldTotalPercent(float amount) => ApplyStatChange(() => totalMaxShieldPercent += amount);
    // Bonus tier — first augment-facing entry point for MaxShield (ShieldAugment). Adds directly against
    // baseMaxShieldPercent, both in the same "percent of Max Health" units, e.g. +0.2 = +20% Max Health as
    // shield — see MaxShield's own comment for why there's no separate bonusFlat*bonusPercent multiplier
    // step here, unlike most other stats. Routes through ApplyStatChange per that method's own "every future
    // stat-granting effect should route through this" convention, even though shield isn't part of the
    // health/energy catch-up logic it performs (see ApplyStatChange's own comment) — the ceiling still rises
    // immediately, current shield just naturally catches up via its own regen/no-damage-timer snap instead
    // of being bumped here.
    public void AddMaxShieldPercent(float amount) => ApplyStatChange(() => bonusMaxShieldAdditive += amount);
    // Total tier — what the weapon-count/multishot/track "compensate" augments (BladeweaviestBladeweaverAugment,
    // MultiShotAugment, OuterTrackAugment, SurgeWeaponCountAugment, WeaponCountAugment) all call now, same
    // Total-tier treatment as their Strength grant.
    public void AddOnHitPowerTotalPercent(float amount) => ApplyStatChange(() => totalOnHitPowerPercent += amount);
    // Same MultiplyStrengthPenalty-style compounding convention — multiple spread-reducing sources
    // multiply together rather than summing (two -80% reductions compound to 0.2 * 0.2 = 0.04, not clamp
    // at 1 - 1.6). factor is what bonusMultishotSpreadPercent gets multiplied by (e.g. 1f - 0.8f for
    // MultiShotSpreadAugment's -80% reduction).
    public void MultiplyMultishotSpreadPercent(float factor) => ApplyStatChange(() => bonusMultishotSpreadPercent *= factor);
    // "Additive against the 1f baseline" pattern, same as AddCritChancePercent — knockbackMultiplier starts
    // at 1f (also aggregated multiplicatively by ClassPassiveData separately), e.g. +0.5 = +50% knockback.
    public void AddKnockbackPercent(float amount) => ApplyStatChange(() => knockbackMultiplier += amount);

    // Shared "stacking bonus" scaffold for the whole Phantom Blade augment family — each Phantom Blade
    // augment the player has picked contributes its own delta here (matching old system's per-augment
    // OnSummon handlers, e.g. PhantomBladeOnCritAugment/PhantomBladePassiveAugment both doing
    // `blade.damage += blade.baseDamage * bladeDamageBonus`), and every blade reads the combined total
    // once at spawn time rather than each blade needing to know which augments exist. Plain += (not
    // ApplyStatChange) — doesn't affect Max Health/Energy, and nothing else reads these live the way
    // Strength/MoveSpeed etc. do.
    [HideInInspector] public float phantomBladeDamageBonus = 0f;
    [HideInInspector] public float phantomBladeSizeBonus = 0f;
    // Second, separate multiplicative layer for each of the two above — PhantomBladeSizeAugment's own
    // damage/size bonuses are meant to scale the blade's ALREADY-combined additive total (every other
    // Phantom Blade augment's own +damage%/+size% summed together), not just join those same additive
    // pools. Both start at 1f and are added to directly, same "additive against the 1f baseline"
    // convention as bonusMaxHealthPercent/bonusCritChancePercent (+0.5 = +50% on top of the additive
    // total). PhantomBladeSummonHelper applies each pair as
    // `(1 + phantomBladeDamageBonus) * phantomBladeDamageBonusPercent` and
    // `(1 + phantomBladeSizeBonus) * phantomBladeSizeBonusPercent`.
    [HideInInspector] public float phantomBladeDamageBonusPercent = 1f;
    [HideInInspector] public float phantomBladeSizeBonusPercent = 1f;
    // Third layer, multiplicative like the "Percent" pair above but for PENALTIES rather than bonuses —
    // e.g. PhantomBladeCarnivalAugment's own -35% total damage/size tradeoff for its extra-blade-per-summon
    // effect. Same MultiplyStrengthPenalty-style compounding convention as bonusStrengthPenalty
    // (multiple penalty sources compound multiplicatively rather than summing, so two -35% penalties would
    // compound to 0.65 * 0.65 = 0.4225, not clamp at 1 - 0.7 = 0.3). Plain *= (not ApplyStatChange), same
    // reasoning as the rest of this scaffold.
    [HideInInspector] public float phantomBladeDamagePenalty = 1f;
    [HideInInspector] public float phantomBladeSizePenalty = 1f;
    // False by default — Phantom Blades don't trigger OnHit at all unless PhantomBladeOnHitAugment (or a
    // future family member) turns it on. Kept as its own bool rather than gating off
    // phantomBladeOnHitPowerPercent > 0 — that would conflate "how strong is the onhit trigger" (a
    // magnitude) with "does this blade attempt to trigger onhit at all" (a binary capability), which
    // would silently break if anything ever reduced the percent without meaning to disable the feature.
    // Both are read once at Summon() time like the other two, baked into that blade's lifetime rather
    // than reacting live to later picks.
    [HideInInspector] public bool  phantomBladeOnHitEnabled = false;
    [HideInInspector] public float phantomBladeOnHitPowerPercent = 0f;
    public void AddPhantomBladeDamageBonus(float amount) => phantomBladeDamageBonus += amount;
    public void AddPhantomBladeSizeBonus(float amount) => phantomBladeSizeBonus += amount;
    public void AddPhantomBladeDamageBonusPercent(float amount) => phantomBladeDamageBonusPercent += amount;
    public void AddPhantomBladeSizeBonusPercent(float amount) => phantomBladeSizeBonusPercent += amount;
    public void MultiplyPhantomBladeDamagePenalty(float factor) => phantomBladeDamagePenalty *= factor;
    public void MultiplyPhantomBladeSizePenalty(float factor) => phantomBladeSizePenalty *= factor;
    public void EnablePhantomBladeOnHit() => phantomBladeOnHitEnabled = true;
    public void AddPhantomBladeOnHitPowerPercent(float amount) => phantomBladeOnHitPowerPercent += amount;

    // Same enable-bool shape as phantomBladeOnHitEnabled above, but generic (no attached magnitude here) —
    // weapons only pay for the extra ally-hurtbox overlap query when this (or allyStrikeDamageEnabled
    // below) is true, and ANY "hit ally, do something" augment (OnStrikeHealAllyAugment, future family
    // members) turns it on the same way via EnableAllyStrikeHit(). Multiple such augments picked at once
    // just each call it — idempotent, no conflict — and all react independently to the same underlying
    // _onAllyStrike notifications below. Consolidated from the old system's canContactAllies (same role,
    // dormant/unused in this project until now).
    [HideInInspector] public bool allyStrikeHitEnabled = false;
    public void EnableAllyStrikeHit() => allyStrikeHitEnabled = true;

    // Read directly by EnemyBasePD.Die() (Simulate-context, safe pure reads of counts only ever incremented
    // once each, at OnApply()) to reserve exactly enough death-holds up front, atomically — no dependence
    // on OnEnemyDeath listener dispatch order/timing the way a single "did anyone enable this" bool would
    // have (that was the previous killEffectHoldEnabled design; a listener requesting its own hold DURING
    // dispatch could lose the race against deletion checking the count that same tick). deathEventCount is
    // summed across every player's Stats since a death-reactive effect (e.g. a future "on any enemy death"
    // augment) doesn't care who the killer was; killEventCount is read only off the killer's own Stats since
    // kill-credited effects (e.g. OnKillLaunchEnemiesAugment) only ever react to their own kills. Each
    // augment that needs a hold increments the matching count once in OnApply() — multiple such augments on
    // the same player just add up, same idempotent shape as every other augment count in this file — and
    // releases its own hold via the enemy's ReleaseDeathHold() once its individual effect concludes.
    [HideInInspector] public int deathEventCount = 0;
    [HideInInspector] public int killEventCount = 0;
    public void AddDeathEventCount(int amount = 1) => deathEventCount += amount;
    public void AddKillEventCount(int amount = 1) => killEventCount += amount;

    // Punches a partial-rate hole in Simulate()'s own !state.isSurging gate on the passive EnergyRegen tick
    // (see EnergyRegenWhileSurgingAugment) — 0 (default) means fully suppressed while Surging, same as
    // before this existed. Plain += (not ApplyStatChange) since it doesn't touch Max Health/Energy, same
    // shape as phantomBladeDamageBonusPercent; multiple copies of the augment (or future family members)
    // just stack additively.
    [HideInInspector] public float energyRegenWhileSurgingPercent = 0f;
    public void AddEnergyRegenWhileSurgingPercent(float amount) => energyRegenWhileSurgingPercent += amount;

    // Separate gate for actually DAMAGING a touched ally (real friendly fire) — a weapon only applies
    // TakeDamage to an ally it touches if this is true, independent of allyStrikeHitEnabled above (a
    // weapon can heal-touch without damaging, or vice versa; detection itself runs if EITHER is true).
    // Consolidated from the old system's canDamageAllies. Nothing sets this yet — no augment/class passive
    // currently turns it on — but weapons already check it, so it's a real, working gate the moment
    // something eventually does.
    [HideInInspector] public bool allyStrikeDamageEnabled = false;
    public void EnableAllyStrikeDamage() => allyStrikeDamageEnabled = true;

    // Called by a weapon (melee/axe/ranged) once it's confirmed touching an ally's hurtbox — a plain touch,
    // not a damage hit, so this never goes through TakeDamage/RollDamage's damage path (a weapon's own
    // friendly-fire branch, gated on allyStrikeDamageEnabled, handles that separately). Self- and
    // dead-ally exclusion live here (not in each weapon's detection loop) so every current and future
    // _onAllyStrike listener gets that guard for free rather than each needing to re-check it.
    public void NotifyAllyStrike(AllyStrikeData data)
    {
        if (data.ally == null || data.ally == this || data.ally.IsDead) return;
        _onAllyStrike.Invoke(data);
    }

    // ── Execute ────────────────────────────────────────────────────────────
    // Checked by EnemyBasePD.TakeDamage — see that method's own doc comment for the full condition. Two
    // independent tracks, each gated purely on its own threshold being above 0 — no separate enable bool:
    // a plain "any hit" execute (executeThreshold — nothing sets this yet, pure futureproofing for a
    // non-crit execute augment down the line) and a crit-gated one (critExecuteThreshold, what
    // CritExecuteAugment actually turns on by adding to it). Execute is just a cheap "does this attacker
    // have EITHER threshold above 0 at all" gate, checked before either specific comparison runs.
    [HideInInspector] public float executeThreshold = 0f;
    [HideInInspector] public float critExecuteThreshold = 0f;
    public bool Execute => executeThreshold > 0f || critExecuteThreshold > 0f;

    // Bosses resist execute effects by 80% — a threshold tuned for normal/elite enemies would otherwise
    // insta-finish a boss the instant one crit landed while it was already low, which is a very different
    // balance question than "does this run-of-the-mill enemy die a hit early." One shared multiplier
    // (0.2 = keep 20%, i.e. -80%) rather than a separate boss-specific stat, so tuning the player-facing
    // threshold and the boss reduction can never drift out of the intended ratio relative to each other.
    public float BossExecuteThreshold => executeThreshold * 0.2f;
    public float BossCritExecuteThreshold => critExecuteThreshold * 0.2f;

    public void AddExecuteThresholdPercent(float amount) => executeThreshold += amount;
    public void AddCritExecuteThresholdPercent(float amount) => critExecuteThreshold += amount;

    // These children stay visually the same size regardless of the character's own scale — a revive
    // trigger volume and worldspace/minimap UI markers shouldn't grow along with the player. Height/position
    // is deliberately left alone — WorldspaceUI in particular sits under its own "YAxisMovers" parent
    // specifically so its Y offset keeps scaling up with the character (so the UI doesn't clip into a bigger
    // character), while only ITS OWN scale (set below) gets counter-scaled back to constant. Direct
    // Inspector references, not a name-string lookup — a plain transform.Find(name) only searches direct
    // children, so it silently found nothing (and left the UI scaling with the character) once WorldspaceUI/
    // Minimap Icon ended up nested under an intermediate parent instead of directly under this transform;
    // wiring the actual Transform here fails loudly (an empty slot, visible in the Inspector) instead of
    // silently no-opping the next time the hierarchy gets restructured.
    [SerializeField] private Transform[] unscaledSizeChildren;

    // Additive against the 1f baseline (amount is a delta, e.g. +0.25 = +25%), same convention as every
    // other bonus here — unlike a direct transform.localScale *= multiplier, which would compound
    // multiplicatively across multiple size augments instead of stacking their percentages. Re-derives
    // the full scale from bonusSizePercent every call rather than incrementally adjusting the transform,
    // so this stays correct no matter how many size augments have been applied. Each weapon's own root
    // localScale already reads bonusSizePercent directly too (see WeaponManagerPD's own RegisterWeapon/
    // LateUpdate — WeaponRadius itself deliberately doesn't, see its own comment for why), so no separate
    // call is needed here to scale the weapon.
    public void AddSizePercent(float amount)
    {
        bonusSizePercent += amount;
        transform.localScale = Vector3.one * bonusSizePercent;

        foreach (var child in unscaledSizeChildren)
            if (child != null)
                child.localScale = Vector3.one / bonusSizePercent;
    }

    private bool _bonusesApplied;
    public void ApplyProgressionBonuses(ProgressionBonuses b)
    {
        if (_bonusesApplied) return;
        _bonusesApplied = true;

        ApplyStatChange(() =>
        {
            bonusStrengthFlat          += b.strengthFlat;
            bonusMoveSpeedFlat         += b.moveSpeedFlat;
            attackSpeedFlat            += b.attackSpeedPct * baseAttackSpeed;
            bonusEnergyRegenFlat       += b.energyRegenFlat;
            dashHaste                  += b.dashHaste;
            bonusCritChanceFlat        += b.critChanceFlat;
            bonusXPRadiusFlat          += b.xpRadiusFlat;
            bonusWeaponRangeFlat       += b.weaponRangeFlat;
            bonusMaxEnergyFlat         += b.maxEnergyFlat;
            dashCharges                += b.dashChargesBonus;
            levelUpOptions             += b.levelUpOptionsBonus;
            revivePower                += b.revivePower;
            reviveTimePenaltyReduction *= b.reviveTimePenaltyMult;
            xpMultiplier               += b.xpMultiplier;
            bonusMaxHealthFlat         += b.maxHealthFlat;
            bonusHealthRegenFlat       += b.healthRegenFlat;
            bonusStrengthPercent       += b.strengthPct;
            bonusLuckFlat              += b.luckFlat;
            strengthPerLevel           += b.strengthPerLevel;
            bonusMaxShieldAdditive     += b.maxShieldFlat;
            shieldRechargeTime         += b.shieldRechargeTime;
        });
    }

    // Called from GameManagerPD.UpdateView() once per level gained, off verified state only — never
    // from Simulate(), since bonus fields aren't part of predicted State and can't roll back on replay.
    public void OnLevelUp() => ApplyStatChange(() => bonusStrengthFlat += strengthPerLevel);

    // Whole-run bookkeeping only, one field per StatType ApplySingleStatEntry below actually handles — the
    // literal amount ever added to that field via a stat-augment pick, nothing more (not the individual
    // picks/options themselves, which AugmentManagerPD.State.picks already tracks whole-run, tagged per
    // round). Returned by ApplyStatAugmentPick so AugmentManagerPD can accumulate it into its own per-player
    // running total —
    // kept here, not duplicated as a second switch statement elsewhere, so there's exactly one place that
    // defines "what a stat pick actually grants."
    public struct StatBoostTotals
    {
        public float maxHealth;
        public float strength;
        public float moveSpeed;
        public float weaponRange;
        public float attackSpeed;
        public float healthRegen;
        public float energyRegen;
        public float dashHaste;
        public float critChance;
        public float luck;
        public float abilityHaste;

        public static StatBoostTotals operator +(StatBoostTotals a, StatBoostTotals b) => new()
        {
            maxHealth    = a.maxHealth    + b.maxHealth,
            strength     = a.strength     + b.strength,
            moveSpeed    = a.moveSpeed    + b.moveSpeed,
            weaponRange  = a.weaponRange  + b.weaponRange,
            attackSpeed  = a.attackSpeed  + b.attackSpeed,
            healthRegen  = a.healthRegen  + b.healthRegen,
            energyRegen  = a.energyRegen  + b.energyRegen,
            dashHaste    = a.dashHaste    + b.dashHaste,
            critChance   = a.critChance   + b.critChance,
            luck         = a.luck         + b.luck,
            abilityHaste = a.abilityHaste + b.abilityHaste,
        };
    }

    // Called from AugmentManagerPD.UpdateView() once per verified pick, same reasoning as OnLevelUp.
    // multiplier is the picking player's own meta-progression multiplier (ProgressionManager.GetStatMultiplier),
    // captured into PlayerControllerPD.Input by the OWNING client — never recomputed locally here, since
    // a remote machine's own ProgressionManager save data would give a different (wrong) answer.
    public StatBoostTotals ApplyStatAugmentPick(StatAugmentEntry entryA, StatAugmentEntry entryB, float multiplier)
    {
        StatBoostTotals delta = default;
        ApplyStatChange(() =>
        {
            ApplySingleStatEntry(entryA, multiplier, ref delta);
            ApplySingleStatEntry(entryB, multiplier, ref delta);
        });
        return delta;
    }

    // Called from PlayerDataPD.UpdateView() once per character spawn, on every machine (not owner-gated
    // like ApplyProgressionBonuses — classPassive is a shared asset reference baked into the prefab, not
    // per-machine save data, so every observer computes the identical result). No meta-progression
    // multiplier applied here, unlike picked stat augments — a class passive is a fixed identity bonus,
    // not something ProgressionManager's LevelUpStats upgrade is meant to scale.
    private bool _classPassivesApplied;
    public void ApplyClassPassives()
    {
        if (_classPassivesApplied) return;
        _classPassivesApplied = true;

        // "No passive assigned" resolves as "applied" too — future code checking this flag directly
        // shouldn't need to separately handle the no-passive case as a distinct not-yet-resolved state.
        if (classPassive == null) return;

        var p = classPassive;
        ApplyStatChange(() =>
        {
            bonusMoveSpeedFlat              += p.bonusMoveSpeedFlat;
            bonusMoveSpeedPercent           += p.bonusMoveSpeedPercent;
            moveSpeedPenalty                += p.moveSpeedPenalty;
            totalMoveSpeedPercent           += p.totalMoveSpeedPercent;
            totalMoveSpeedPenalty           += p.totalMoveSpeedPenalty;

            bonusStrengthFlat           += p.bonusStrengthFlat;
            bonusStrengthPercent        += p.bonusStrengthPercent;
            bonusStrengthPenalty        += p.bonusStrengthPenalty;
            totalStrengthPercent        += p.totalStrengthPercent;
            totalStrengthPenalty        += p.totalStrengthPenalty;

            bonusWeaponRadiusFlat           += p.bonusWeaponRadiusFlat;
            bonusWeaponRadiusPercent        += p.bonusWeaponRadiusPercent;
            totalWeaponRadiusPercent        += p.totalWeaponRadiusPercent;
            totalWeaponRadiusPenalty        += p.totalWeaponRadiusPenalty;

            attackSpeedFlat                 += p.attackSpeedFlat;
            attackSpeedTotalPercent         += p.attackSpeedTotalPercent;
            attackSpeedPenalty              += p.attackSpeedPenalty;

            bonusWeaponCountFlat            += p.bonusWeaponCountFlat;
            weaponCountMultiplier      *= p.bonusWeaponCountMultiplier;

            bonusWeaponRangeFlat            += p.bonusWeaponRangeFlat;
            bonusWeaponRangePercent         += p.bonusWeaponRangePercent;
            weaponRangePenalty              += p.weaponRangePenalty;
            totalWeaponRangePercent         += p.totalWeaponRangePercent;
            totalWeaponRangePenalty         += p.totalWeaponRangePenalty;

            if (p.bonusSizePercent != 0f) AddSizePercent(p.bonusSizePercent);

            bonusCritChanceFlat             += p.bonusCritChanceFlat;
            bonusCritChancePercent          += p.bonusCritChancePercent;
            totalCritChancePercent          += p.totalCritChancePercent;
            totalCritChancePenalty          += p.totalCritChancePenalty;

            bonusCritDamageFlat             += p.bonusCritDamageFlat;
            bonusCritDamagePercent          += p.bonusCritDamagePercent;

            bonusRicochetFlat               += p.bonusRicochetFlat;
            bonusRicochetPercent            += p.bonusRicochetPercent;
            totalRicochetPercent            += p.totalRicochetPercent;
            totalRicochetPenalty            += p.totalRicochetPenalty;

            bonusMultishotFlat              += p.bonusMultishotFlat;
            bonusMultishotPercent           += p.bonusMultishotPercent;
            totalMultishotPercent           += p.totalMultishotPercent;
            totalMultishotPenalty           += p.totalMultishotPenalty;

            projectileDamageDropoffReduction += p.projectileDamageDropoffReduction;
            projectileOnHitDropoffReduction  += p.projectileOnHitDropoffReduction;

            fireRateFlat                    += p.fireRateFlat;
            fireRateTotalPercent            += p.fireRateTotalPercent;
            fireRateTotalPenalty            += p.fireRateTotalPenalty;

            projectileSpeedFlat             += p.projectileSpeedFlat;
            projectileSpeedTotalPercent     += p.projectileSpeedTotalPercent;
            projectileSpeedTotalPenalty     += p.projectileSpeedTotalPenalty;

            bonusMaxHealthFlat              += p.bonusMaxHealthFlat;
            bonusMaxHealthPercent           += p.bonusMaxHealthPercent;
            totalMaxHealthPercent           += p.totalMaxHealthPercent;
            totalMaxHealthPenalty           += p.totalMaxHealthPenalty;

            bonusHealthRegenFlat            += p.bonusHealthRegenFlat;
            bonusHealthRegenPercent         += p.bonusHealthRegenPercent;
            totalHealthRegenPercent         += p.totalHealthRegenPercent;
            totalHealthRegenPenalty         += p.totalHealthRegenPenalty;

            bonusMaxShieldAdditive          += p.bonusMaxShieldAdditive;
            totalMaxShieldPercent           += p.totalMaxShieldPercent;
            totalMaxShieldPenalty           += p.totalMaxShieldPenalty;

            bonusMaxEnergyFlat              += p.bonusMaxEnergyFlat;
            bonusMaxEnergyPercent           += p.bonusMaxEnergyPercent;

            bonusEnergyRegenFlat            += p.bonusEnergyRegenFlat;
            bonusEnergyRegenPercent         += p.bonusEnergyRegenPercent;
            energyRegenPenalty              += p.energyRegenPenalty;
            totalEnergyRegenPercent         += p.totalEnergyRegenPercent;
            totalEnergyRegenPenalty         += p.totalEnergyRegenPenalty;

            bonusSurgeCostFlat              += p.bonusSurgeCostFlat;
            bonusSurgeCostPercent           += p.bonusSurgeCostPercent;
            surgeAttackSpeedPenalty         += p.surgeAttackSpeedPenalty;

            dashHaste                       += p.dashHaste;
            bonusDashHastePercent           += p.bonusDashHastePercent;
            totalDashHastePercent           += p.totalDashHastePercent;
            totalDashHastePenalty           += p.totalDashHastePenalty;

            abilityHaste                    += p.abilityHaste;
            bonusAbilityHastePercent        += p.bonusAbilityHastePercent;
            totalAbilityHastePercent        += p.totalAbilityHastePercent;
            totalAbilityHastePenalty        += p.totalAbilityHastePenalty;

            bonusLuckFlat                   += p.bonusLuckFlat;
            bonusLuckPercent                += p.bonusLuckPercent;
            luckPenalty                     += p.luckPenalty;
            totalLuckPercent                += p.totalLuckPercent;
            totalLuckPenalty                += p.totalLuckPenalty;
            bonusLuckFloorPercent           += p.bonusLuckFloorPercent;
            luckFloorPenalty                += p.luckFloorPenalty;
            bonusLuckCeilingPercent         += p.bonusLuckCeilingPercent;
            luckCeilingPenalty              += p.luckCeilingPenalty;
            bonusHighrollPercent            += p.bonusHighrollPercent;
            highrollPenalty                 += p.highrollPenalty;
            bonusHighrollBonusPercent       += p.bonusHighrollBonusPercent;
            highrollBonusPenalty            += p.highrollBonusPenalty;

            knockbackMultiplier             *= p.knockbackMultiplier;
            knockbackPenalty                += p.knockbackPenalty;

            bonusOnHitPowerMultiplier       *= p.bonusOnHitPowerMultiplier;
            bonusOnHitPowerPenalty          += p.bonusOnHitPowerPenalty;
            totalOnHitPowerPercent          += p.totalOnHitPowerPercent;
            totalOnHitPowerPenalty          += p.totalOnHitPowerPenalty;

            stackPowerPercent               += p.stackPowerPercent;
            stackPowerPenalty               += p.stackPowerPenalty;

            xpMultiplier                    += p.xpMultiplier;
            xpPenalty                       += p.xpPenalty;

            bonusXPRadiusFlat               += p.bonusXPRadiusFlat;
            bonusXPRadiusPercent            += p.bonusXPRadiusPercent;

            reviveTimePenalty               += p.reviveTimePenalty;
            reviveTimePenaltyReduction      *= p.reviveTimePenaltyReduction;
            revivePower                     += p.revivePower;

            damageReductionPercent          += p.damageReductionPercent;
            damageReductionPenalty          += p.damageReductionPenalty;
        });

        // classPassive.passiveAugments is no longer spawned here — AugmentManagerPD.Simulate() registers
        // them as real PlayerAugmentRecords (and spawns their effect prefabs itself via SpawnAugmentEffect)
        // the moment this player's CharacterStatsPD becomes available, since that's the only place safe to
        // mutate AugmentManagerPD's own predicted state. See RegisterClassPassiveAugments and
        // ClassPassiveData.passiveAugments' own doc comments for the full reasoning.
    }

    // Called once per spawn (see PlayerDataPD.UpdateView()) to reapply whatever stat-augment totals
    // AugmentManagerPD has on record for this player — same underlying fields ApplySingleStatEntry above
    // grants, just applied as one lump sum instead of one pick at a time. On a normal game start every
    // player's recorded totals are still all-zero (nobody's picked anything yet), so this is a genuine
    // no-op then; for a reconnecting player (AugmentManagerPD itself is never destroyed/recreated by a
    // single player dropping, so its bookkeeping survives the disconnect) it's what restores the stats
    // they'd already earned. Not owner-gated, same reasoning as ApplyClassPassives — every machine holds
    // an identically-computed copy of AugmentManagerPD's totals (accumulated off verified picks the same
    // way on every machine), so every observer applies the same thing.
    public void ApplyStatBoostTotals(StatBoostTotals totals)
    {
        ApplyStatChange(() =>
        {
            bonusMaxHealthFlat    += totals.maxHealth;
            bonusStrengthFlat     += totals.strength;
            bonusMoveSpeedFlat    += totals.moveSpeed;
            bonusWeaponRangeFlat  += totals.weaponRange;
            attackSpeedFlat       += totals.attackSpeed;
            bonusHealthRegenFlat  += totals.healthRegen;
            bonusEnergyRegenFlat  += totals.energyRegen;
            dashHaste             += totals.dashHaste;
            bonusCritChanceFlat   += totals.critChance;
            bonusLuckFlat         += totals.luck;
            abilityHaste          += totals.abilityHaste;
        });
    }

    private void ApplySingleStatEntry(StatAugmentEntry entry, float multiplier, ref StatBoostTotals delta)
    {
        switch (entry.statType)
        {
            case StatType.MaxHealth:
                { float d = entry.value * multiplier; bonusMaxHealthFlat += d; delta.maxHealth += d; break; }
            case StatType.Strength:
                { float d = entry.value * multiplier; bonusStrengthFlat += d; delta.strength += d; break; }
            case StatType.MoveSpeed:
                { float d = entry.value * multiplier; bonusMoveSpeedFlat += d; delta.moveSpeed += d; break; }
            case StatType.WeaponRange:
                { float d = entry.value; bonusWeaponRangeFlat += d; delta.weaponRange += d; break; }
            case StatType.AttackSpeed:
                { float d = entry.value * 0.01f * baseAttackSpeed * multiplier; attackSpeedFlat += d; delta.attackSpeed += d; break; }
            case StatType.HealthRegen:
                { float d = entry.value * multiplier; bonusHealthRegenFlat += d; delta.healthRegen += d; break; }
            case StatType.EnergyRegen:
                { float d = entry.value * multiplier; bonusEnergyRegenFlat += d; delta.energyRegen += d; break; }
            case StatType.DashHaste:
                { float d = entry.value * multiplier; dashHaste += d; delta.dashHaste += d; break; }
            case StatType.CritChance:
                { float d = entry.value * 0.01f * multiplier; bonusCritChanceFlat += d; delta.critChance += d; break; }
            case StatType.Luck:
                { float d = entry.value * multiplier; bonusLuckFlat += d; delta.luck += d; break; }
            case StatType.AbilityHaste:
                { float d = entry.value * multiplier; abilityHaste += d; delta.abilityHaste += d; break; }
        }
    }

    public Color PlayerColor { get; private set; } = Color.white;
    public void ApplyColor(int colorIndex)
    {
        Color c = _colorPalette != null ? _colorPalette.Get(colorIndex) : Color.white;
        PlayerColor = c;
        foreach (var a in GetComponentsInChildren<PlayerColorApplicator>(true))
            a.Apply(c);
        if (TryGetComponent(out WeaponManagerPD wm))
            wm.ApplyColor(c);
    }

    // ── Public API ─────────────────────────────────────────────────────────
    public void StartSurge()
    {
        if (currentState.isDead || currentState.isSurging) return;
        currentState.isSurging = true;
        currentState.surgeTickTimer = Mathf.Max(1, Mathf.RoundToInt(SurgeTickInterval / _currentDelta));
        currentState.surgeMinDurationTimer = Mathf.Max(1, Mathf.RoundToInt(SurgeMinDuration / _currentDelta));
        currentState.energy -= SurgeCost;
        _onSurgeStarted.Invoke();
    }

    public void TryEndSurge()
    {
        if (!currentState.isSurging || currentState.surgeMinDurationTimer > 0) return;
        currentState.isSurging = false;
        _onSurgeEnded.Invoke();
    }

    // Unconditional surge end, bypassing the surgeMinDurationTimer throttle TryEndSurge() respects — for
    // effects that need to guarantee surge is off right now (e.g. the burst), not just request an end that
    // could silently no-op if surge only just started.
    public void ForceEndSurge()
    {
        if (!currentState.isSurging) return;
        currentState.isSurging = false;
        currentState.surgeMinDurationTimer = 0;
        _onSurgeEnded.Invoke();
    }

    // See IsSurgeBlocked/IAbilityPD.ActiveSurgeBlock's own doc comments for the full contract — each ability
    // holding a block calls AddSurgeBlock() once at its own Activate() (alongside ForceEndSurge()) and
    // RemoveSurgeBlock() once at its own end-of-active point. Paired 1:1 per holder, so the counter only
    // ever reaches 0 once every current holder has released its own block.
    public void AddSurgeBlock() => currentState.surgeBlockCount++;
    public void RemoveSurgeBlock() => currentState.surgeBlockCount = Mathf.Max(0, currentState.surgeBlockCount - 1);

    // Grants (or extends) invincibility for `duration` seconds. Always MAXES against whatever time is
    // already remaining rather than overwriting it, and a non-positive duration is simply a no-op — nothing
    // ever explicitly turns invincibility off "for" another source anymore. This is deliberate: dash,
    // ShieldBreakBurstAugment, ShieldDamageReductionAugment's death save, and Revive() all call this
    // independently, and previously (when this was a plain setter) whichever one fired LAST would win,
    // including a dash ending early and wiping out a still-active augment-granted window. Now every source
    // just contributes its own floor, the longest one currently active always wins, and each source's own
    // contribution is left to expire naturally via the invincibleTimer countdown in Simulate() — nobody
    // needs (or is able) to force it off early.
    public void GrantInvincibility(float duration)
    {
        if (duration <= 0f) return;
        currentState.invincible = true;
        currentState.invincibleTimer = Mathf.Max(currentState.invincibleTimer, Mathf.RoundToInt(duration / _currentDelta));
    }

    // Direct on/off, not duration-based like GrantInvincibility — whatever Taunt/Invisibility ability ends
    // up calling these owns its own duration/timer, this is just the flag enemies actually read. Guarded on
    // an actual change so _onTauntChanged/_onInvisibleChanged only ever fire for a real flip, never a no-op
    // call — the enemy-targeting hub (EnemySpawnerPD) reacts to every firing, so a redundant call here would
    // otherwise force every active enemy to redundantly re-evaluate its target for nothing.
    public void SetTaunting(bool value)
    {
        if (currentState.taunting == value) return;
        currentState.taunting = value;
        _onTauntChanged.Invoke(value);
    }

    public void SetInvisible(bool value)
    {
        if (currentState.invisible == value) return;
        currentState.invisible = value;
        _onInvisibleChanged.Invoke(value);
    }

    // No change-guard/event needed (unlike SetTaunting/SetInvisible) — nothing reacts to the flip itself,
    // every weapon just reads the current value fresh each tick.
    public void SetWeaponsDisabled(bool value) => currentState.weaponsDisabled = value;

    // Plain, not State — reset to false right before every _onLethalDamageTaken.Invoke() call and read
    // immediately after, all within the same synchronous TakeDamage() call. Never persists across separate
    // calls, so this is a same-call communication channel, not an accumulating flag — safe regardless of
    // resimulation.
    private bool _lethalDamagePrevented;

    // Called by a death-prevention listener (e.g. ShieldDamageReductionAugment), synchronously from inside
    // its own _onLethalDamageTaken callback, to flag that the lethal hit currently being processed by
    // TakeDamage should instead leave the player at 1 health. Calling this outside that callback (i.e. any
    // time TakeDamage isn't mid-resolution of a lethal hit) has no effect — the flag is only ever read once,
    // immediately after the event that gives it meaning.
    public void PreventLethalDamage() => _lethalDamagePrevented = true;

    // Instantly refills shield to full and fires the same recharge-edge event the natural Simulate()-driven
    // regen does (only if it was actually broken) — used by effects like ShieldDamageReductionAugment's
    // death save, so "reapply while shielded"-style effects correctly reapply immediately rather than
    // waiting for the natural shieldRechargeTimer to also elapse.
    public void RechargeShield()
    {
        bool wasBroken = currentState.shield <= 0f;
        currentState.shield = MaxShield;
        currentState.shieldRechargeTimer = 0;
        if (wasBroken && currentState.shield > 0f)
            _onShieldRecharge.Invoke();
    }

    public void TakeDamage(float damage)
    {
        if (currentState.isDead) return;
        if (currentState.invincible) return;

        currentState.damageEventId++;
        currentState.shieldRechargeTimer = Mathf.Max(1, Mathf.RoundToInt(shieldRechargeTime / _currentDelta));
        damage *= DamageReduction;
        // Captured here, before the shield/health split below — "how much damage this hit represented" as
        // a whole, for _onDamaged's payload.
        float totalDamageTaken = damage;

        // Break edge, not "shield is currently 0" — only true the tick it actually crosses down from >0,
        // same reasoning as every other transition-detected event in this class. Invoking is deferred to
        // the very end (see below) so a hit that also kills outright through shield+health+DamageReduction
        // doesn't fire it.
        bool shieldJustBroke = false;

        if (currentState.shield > 0f)
        {
            float shieldDamage = Mathf.Min(damage, currentState.shield);
            currentState.shield -= shieldDamage;
            damage -= shieldDamage;

            if (currentState.shield <= 0f)
                shieldJustBroke = true;
        }

        if (damage > 0f)
        {
            bool wouldBeLethal = currentState.health - damage <= 0f;
            if (wouldBeLethal)
            {
                // Gives any listener (e.g. a death-prevention augment) a chance to call
                // PreventLethalDamage() synchronously, right here, before this damage is actually applied —
                // see that method's own doc comment.
                _lethalDamagePrevented = false;
                _onLethalDamageTaken.Invoke();
            }

            if (wouldBeLethal && _lethalDamagePrevented)
                currentState.health = 1f;
            else
                currentState.health -= damage;
        }

        if (currentState.health <= 0f)
        {
            currentState.health = 0f;
            currentState.isDead = true;
        }

        // Only fires if this same hit didn't also kill the player — a lethal hit shouldn't trigger a
        // "shield broke" reaction (invincibility, an AOE burst, etc.) on a player who's already dead.
        if (shieldJustBroke && !currentState.isDead)
            _onShieldBreak.Invoke();

        // Same "not on the killing blow" exclusion as _onShieldBreak above.
        if (totalDamageTaken > 0f && !currentState.isDead)
            _onDamaged.Invoke(totalDamageTaken);
    }

    public void Heal(float amount)
    {
        if (currentState.isDead) return;
        float healed = Mathf.Min(amount, MaxHealth - currentState.health);
        if (healed <= 0f) return;
        currentState.health += healed;
        _onHeal.Invoke(healed);
    }

    // Same as Heal(), minus the _onHeal invocation — for effects that react to a heal already in progress
    // (e.g. CriticalHealingAugment topping up a heal it just observed via AddHealListener) and would
    // otherwise re-trigger their own listener by calling Heal() again, cascading indefinitely on repeated
    // crits. Same "must not re-notify its own trigger" shape as OnHitDamageAugment's own crit handling.
    public void HealSilent(float amount)
    {
        if (currentState.isDead) return;
        float healed = Mathf.Min(amount, MaxHealth - currentState.health);
        if (healed <= 0f) return;
        currentState.health += healed;
    }

    // ApplyStatChange's own before/after MaxHealth delta (see its own comment) only ever ADDS the same delta
    // current health moved by when Max Health rises or falls — it never separately re-clamps current health
    // down to a lower new ceiling if current health was already sitting above it for some other reason (e.g.
    // natural regen/healing topped the player up while a temporary Max Health buff was active, then the buff
    // ends and the ceiling drops out from under them). Effects reversing a temporary Max Health buff should
    // call this right after, same convention as KnightBigBuffAbilityPD's own buff-end reversal.
    public void ClampHealthToMax()
    {
        if (currentState.health > MaxHealth) currentState.health = MaxHealth;
    }

    // Same clamped-gain shape as Heal(), for Energy instead of Health — no _onXInvoke since nothing
    // currently needs to react to an energy gain in progress the way CriticalHealingAugment reacts to heals.
    public void GainEnergy(float amount)
    {
        if (currentState.isDead) return;
        float gained = Mathf.Min(amount, MaxEnergy - currentState.energy);
        if (gained <= 0f) return;
        currentState.energy += gained;
    }

    public void Revive(float healthPercent = 0.5f)
    {
        if (!currentState.isDead) return;

        currentState.isDead = false;
        currentState.health = MaxHealth * healthPercent;
        currentState.shield = MaxShield;
        currentState.energy = MaxEnergy;
        currentState.currentReviveTime += ReviveTimePenalty;
        GrantInvincibility(2f);
    }
}
