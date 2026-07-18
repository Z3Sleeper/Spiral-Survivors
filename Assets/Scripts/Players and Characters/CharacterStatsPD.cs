using System;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

public class CharacterStatsPD : PredictedIdentity<CharacterStatsPD.State>
{
    public static readonly Dictionary<PlayerID, CharacterStatsPD> All = new();

    // Static, not per-instance PredictedEvent — fired from EnemyBasePD.UpdateView() off verified state with
    // its own edge-detection (isDead && !_wasDeadLastFrame), the same hand-rolled pattern already
    // established as functionally equivalent to PredictedEvent (see GameManagerPD.OnGameEnded), so it's
    // already safe to fire directly here without needing the PredictedEvent machinery itself. OnEnemyDeath
    // fires for every enemy death regardless of killer (killer may be null if it doesn't resolve to a
    // living player); OnEnemyKill only fires additionally when the killer is the LOCAL owner specifically —
    // for augments that care only about the local player's own kills, not every death on the field.
    public static event Action<CharacterStatsPD, IEnemyPD> OnEnemyDeath;
    public static event Action<CharacterStatsPD, IEnemyPD> OnEnemyKill;
    public static void RaiseOnEnemyDeath(CharacterStatsPD killer, IEnemyPD enemy) => OnEnemyDeath?.Invoke(killer, enemy);
    public static void RaiseOnEnemyKill(CharacterStatsPD killer, IEnemyPD enemy) => OnEnemyKill?.Invoke(killer, enemy);

    public enum CharacterClass { Knight, Rogue, Archer, Bladeweaver, Executioner }
    public CharacterClass characterClass;

    [SerializeField] private ClassPassiveData classPassive;

    // ── Serialized base values ─────────────────────────────────────────────

    [Header("Movement")]
    public float baseMoveSpeed = 10f;

    [Header("Weapon")]
    public float damagePerLevel = 2.5f;
    public float baseWeaponDamage = 15f;
    public float baseWeaponRadius = 0f;
    public float baseAttackSpeed = 0.5f;
    public int baseWeaponCount = 1;
    public float baseWeaponRange = 10f;
    public float baseWeaponSize = 1f;
    public bool continuousFire = false;

    [Header("Crit")]
    public float baseCritChance = 0f;
    public float baseCritDamage = 2f;

    [Header("Ranged Weapons")]
    public float baseRicochet = 0f;
    public float baseMultishot = 0f;
    public float baseMultishotSpreadAngle = 75f;
    public float baseProjectileDamageDropoff = 0.1f;
    public float baseProjectileOnHitDropoff = 0.1f;

    [Header("Health")]
    public float baseMaxHealth = 50f;
    public float baseHealthRegen = 0f;

    [Header("Shield")]
    public float baseMaxShield = 0.05f;
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

    [Header("Burst + SP")]
    public float baseMaxSP = 100f;

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
    // snaps up to the max and invokes the hitting weapon's own _onHighroll (a PredictedEvent, rollback-safe
    // — see WeaponBasePD<TState>._onHighroll / WeaponRangedProjectilePD._onHighroll). Same base+bonus+penalty
    // convention as every other stat here, so a future augment/passive can push it (e.g. toward 1.0 = never
    // highrolls, or lower = highrolls more often) without touching WeaponDamageMath itself.
    public float baseHighroll = 0.99f;
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

    [Header("Revive")]
    public float reviveTime = 3f;

    // ── Augment/bonus modifiers ────────────────────────────────────────────

    [HideInInspector] public float bonusMoveSpeedFlat = 0f;
    [HideInInspector] public float bonusMoveSpeedPercent = 1f;
    [HideInInspector] public float moveSpeedPenalty = 1f;

    [HideInInspector] public float bonusWeaponDamageFlat = 0f;
    [HideInInspector] public float bonusWeaponDamagePercent = 1f;
    [HideInInspector] public float bonusWeaponDamagePenalty = 1f;

    [HideInInspector] public float bonusWeaponRadiusFlat = 0f;
    [HideInInspector] public float bonusWeaponRadiusPercent = 1f;

    [HideInInspector] public float attackSpeedPercent = 0f;
    [HideInInspector] public float attackSpeedTotalPercent = 1f;
    [HideInInspector] public float attackSpeedPenalty = 1f;

    [HideInInspector] public int bonusWeaponCountFlat = 0;
    [HideInInspector] public int bonusWeaponCountMultiplier = 1;

    [HideInInspector] public float bonusWeaponRangeFlat = 0f;
    [HideInInspector] public float bonusWeaponRangePercent = 1f;
    [HideInInspector] public float weaponRangePenalty = 1f;

    [HideInInspector] public float bonusWeaponSizePercent = 1f;

    // Character-size augments (Titan, and any future ones) add their delta here rather than each
    // multiplying transform.localScale directly — additive, so multiple size augments stack (+25% +
    // +30% = +55%) instead of compounding multiplicatively (1.25 * 1.30 = 1.625x). WeaponSize (the
    // weapon's actual hitbox/visual scale) scales by this too, so the weapon grows with the character
    // without a separate per-augment multiply call. WeaponRadius (orbit distance) deliberately does not
    // — see its own comment for why.
    [HideInInspector] public float bonusSizePercent = 1f;

