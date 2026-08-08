using PurrNet.Prediction;
using UnityEngine;

// Knight's second class ability, built on the IAbilityPD architecture — gated by a cooldown, same shape as
// RogueVanishAbilityPD (a fixed-duration self-buff with no active-window locking, see that file for the
// template this mirrors). Grants +50% Total Size, Max Health, Weapon Range, Strength and 50%
// Damage Reduction for a fixed duration, and taunts every enemy (CharacterStatsPD.SetTaunting) for the same
// window — EnemyBasePD/EnemySpawnerPD already react to SetTaunting immediately (wired ahead of this ability
// existing, see OnPlayerTauntChanged's own doc comment), so this only needs to flip the flag on and back off.
//
// All five stat grants use the Total tier (AddSizePercent/AddMaxHealthTotalPercent/
// AddWeaponRangeTotalPercent/AddStrengthTotalPercent/AddDamageReductionPercent) rather than the plain Bonus
// tier the 8 generic stat-pick augments use — same "outlier/special effect, not a per-point-buy pick"
// reasoning as RogueVanishAbilityPD's own AddMoveSpeedTotalPercent (see project_bonus_total_stat_tiers
// memory). CharacterStatsPD.AbilityPower scales only the buff's own duration, same uniform convention every
// buff ability in this codebase now follows (RogueVanishAbilityPD/ExeBigAxeAbilityPD/BWBlinkAbilityPD) —
// snapshotted at cast time, since a live AbilityPower change mid-buff must not leave the active timer
// running against a duration that no longer matches what was granted. The five stat grants themselves are
// flat, fixed values — deliberately NOT also AbilityPower-scaled, since a buff's own strength and its
// duration both scaling off the same stat on the same cast would double-dip.
//
// The Max Health buff acts as a pseudo-heal for free: CharacterStatsPD.ApplyStatChange (which
// AddMaxHealthTotalPercent routes through) already bumps current health by the exact same delta the ceiling
// just rose by, so granting +50% Max Health here also tops the caster up by 50% of their pre-buff Max
// Health, no separate Heal() call needed. The reverse direction needs one extra step ApplyStatChange does
// NOT do on its own: if natural regen/healing filled the player back up while the ceiling was raised, current
// health could still be sitting above the new (lower) ceiling once the buff ends and the ceiling drops back
// down — CharacterStatsPD.ClampHealthToMax() (called right after AddMaxHealthTotalPercent's own reversal
// below) explicitly re-clamps for exactly that case.
public class KnightBigBuffAbilityPD : PredictedIdentity<KnightBigBuffAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float activeTimer;
        public bool  isActive;
        public float cooldownTimer;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Rally";
    [SerializeField, TextArea] private string description =
        "Gain +50% Size, Max Health, Range, Damage and Damage Reduction, and taunt all enemies, for 10 seconds.";
    public float cooldown = 30f;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false (held at its full value
    // throughout the buff window) — see Simulate(). true: cooldown starts ticking immediately at cast,
    // running concurrently with the active window. Same convention/default as RogueVanishAbilityPD's own.
    [SerializeField] private bool cooldownStartsOnCast = false;
    // Which action-bar slot this occupies — Inspector-configurable per character rather than hardcoded per
    // class, so which ability goes where is a prefab-authoring choice, not a recompile.
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability2;
    [SerializeField] private string[] tags;
    // false (default) — see IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract.
    [SerializeField] private bool activeSurgeBlock = false;

    [Header("Effect")]
    public float duration = 10f;
    public float sizePercent            = 0.5f;
    public float maxHealthPercent       = 0.5f;
    public float rangePercent           = 0.5f;
    public float strengthPercent        = 0.5f;
    // Passed straight to AddDamageReductionPercent, which itself subtracts this from damageReductionPercent
    // (1f = no reduction) — 0.5 here means incoming damage multiplies by 0.5, i.e. half damage taken. Not a
    // "true total" increase stacking convention, just a flat multiplicative half same as the ability's own
    // stated design (see class doc comment).
    public float damageReductionPercent = 0.5f;

    // Mirrors Activate()'s own _appliedDuration formula exactly (see class doc comment). Outside a live
    // match, falls back to the base field itself — exact at neutral AbilityPower (see
    // ArcherFocusFireAbilityPD.LiveDurationRatio's own doc comment for why), so one token covers both.
    public string LiveDurationRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? duration * stats.AbilityPower
            : duration;
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(value)} ";
    }

    private CharacterStatsPD _stats;

    // Flat — not AbilityPower-scaled (see class doc comment), so these are just plain snapshots of the
    // fields at cast time, reversed with the identical values at buff-end.
    private float _appliedSizePercent;
    private float _appliedMaxHealthPercent;
    private float _appliedRangePercent;
    private float _appliedStrengthPercent;
    private float _appliedDamageReductionPercent;
    // Snapshotted alongside the others — see IAbilityPD.ActiveTimeDuration's own doc comment for why the UI
    // needs this fixed rather than recomputing duration * AbilityPower live off a since-changed AbilityPower.
    private float _appliedDuration;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
    }

    public bool IsReady => _stats != null && !currentState.isActive && currentState.cooldownTimer <= 0f;
    public bool IsActive => currentState.isActive;
    // Only ever flips stat totals/taunt — never touches movement, so it never locks dash or the other
    // ability slot out while active (see IAbilityPD.ForcesSpecialAction's own doc comment).
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    // Same underlying window as IsActive for this ability (the whole buff) — see IAbilityPD.
    // AbilityIsActive's own doc comment for why these are separate interface members even though they
    // agree here.
    public bool AbilityIsActive => currentState.isActive;
    public bool HasActiveWindow => true;
    public float ActiveTimeRemaining => Mathf.Max(0f, currentState.activeTimer);
    public float ActiveTimeDuration => _appliedDuration;
    public float CooldownRemaining => Mathf.Max(0f, currentState.cooldownTimer);
    public float CooldownDuration => EffectiveCooldown;
    public float BaseCooldownDuration => cooldown;
    public bool CooldownStartsOnCast => cooldownStartsOnCast;
    public Sprite Icon => icon;
    public string Name => abilityName;
    public string Description => description;
    public string[] Tags => tags;
    public AbilitySlot Slot => slot;

    // Same formula as CharacterStatsPD.DashCooldown (baseCooldown / (1 + haste/100)) — a haste augment
    // shortens this ability's cooldown the same way it already does for dash, or lengthens it if
    // EffectiveAbilityHaste ever goes negative (a penalty).
    private float EffectiveCooldown => _stats != null ? cooldown / (1f + _stats.EffectiveAbilityHaste / 100f) : cooldown;

    // Mirrors EffectiveCooldown's own formula exactly. Outside a live match, falls back to the base field
    // itself — exact at neutral EffectiveAbilityHaste (0, i.e. cooldown / (1+0/100) == cooldown), same
    // "neutral stat value == the base field" convention as every other Live*Ratio in this file.
    public string LiveCooldownRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? cooldown / (1f + stats.EffectiveAbilityHaste / 100f)
            : cooldown;
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(value)} ";
    }

    // See IAbilityPD's own doc comments for the exact contract of each of these three.
    public void ReduceCooldownFlat(float seconds) =>
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer - seconds);
    public void ReduceCooldownPercentRemaining(float percent01) =>
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer * (1f - Mathf.Clamp01(percent01)));
    public void ReduceCooldownPercentTotal(float percent01) =>
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer - CooldownDuration * Mathf.Clamp01(percent01));

    public void Activate()
    {
        if (!IsReady || _stats == null) return;

        // AbilityPower's interpretation for this ability: grows only the buff's own duration — see class doc
        // comment. The five stat grants below are flat, not AbilityPower-scaled.
        float abilityPower = _stats.AbilityPower;
        _appliedSizePercent            = sizePercent;
        _appliedMaxHealthPercent       = maxHealthPercent;
        _appliedRangePercent           = rangePercent;
        _appliedStrengthPercent        = strengthPercent;
        _appliedDamageReductionPercent = damageReductionPercent;
        _appliedDuration                = duration * abilityPower;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        currentState.cooldownTimer = EffectiveCooldown;

        // Direct cross-identity mutation, same pattern as RogueVanishAbilityPD.Activate() — safe since
        // Activate() is only ever called from PlayerControllerPD's own Simulate(), deterministically,
        // identically on every machine.
        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in Simulate()'s own end-of-active block below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }
        _stats.SetTaunting(true);
        _stats.AddSizePercent(_appliedSizePercent);
        // Also tops current health up by the same delta the ceiling just rose by — see class doc comment.
        _stats.AddMaxHealthTotalPercent(_appliedMaxHealthPercent);
        _stats.AddWeaponRangeTotalPercent(_appliedRangePercent);
        _stats.AddStrengthTotalPercent(_appliedStrengthPercent);
        _stats.AddDamageReductionPercent(_appliedDamageReductionPercent);

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cycle (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    public void NotifyActivationBlocked() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole active
        // window below) or only once that window ends (state.isActive false) — see the field's own doc
        // comment. Either way Activate() already set cooldownTimer to its full value at cast time. Same
        // shape as RogueVanishAbilityPD.Simulate().
        if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive))
            state.cooldownTimer -= delta;

        if (!state.isActive) return;

        state.activeTimer -= delta;
        // Death force-ends the buff early — see ExeBigAxeAbilityPD.Simulate()'s own identical comment for
        // the full reasoning (same shape here: a dead caster gets nothing from these stats, and the cooldown
        // otherwise couldn't start ticking until this window ends anyway).
        bool forceEndOnDeath = _stats != null && _stats.IsDead;
        if (state.activeTimer > 0f && !forceEndOnDeath) return;

        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        if (_stats != null)
        {
            _stats.SetTaunting(false);
            _stats.AddSizePercent(-_appliedSizePercent);
            _stats.AddMaxHealthTotalPercent(-_appliedMaxHealthPercent);
            // Explicit re-clamp for the one case ApplyStatChange's own delta-add doesn't cover — see class
            // doc comment.
            _stats.ClampHealthToMax();
            _stats.AddWeaponRangeTotalPercent(-_appliedRangePercent);
            _stats.AddStrengthTotalPercent(-_appliedStrengthPercent);
            _stats.AddDamageReductionPercent(-_appliedDamageReductionPercent);
        }
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        // See the matching NotifyAbilityStart call in Activate() for why this is gated on cooldown > 0 too.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }
}