    [HideInInspector] public float bonusCritChanceFlat = 0f;
    [HideInInspector] public float bonusCritChancePercent = 1f;
    // False by default — onhit-triggered damage effects don't crit at all unless OnHitCritAugment turns
    // this on. Future onhit-damage-dealing augments should check this before rolling their own crit
    // chance/multiplier, same "capability is off until an augment turns it on" shape as
    // phantomBladeOnHitEnabled.
    [HideInInspector] public bool onHitCanCrit = false;

    [HideInInspector] public float bonusCritDamageFlat = 0f;
    [HideInInspector] public float bonusCritDamagePercent = 1f;

    [HideInInspector] public float bonusRicochetFlat = 0f;
    [HideInInspector] public float bonusRicochetPercent = 1f;

    [HideInInspector] public float bonusMultishotFlat = 0f;
    [HideInInspector] public float bonusMultishotPercent = 1f;
    [HideInInspector] public float bonusMultishotSpreadPercent = 1f;

    [HideInInspector] public float projectileDamageDropoffReduction = 1f;
    [HideInInspector] public float projectileOnHitDropoffReduction = 1f;

    [HideInInspector] public float bonusMaxHealthFlat = 0f;
    [HideInInspector] public float bonusMaxHealthPercent = 1f;

    [HideInInspector] public float bonusHealthRegenFlat = 0f;
    [HideInInspector] public float bonusHealthRegenPercent = 1f;

    [HideInInspector] public float bonusMaxShieldFlat = 0f;
    [HideInInspector] public float bonusMaxShieldPercent = 1f;

    [HideInInspector] public float bonusMaxEnergyFlat = 0f;
    [HideInInspector] public float bonusMaxEnergyPercent = 1f;

    [HideInInspector] public float bonusEnergyRegenFlat = 0f;
    [HideInInspector] public float bonusEnergyRegenPercent = 1f;
    [HideInInspector] public float energyRegenPenalty = 1f;

    [HideInInspector] public float bonusSurgeCostFlat = 0f;
    [HideInInspector] public float bonusSurgeCostPercent = 1f;
    [HideInInspector] public float surgeEffectiveness = 1f;
    [HideInInspector] public float surgeEffectivenessPenalty = 1f;
    [HideInInspector] public float surgeAttackSpeedPenalty = 1f;

    [HideInInspector] public float dashHaste = 0f;
    // Multiplicative on top of dashHaste (see DashCooldown) — same "additive against a 1f baseline"
    // pattern as bonusWeaponRangePercent/bonusCritChancePercent, added alongside dashHaste's existing flat
    // grant so DashHaste-family augments can have the same flat+percent dual structure Range/CritChance do.
    [HideInInspector] public float bonusDashHastePercent = 1f;

    [HideInInspector] public float bonusMaxSPFlat = 0f;
    [HideInInspector] public float bonusMaxSPPercent = 1f;

    [HideInInspector] public float bonusLuckFlat = 0f;
    [HideInInspector] public float bonusLuckPercent = 1f;
    [HideInInspector] public float luckPenalty = 1f;
    [HideInInspector] public float bonusLuckFloorPercent = 1f;
    [HideInInspector] public float luckFloorPenalty = 1f;
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

    [HideInInspector] public float stackPowerPercent = 1f;
    [HideInInspector] public float stackPowerPenalty = 1f;

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

    public float MoveSpeed => (baseMoveSpeed + bonusMoveSpeedFlat) * bonusMoveSpeedPercent * moveSpeedPenalty * (currentState.isSurging ? SurgeMoveSpeedMulti : 1f);

    public float WeaponDamage => (baseWeaponDamage + bonusWeaponDamageFlat) * bonusWeaponDamagePercent * bonusWeaponDamagePenalty;
    public float UnpenalizedWeaponDamage => (baseWeaponDamage + bonusWeaponDamageFlat) * bonusWeaponDamagePercent;
    // Deliberately doesn't factor in bonusSizePercent, unlike WeaponSize — Bladeweaver is the only class
    // that uses this for actual orbit distance (hovering blades), and a size augment pushing that
    // distance out further makes it harder for them to hit anything nearby, which isn't the intent of a
    // size augment. WeaponSize (hitbox/visual scale) still scales with size; this doesn't need to.
    public float WeaponRadius => (baseWeaponRadius + bonusWeaponRadiusFlat) * bonusWeaponRadiusPercent;

    public float AttackSpeed => (baseAttackSpeed + attackSpeedPercent) * attackSpeedTotalPercent * attackSpeedPenalty * (currentState.isSurging ? SurgeAttackSpeedMultiplier : 1f);
    public float WeaponSpeed => AttackSpeed * 360f;
    public float AttackSpeedPercent => attackSpeedTotalPercent * attackSpeedPenalty;

    public int WeaponCount => (baseWeaponCount + bonusWeaponCountFlat) * bonusWeaponCountMultiplier;
    public float WeaponRange => (baseWeaponRange + bonusWeaponRangeFlat) * bonusWeaponRangePercent * weaponRangePenalty * 0.2f;
    public float WeaponSize => baseWeaponSize * bonusWeaponSizePercent * bonusSizePercent;

    public float CritChance => (baseCritChance + bonusCritChanceFlat) * bonusCritChancePercent;
    public float CritDamage => ((baseCritDamage + bonusCritDamageFlat) * bonusCritDamagePercent) + Mathf.Max(0f, CritChance - 1f) * 0.5f;

    public float Ricochet => (baseRicochet + bonusRicochetFlat) * bonusRicochetPercent;
    public float Multishot => (baseMultishot + bonusMultishotFlat) * bonusMultishotPercent;
    public float MultishotSpreadAngle => baseMultishotSpreadAngle * bonusMultishotSpreadPercent;
    public float ProjectileDamageDropoff => baseProjectileDamageDropoff * projectileDamageDropoffReduction;
    public float ProjectileOnHitDropoff => baseProjectileOnHitDropoff * projectileOnHitDropoffReduction;

    public float MaxHealth => (baseMaxHealth + bonusMaxHealthFlat) * bonusMaxHealthPercent;
    public float HealthRegen => (baseHealthRegen + bonusHealthRegenFlat) * bonusHealthRegenPercent;

    public float MaxShieldPercentOfMaxHealth => (baseMaxShield + bonusMaxShieldFlat) * bonusMaxShieldPercent;
    public float MaxShield => MaxHealth * MaxShieldPercentOfMaxHealth;

    public float MaxEnergy => (baseMaxEnergy + bonusMaxEnergyFlat) * bonusMaxEnergyPercent;
    public float EnergyRegen => (baseEnergyRegen + bonusEnergyRegenFlat) * bonusEnergyRegenPercent * energyRegenPenalty;

    public float SurgeCost => (baseSurgeCost + bonusSurgeCostFlat) * bonusSurgeCostPercent;
    public float SurgeActivationThreshhold => SurgeCost * 5f;
    public float SurgeEffectiveness => surgeEffectiveness * surgeEffectivenessPenalty;
    public float SurgeMoveSpeedMulti => surgeMoveSpeedMulti * surgeMoveSpeedPenalty * SurgeEffectiveness;
    public float SurgeAttackSpeedMultiplier => surgeAttackSpeedMulti * surgeAttackSpeedPenalty * SurgeEffectiveness;

    // The actual effective dash haste value DashCooldown's formula uses — exposed separately so UI (or
    // anything else) can display the real number instead of the pre-multiplier raw dashHaste field.
    public float EffectiveDashHaste => dashHaste * bonusDashHastePercent;
    public float DashCooldown => baseDashCooldown / (1f + EffectiveDashHaste / 100f);

    public float MaxSP => (baseMaxSP + bonusMaxSPFlat) * bonusMaxSPPercent;

    public float Luck => (baseLuck + bonusLuckFlat) * bonusLuckPercent * luckPenalty;
    public float LuckChance => Luck / (Luck + 100f);
    // Both clamped to a minimum of 0 here — the one source every consumer reads from — so no
    // debuff/penalty stacking can ever push either one negative and cause a roll below zero. No upper
    // clamp on the ceiling; an arbitrarily high one is just a bigger reward, not a bug.
    public float LuckFloor   => Mathf.Max(0f, baseLuckFloor * bonusLuckFloorPercent * luckFloorPenalty);
    public float LuckCeiling => Mathf.Max(0f, baseLuckCeiling * bonusLuckCeilingPercent * luckCeilingPenalty);
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

    public float OnHitPower => baseOnHitPower * bonusOnHitPowerMultiplier * bonusOnHitPowerPenalty;
    public float StackPower => stackPowerPercent * stackPowerPenalty;
    public float XPModifier => xpMultiplier * xpPenalty;
    public float XPRadius => (baseXPRadius + bonusXPRadiusFlat) * bonusXPRadiusPercent;

    public float ReviveTimePenalty => reviveTimePenalty * reviveTimePenaltyReduction;
    public float DamageReduction => damageReductionPercent * damageReductionPenalty;

    // Simulation state reads (use in Simulate / gameplay logic only)
    public float CurrentHealth => currentState.health;
    public float CurrentEnergy => currentState.energy;
    public float CurrentShield => currentState.shield;
    public float CurrentSP     => currentState.sp;
    public bool  IsDead        => currentState.isDead;
    public bool  IsSurging     => currentState.isSurging;
    public bool  IsInvincible  => currentState.invincible;
    public float CurrentReviveTime => currentState.currentReviveTime;

    // UI-facing state (set in UpdateView — safe to read from MonoBehaviour.Update)
    // Health/shield use verified state to avoid prediction-rollback jumps.
    // Energy/SP/surge use viewState since the owner predicts these accurately.
    public float UIHealth              { get; private set; }
    public float UIShield              { get; private set; }
    public float UIShieldRechargeTimer { get; private set; }
    public float UIEnergy              { get; private set; }
    public float UISP                  { get; private set; }
    public bool  UIIsSurging           { get; private set; }

    // ── State ──────────────────────────────────────────────────────────────
    public struct State : IPredictedData<State>
    {
        public float health;
        public float energy;
        public float shield;
        public float sp;
        public bool isDead;
        public bool isSurging;
        public bool invincible;
        public float invincibleTimer;
        public float surgeTickTimer;
        public float surgeMinDurationTimer;
        public int colorIndex;
        public float healthRegenTimer;
        public float shieldRechargeTimer;
        public uint damageEventId;
        public float currentReviveTime;
        public void Dispose() { }
    }

    protected override State GetInitialState() => new()
    {
        health = baseMaxHealth,
        energy = baseMaxEnergy,
        shield = 0f,
        sp = 0f,
        healthRegenTimer = 1f,
        shieldRechargeTimer = 0f,
        currentReviveTime = reviveTime
    };

    // ── Events ─────────────────────────────────────────────────────────────
    // Per-instance (not static): each character's events are its own, not a shared global channel.
    // PredictedEvent handles view-safety itself — server always fires, the owner fires once on its
    // own live (non-replay) pass, and non-owners fire once the tick is verified — so callers don't
    // need to reason about replay/ownership at the invoke site.
    private PredictedEvent<float> _onHeal;
    private PredictedEvent _onSurgeStarted;
    private PredictedEvent _onSurgeEnded;
    // Relayed here from WeaponManagerPD (which subscribes to every one of this player's own registered
    // weapons' own crit events) — the one channel downstream per-player systems like
    // PhantomBladeOnCritAugment subscribe to, instead of each augment needing to know about individual
    // weapons at all. Deliberately per-instance like every other event here, not static — this is what
    // makes cross-player interaction structurally impossible rather than just filtered against.
    private PredictedEvent<WeaponHitData> _onCritStrike;
    // Same relay shape as _onCritStrike, but fires for every weapon hit (crit or not) — e.g.
    // CritTriggerOnHitAugment subscribes to both and calls NotifyHit again from its own crit listener to
    // make a crit count as two hits.
    private PredictedEvent<WeaponHitData> _onHit;
    // Fires whenever any of this player's weapons touches (not damages) an ally — called directly by each
    // weapon (no WeaponManagerPD relay hop needed, unlike _onHit/_onCritStrike, since "touched an ally"
    // isn't a per-weapon-type concept anything else needs to distinguish). Generic on purpose: multiple
    // "hit ally, do something" augments (HealAllyOnStrikeAugment and future family members) can each
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

    public void AddHealListener(Action<float> listener) => _onHeal.AddListener(listener);
    public void RemoveHealListener(Action<float> listener) => _onHeal.RemoveListener(listener);
    public void AddSurgeStartedListener(Action listener) => _onSurgeStarted.AddListener(listener);
    public void RemoveSurgeStartedListener(Action listener) => _onSurgeStarted.RemoveListener(listener);
    public void AddSurgeEndedListener(Action listener) => _onSurgeEnded.AddListener(listener);
    public void RemoveSurgeEndedListener(Action listener) => _onSurgeEnded.RemoveListener(listener);
    public void AddCritStrikeListener(Action<WeaponHitData> listener) => _onCritStrike.AddListener(listener);
    public void RemoveCritStrikeListener(Action<WeaponHitData> listener) => _onCritStrike.RemoveListener(listener);
    public void NotifyCritStrike(WeaponHitData data) => _onCritStrike.Invoke(data);
    public void AddHitListener(Action<WeaponHitData> listener) => _onHit.AddListener(listener);
    public void RemoveHitListener(Action<WeaponHitData> listener) => _onHit.RemoveListener(listener);
    public void NotifyHit(WeaponHitData data) => _onHit.Invoke(data);
    public void AddAllyStrikeListener(Action<AllyStrikeData> listener) => _onAllyStrike.AddListener(listener);
    public void RemoveAllyStrikeListener(Action<AllyStrikeData> listener) => _onAllyStrike.RemoveListener(listener);
    public void AddPhantomBladeSummonListener(Action<PhantomBladeSummonData> listener) => _onPhantomBladeSummon.AddListener(listener);
    public void RemovePhantomBladeSummonListener(Action<PhantomBladeSummonData> listener) => _onPhantomBladeSummon.RemoveListener(listener);
    public void NotifyPhantomBladeSummon(PhantomBladeSummonData data) => _onPhantomBladeSummon.Invoke(data);

    // ── Lifecycle ──────────────────────────────────────────────────────────
    protected override void LateAwake()
    {
        _onHeal          = new PredictedEvent<float>(predictionManager, this);
        _onSurgeStarted  = new PredictedEvent(predictionManager, this);
        _onSurgeEnded    = new PredictedEvent(predictionManager, this);
        _onCritStrike    = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onHit           = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onAllyStrike    = new PredictedEvent<AllyStrikeData>(predictionManager, this);
        _onPhantomBladeSummon = new PredictedEvent<PhantomBladeSummonData>(predictionManager, this);

        // bonuses applied by PlayerDataPD.Simulate after spawn
        GetComponent<WeaponManagerPD>()?.Init(this);
        // TODO: GetComponent<RogueClassPassive>()?.Init(this);

        UIHealth = baseMaxHealth;
        UIEnergy = baseMaxEnergy;

        if (isOwner && LocalUIManagerPD.instance != null)
            LocalUIManagerPD.instance.SetPlayer(this);
    }

    private const float SurgeTickInterval = 0.1f;
    private const float SurgeMinDuration  = 0.5f;

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Energy recharges even while dead so the player has energy on revive.
        if (!state.isSurging)
            state.energy = Mathf.Min(state.energy + EnergyRegen * delta, MaxEnergy);

        if (state.isDead) return;

        // Invincibility timer
        if (state.invincible && state.invincibleTimer > 0f)
        {
            state.invincibleTimer -= delta;
            if (state.invincibleTimer <= 0f)
                state.invincible = false;
        }

        // Health regen
        if (state.health < MaxHealth)
        {
            state.healthRegenTimer -= delta;
            if (state.healthRegenTimer <= 0f)
            {
                float healed = Mathf.Min(HealthRegen, MaxHealth - state.health);
                state.health += healed;
                state.healthRegenTimer = 1f;
                if (healed > 0f) _onHeal.Invoke(healed);
            }
        }

        // Shield regen
        if (state.shield < MaxShield)
        {
            state.shieldRechargeTimer -= delta;
            if (state.shieldRechargeTimer <= 0f)
                state.shield = MaxShield;
        }

        // Surge energy drain (can't be surging while dead, so this block is alive-only)
        if (state.isSurging)
        {
            if (state.surgeMinDurationTimer > 0f)
                state.surgeMinDurationTimer -= delta;

            state.surgeTickTimer -= delta;
            if (state.surgeTickTimer <= 0f)
            {
                state.surgeTickTimer = SurgeTickInterval;
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
            UIShieldRechargeTimer = verified.Value.shieldRechargeTimer;
        }
        UIEnergy              = viewState.energy;
        UISP                  = viewState.sp;
        UIIsSurging           = viewState.isSurging;

        if (isOwner && viewState.damageEventId != _lastDamageEventId)
        {
            _lastDamageEventId = viewState.damageEventId;
            if (SFXManager.instance != null) SFXManager.instance.Play(_damagedSfx, transform.position);
        }

        if (_invincibilityShell != null)
            _invincibilityShell.SetActive(viewState.invincible);

        // TODO: apply color
    }

    protected override void Destroyed()
    {
        if (owner.HasValue)
            All.Remove(owner.Value);
    }

    // ── Init ──────────────────────────────────────────────────────────────
    public struct ProgressionBonuses
    {
        public float weaponDamageFlat, moveSpeedFlat, attackSpeedPct, energyRegenFlat;
        public float dashHaste, critChanceFlat, xpRadiusFlat, weaponRangeFlat, maxEnergyFlat;
        public int   dashChargesBonus, levelUpOptionsBonus;
        public float revivePower, reviveTimePenaltyMult, xpMultiplier;
        public float maxHealthFlat, healthRegenFlat, weaponDamagePct;
        public float luckFlat, damagePerLevel, maxShieldFlat, shieldRechargeTime;
    }

    public static ProgressionBonuses ComputeProgressionBonuses() => new()
    {
        weaponDamageFlat        = ProgressionManager.GetBaseStatBonus(StatType.WeaponDamage),
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
        weaponDamagePct         = ProgressionManager.GetBaseStatBonus(StatType.WeaponDamagePercent),
        luckFlat                = ProgressionManager.GetBaseStatBonus(StatType.Luck),
        damagePerLevel          = ProgressionManager.GetBaseStatBonus(StatType.DamagePerLevel),
        maxShieldFlat           = ProgressionManager.GetBaseStatBonus(StatType.MaxShield),
        shieldRechargeTime      = ProgressionManager.GetBaseStatBonus(StatType.ShieldRechargeTime),
    };

    // Wraps any stat mutation that might raise MaxHealth/MaxEnergy, and carries current resources up
    // by the same delta so the ceiling never rises while current stays pinned below it.
    // Shield is deliberately excluded — it doesn't gradually regen like health/energy, it snaps to
    // full after a no-damage timer, so a max-shield increase should only raise that ceiling and let
    // the existing regen naturally catch current shield up to it.
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
    }

    // Public entry point for augment effect components (e.g. HealthAugment) to grant a flat max health
    // bonus — routes through ApplyStatChange so current health rises with the new ceiling instead of
    // staying pinned below it. Augment effects apply identically on every machine (unlike the old
    // owner-only model), so this needs no isOwner gating here.
    public void AddMaxHealthFlat(float amount) => ApplyStatChange(() => bonusMaxHealthFlat += amount);

    // Same pattern as AddMaxHealthFlat, for augments that grant a percentage bonus instead (additive
    // against the 1f baseline, e.g. +0.2 = +20% total).
    public void AddMaxHealthPercent(float amount) => ApplyStatChange(() => bonusMaxHealthPercent += amount);
    public void AddWeaponRangeFlat(float amount) => ApplyStatChange(() => bonusWeaponRangeFlat += amount);
    public void AddWeaponRangePercent(float amount) => ApplyStatChange(() => bonusWeaponRangePercent += amount);
    public void AddAttackSpeedTotalPercent(float amount) => ApplyStatChange(() => attackSpeedTotalPercent += amount);
    // attackSpeedPercent (despite the name) is added directly in the same units as baseAttackSpeed, not a
    // raw fraction — matches the progression system's own conversion (entry.value * 0.01f * baseAttackSpeed)
    // so a caller here can pass an intuitive fraction (e.g. 0.25 for +25%) same as every other percent Add.
    public void AddAttackSpeedPercent(float amount) => ApplyStatChange(() => attackSpeedPercent += amount * baseAttackSpeed);
    public void AddMoveSpeedFlat(float amount) => ApplyStatChange(() => bonusMoveSpeedFlat += amount);
    public void AddMoveSpeedPercent(float amount) => ApplyStatChange(() => bonusMoveSpeedPercent += amount);
    public void AddHealthRegenFlat(float amount) => ApplyStatChange(() => bonusHealthRegenFlat += amount);
    public void AddHealthRegenPercent(float amount) => ApplyStatChange(() => bonusHealthRegenPercent += amount);
    public void AddEnergyRegenFlat(float amount) => ApplyStatChange(() => bonusEnergyRegenFlat += amount);
    public void AddEnergyRegenPercent(float amount) => ApplyStatChange(() => bonusEnergyRegenPercent += amount);
    public void AddLuckFlat(float amount) => ApplyStatChange(() => bonusLuckFlat += amount);
    public void AddLuckPercent(float amount) => ApplyStatChange(() => bonusLuckPercent += amount);
    public void AddCritChanceFlat(float amount) => ApplyStatChange(() => bonusCritChanceFlat += amount);
    // Same "additive against the 1f baseline" pattern as AddMaxHealthPercent, for CritChance's own percent
    // multiplier (bonusCritChancePercent starts at 1f, e.g. +0.25 = +25% total crit chance).
    public void AddCritChancePercent(float amount) => ApplyStatChange(() => bonusCritChancePercent += amount);
    public void AddDashHaste(float amount) => ApplyStatChange(() => dashHaste += amount);
    // Same "additive against the 1f baseline" pattern as AddMaxHealthPercent/AddCritChancePercent, for
    // DashHaste's own percent multiplier (bonusDashHastePercent starts at 1f, e.g. +0.15 = +15% total).
    public void AddDashHastePercent(float amount) => ApplyStatChange(() => bonusDashHastePercent += amount);
    public void AddWeaponCountFlat(int amount) => ApplyStatChange(() => bonusWeaponCountFlat += amount);
    // Multiplicative, not additive — bonusWeaponDamagePenalty/bonusOnHitPowerPenalty both start at 1f and
    // apply as straight multipliers (WeaponDamage/OnHitPower), so stacking two -40% penalties correctly
    // compounds to 0.6 * 0.6 = 0.36 rather than clamping at 1 - 0.8 = 0.2. factor is what the penalty gets
    // multiplied by (e.g. 1f - 0.4f for a -40% penalty), matching the old system's identical
    // `bonusWeaponDamagePenalty *= 1f - damagePenalty` call.
    public void MultiplyWeaponDamagePenalty(float factor) => ApplyStatChange(() => bonusWeaponDamagePenalty *= factor);
    public void MultiplyOnHitPowerPenalty(float factor) => ApplyStatChange(() => bonusOnHitPowerPenalty *= factor);
    public void AddRicochetFlat(float amount) => ApplyStatChange(() => bonusRicochetFlat += amount);
    public void AddMultishotFlat(float amount) => ApplyStatChange(() => bonusMultishotFlat += amount);
    // Same MultiplyWeaponDamagePenalty-style compounding convention — multiple spread-reducing sources
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
    // WeaponDamage/MoveSpeed etc. do.
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
    // effect. Same MultiplyWeaponDamagePenalty-style compounding convention as bonusWeaponDamagePenalty
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
    public void EnableOnHitCrit() => onHitCanCrit = true;
    public void AddPhantomBladeOnHitPowerPercent(float amount) => phantomBladeOnHitPowerPercent += amount;

    // Same enable-bool shape as phantomBladeOnHitEnabled above, but generic (no attached magnitude here) —
    // weapons only pay for the extra ally-hurtbox overlap query when this (or allyStrikeDamageEnabled
    // below) is true, and ANY "hit ally, do something" augment (HealAllyOnStrikeAugment, future family
    // members) turns it on the same way via EnableAllyStrikeHit(). Multiple such augments picked at once
    // just each call it — idempotent, no conflict — and all react independently to the same underlying
    // _onAllyStrike notifications below. Consolidated from the old system's canContactAllies (same role,
    // dormant/unused in this project until now).
    [HideInInspector] public bool allyStrikeHitEnabled = false;
    public void EnableAllyStrikeHit() => allyStrikeHitEnabled = true;

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

    // These children stay visually the same size regardless of the character's own scale — a revive
    // trigger volume and worldspace/minimap UI markers shouldn't grow along with the player.
    private static readonly string[] _unscaledSizeChildren = { "ReviveZone", "WorldspaceUI", "Minimap Icon" };

    // Additive against the 1f baseline (amount is a delta, e.g. +0.25 = +25%), same convention as every
    // other bonus here — unlike a direct transform.localScale *= multiplier, which would compound
    // multiplicatively across multiple size augments instead of stacking their percentages. Re-derives
    // the full scale from bonusSizePercent every call rather than incrementally adjusting the transform,
    // so this stays correct no matter how many size augments have been applied. WeaponRadius/WeaponSize
    // already read bonusSizePercent directly, so no separate call is needed to scale the weapon too.
    public void AddSizePercent(float amount)
    {
        bonusSizePercent += amount;
        transform.localScale = Vector3.one * bonusSizePercent;

        foreach (var childName in _unscaledSizeChildren)
        {
            var child = transform.Find(childName);
            if (child != null)
                child.localScale = Vector3.one / bonusSizePercent;
        }
    }

    private bool _bonusesApplied;
    public void ApplyProgressionBonuses(ProgressionBonuses b)
    {
        if (_bonusesApplied) return;
        _bonusesApplied = true;

        ApplyStatChange(() =>
        {
            bonusWeaponDamageFlat      += b.weaponDamageFlat;
            bonusMoveSpeedFlat         += b.moveSpeedFlat;
            attackSpeedPercent         += b.attackSpeedPct * baseAttackSpeed;
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
            bonusWeaponDamagePercent   += b.weaponDamagePct;
            bonusLuckFlat              += b.luckFlat;
            damagePerLevel             += b.damagePerLevel;
            bonusMaxShieldFlat         += b.maxShieldFlat;
            shieldRechargeTime         += b.shieldRechargeTime;
        });
    }

    // Called from GameManagerPD.UpdateView() once per level gained, off verified state only — never
    // from Simulate(), since bonus fields aren't part of predicted State and can't roll back on replay.
    public void OnLevelUp() => ApplyStatChange(() => bonusWeaponDamageFlat += damagePerLevel);

    // Called from AugmentManagerPD.UpdateView() once per verified pick, same reasoning as OnLevelUp.
    // multiplier is the picking player's own meta-progression multiplier (ProgressionManager.GetStatMultiplier),
    // captured into PlayerControllerPD.Input by the OWNING client — never recomputed locally here, since
    // a remote machine's own ProgressionManager save data would give a different (wrong) answer.
    public void ApplyStatAugmentPick(StatAugmentEntry entryA, StatAugmentEntry entryB, float multiplier)
    {
        ApplyStatChange(() =>
        {
            ApplySingleStatEntry(entryA, multiplier);
            ApplySingleStatEntry(entryB, multiplier);
        });
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

            bonusWeaponDamageFlat           += p.bonusWeaponDamageFlat;
            bonusWeaponDamagePercent        += p.bonusWeaponDamagePercent;
            bonusWeaponDamagePenalty        += p.bonusWeaponDamagePenalty;

            bonusWeaponRadiusFlat           += p.bonusWeaponRadiusFlat;
            bonusWeaponRadiusPercent        += p.bonusWeaponRadiusPercent;

            attackSpeedPercent              += p.attackSpeedPercent;
            attackSpeedTotalPercent         += p.attackSpeedTotalPercent;
            attackSpeedPenalty              += p.attackSpeedPenalty;

            bonusWeaponCountFlat            += p.bonusWeaponCountFlat;
            bonusWeaponCountMultiplier      *= p.bonusWeaponCountMultiplier;

            bonusWeaponRangeFlat            += p.bonusWeaponRangeFlat;
            bonusWeaponRangePercent         += p.bonusWeaponRangePercent;
            weaponRangePenalty              += p.weaponRangePenalty;

            bonusWeaponSizePercent          += p.bonusWeaponSizePercent;

            if (p.bonusSizePercent != 0f) AddSizePercent(p.bonusSizePercent);

            bonusCritChanceFlat             += p.bonusCritChanceFlat;
            bonusCritChancePercent          += p.bonusCritChancePercent;

            bonusCritDamageFlat             += p.bonusCritDamageFlat;
            bonusCritDamagePercent          += p.bonusCritDamagePercent;

            bonusRicochetFlat               += p.bonusRicochetFlat;
            bonusRicochetPercent            += p.bonusRicochetPercent;

            bonusMultishotFlat              += p.bonusMultishotFlat;
            bonusMultishotPercent           += p.bonusMultishotPercent;

            projectileDamageDropoffReduction += p.projectileDamageDropoffReduction;
            projectileOnHitDropoffReduction  += p.projectileOnHitDropoffReduction;

            bonusMaxHealthFlat              += p.bonusMaxHealthFlat;
            bonusMaxHealthPercent           += p.bonusMaxHealthPercent;

            bonusHealthRegenFlat            += p.bonusHealthRegenFlat;
            bonusHealthRegenPercent         += p.bonusHealthRegenPercent;

            bonusMaxShieldFlat              += p.bonusMaxShieldFlat;
            bonusMaxShieldPercent           += p.bonusMaxShieldPercent;

            bonusMaxEnergyFlat              += p.bonusMaxEnergyFlat;
            bonusMaxEnergyPercent           += p.bonusMaxEnergyPercent;

            bonusEnergyRegenFlat            += p.bonusEnergyRegenFlat;
            bonusEnergyRegenPercent         += p.bonusEnergyRegenPercent;
            energyRegenPenalty              += p.energyRegenPenalty;

            bonusSurgeCostFlat              += p.bonusSurgeCostFlat;
            bonusSurgeCostPercent           += p.bonusSurgeCostPercent;
            surgeEffectiveness              *= p.surgeEffectiveness;
            surgeEffectivenessPenalty       += p.surgeEffectivenessPenalty;
            surgeAttackSpeedPenalty         += p.surgeAttackSpeedPenalty;

            dashHaste                       += p.dashHaste;

            bonusMaxSPFlat                  += p.bonusMaxSPFlat;
            bonusMaxSPPercent               += p.bonusMaxSPPercent;

            bonusLuckFlat                   += p.bonusLuckFlat;
            bonusLuckPercent                += p.bonusLuckPercent;
            luckPenalty                     += p.luckPenalty;
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

        if (classPassive.effectPrefabs != null)
        {
            foreach (var prefab in classPassive.effectPrefabs)
            {
                if (prefab != null)
                    hierarchy.Create(prefab, transform.position, transform.rotation, owner);
            }
        }
    }

    private void ApplySingleStatEntry(StatAugmentEntry entry, float multiplier)
    {
        switch (entry.statType)
        {
            case StatType.MaxHealth:     bonusMaxHealthFlat    += entry.value * multiplier; break;
            case StatType.WeaponDamage:  bonusWeaponDamageFlat += entry.value * multiplier; break;
            case StatType.MoveSpeed:     bonusMoveSpeedFlat    += entry.value * multiplier; break;
            case StatType.WeaponRange:   bonusWeaponRangeFlat  += entry.value; break;
            case StatType.AttackSpeed:   attackSpeedPercent    += entry.value * 0.01f * baseAttackSpeed * multiplier; break;
            case StatType.HealthRegen:   bonusHealthRegenFlat  += entry.value * multiplier; break;
            case StatType.EnergyRegen:   bonusEnergyRegenFlat  += entry.value * multiplier; break;
            case StatType.DashHaste:     dashHaste             += entry.value * multiplier; break;
            case StatType.CritChance:    bonusCritChanceFlat   += entry.value * 0.01f * multiplier; break;
            case StatType.Luck:          bonusLuckFlat         += entry.value * multiplier; break;
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
        currentState.surgeTickTimer = SurgeTickInterval;
        currentState.surgeMinDurationTimer = SurgeMinDuration;
        currentState.energy -= SurgeCost;
        _onSurgeStarted.Invoke();
    }

    public void TryEndSurge()
    {
        if (!currentState.isSurging || currentState.surgeMinDurationTimer > 0f) return;
        currentState.isSurging = false;
        _onSurgeEnded.Invoke();
    }

    public void SetInvincible(float duration)
    {
        if (duration <= 0f) { currentState.invincible = false; currentState.invincibleTimer = 0f; return; }
        currentState.invincible = true;
        currentState.invincibleTimer = duration;
    }

    public void TakeDamage(float damage)
    {
        if (currentState.isDead) return;
        if (currentState.invincible) return;

        currentState.damageEventId++;
        currentState.shieldRechargeTimer = shieldRechargeTime;
        damage *= DamageReduction;

        if (currentState.shield > 0f)
        {
            float shieldDamage = Mathf.Min(damage, currentState.shield);
            currentState.shield -= shieldDamage;
            damage -= shieldDamage;
        }

        if (damage > 0f)
            currentState.health -= damage;

        if (currentState.health <= 0f)
        {
            currentState.health = 0f;
            currentState.isDead = true;
        }
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
    // crits. Same "must not re-notify its own trigger" shape as OnHitDamageAugment/OnHitCritAugment.
    public void HealSilent(float amount)
    {
        if (currentState.isDead) return;
        float healed = Mathf.Min(amount, MaxHealth - currentState.health);
        if (healed <= 0f) return;
        currentState.health += healed;
    }

    public void Revive(float healthPercent = 0.5f)
    {
        if (!currentState.isDead) return;

        currentState.isDead = false;
        currentState.health = MaxHealth * healthPercent;
        currentState.shield = MaxShield;
        currentState.energy = MaxEnergy;
        currentState.currentReviveTime += ReviveTimePenalty;
        SetInvincible(2f);
    }
}
