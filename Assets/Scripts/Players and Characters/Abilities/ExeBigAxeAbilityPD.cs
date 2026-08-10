using PurrNet.Prediction;
using UnityEngine;

// Executioner's first class ability, built on the IAbilityPD architecture — gated by a cooldown, same
// single-active-window shape as KnightBigBuffAbilityPD (that file is the template this mirrors): three stat
// grants applied at cast, all reversed with the identical snapshotted values once the active window ends.
// Never touches movement (ForcesSpecialAction is false).
//
// Originally also granted a CharacterStatsPD.OuterXZMultiplier bonus (tripling the axe's outer hit zone) —
// scrapped once it turned out to look bad even working exactly as intended (a proportionally bigger
// axehead just doesn't read well at 3x). Swapped for a flat +50% Total Weapon Range grant instead
// (AddWeaponRangeTotalPercent, same call KnightBigBuffAbilityPD's own range buff uses). The OuterXZ system
// itself (CharacterStatsPD.OuterXZMultiplier, WeaponAxePD's own read sites) is untouched and still fully
// functional — just unused by this ability now — in case it's useful again later.
//
// CharacterStatsPD.AbilityPower scales only the buff's own duration, same uniform convention every buff
// ability in this codebase now follows (RogueVanishAbilityPD/KnightBigBuffAbilityPD/BWBlinkAbilityPD) —
// snapshotted at cast time so a live AbilityPower change mid-buff can't leave the active timer running
// against a duration that no longer matches what was granted. The three grants themselves (Weapon Range/
// Execute Threshold/Crit Chance) are flat, fixed values — deliberately NOT also AbilityPower-scaled, since a
// buff's own strength and its duration both scaling off the same stat on the same cast would double-dip.
public class ExeBigAxeAbilityPD : PredictedIdentity<ExeBigAxeAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float activeTimer;
        public bool  isActive;
        public float cooldownTimer;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Big Axe";
    [SerializeField, TextArea] private string description =
        "Wield a massive axe for 10 seconds, gaining +50% Total Weapon Range, +25% Execute Threshold and +25% Crit Chance.";
    public float cooldown = 30f;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false (held at its full value
    // throughout the buff window) — see Simulate(). true: cooldown starts ticking immediately at cast,
    // running concurrently with the active window. Same convention/default as KnightBigBuffAbilityPD's own.
    [SerializeField] private bool cooldownStartsOnCast = false;
    // Which action-bar slot this occupies — Inspector-configurable per character rather than hardcoded per
    // class, so which ability goes where is a prefab-authoring choice, not a recompile.
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability1;
    [SerializeField] private string[] tags;
    // false (default) — see IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract.
    [SerializeField] private bool activeSurgeBlock = false;

    [Header("Effect")]
    public float duration = 10f;
    public float weaponRangeTotalPercent = 0.5f;
    public float executeThresholdPercent = 0.25f;
    public float critChancePercent       = 0.25f;

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
    private float _appliedWeaponRangeTotalPercent;
    private float _appliedExecuteThreshold;
    private float _appliedCritChance;
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
    // Only ever flips stat totals — never touches movement, so it never locks dash or the other ability
    // slot out while active (see IAbilityPD.ForcesSpecialAction's own doc comment).
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

    // Mirrors Simulate()'s own tick condition exactly (see that method's own comment) — a cooldown-reduction
    // effect should only ever eat into time that's ACTUALLY counting down right now, not a cooldownTimer
    // that's merely sitting at its full pre-loaded value while the active window (cooldownStartsOnCast=false)
    // hasn't ended yet to let it start ticking.
    private bool IsCooldownActivelyTicking =>
        currentState.cooldownTimer > 0f && (cooldownStartsOnCast || !currentState.isActive);

    // See IAbilityPD's own doc comments for the exact contract of each of these three.
    public void ReduceCooldownFlat(float seconds)
    {
        if (!IsCooldownActivelyTicking) return;
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer - seconds);
    }
    public void ReduceCooldownPercentRemaining(float percent01)
    {
        if (!IsCooldownActivelyTicking) return;
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer * (1f - Mathf.Clamp01(percent01)));
    }
    public void ReduceCooldownPercentTotal(float percent01)
    {
        if (!IsCooldownActivelyTicking) return;
        currentState.cooldownTimer = Mathf.Max(0f, currentState.cooldownTimer - CooldownDuration * Mathf.Clamp01(percent01));
    }

    public void Activate()
    {
        if (!IsReady || _stats == null) return;

        // AbilityPower's interpretation for this ability: grows only the buff's own duration — see class doc
        // comment. The three grants below are flat, not AbilityPower-scaled.
        float abilityPower = _stats.AbilityPower;
        _appliedWeaponRangeTotalPercent = weaponRangeTotalPercent;
        _appliedExecuteThreshold        = executeThresholdPercent;
        _appliedCritChance              = critChancePercent;
        _appliedDuration                = duration * abilityPower;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        currentState.cooldownTimer = EffectiveCooldown;

        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in Simulate()'s own end-of-active block below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }

        // Direct cross-identity mutation, same pattern as KnightBigBuffAbilityPD.Activate() — safe since
        // Activate() is only ever called from PlayerControllerPD's own Simulate(), deterministically,
        // identically on every machine.
        _stats.AddWeaponRangeTotalPercent(_appliedWeaponRangeTotalPercent);
        _stats.AddExecuteThresholdPercent(_appliedExecuteThreshold);
        _stats.AddCritChanceFlat(_appliedCritChance);

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — see BWBladeDanceAbilityPD's own identical guard
        // for the full reasoning (moot here at the default 30s cooldown, kept for consistency).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    public void NotifyActivationBlocked() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole active
        // window below) or only once that window ends (state.isActive false) — see the field's own doc
        // comment. Either way Activate() already set cooldownTimer to its full value at cast time. Same
        // shape as KnightBigBuffAbilityPD.Simulate().
        if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive))
            state.cooldownTimer -= delta;

        if (!state.isActive) return;

        state.activeTimer -= delta;
        // Death force-ends the buff early — a dead caster gets no benefit from Range/Execute/Crit Chance
        // sitting on a corpse, and the cooldown otherwise couldn't even start ticking (cooldownStartsOnCast
        // is false by default here, so cooldownTimer only ticks once state.isActive goes false — see the
        // condition above). _stats.IsDead is part of currentState, so every machine forces the same end tick.
        bool forceEndOnDeath = _stats != null && _stats.IsDead;
        if (state.activeTimer > 0f && !forceEndOnDeath) return;

        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        if (_stats != null)
        {
            _stats.AddWeaponRangeTotalPercent(-_appliedWeaponRangeTotalPercent);
            _stats.AddExecuteThresholdPercent(-_appliedExecuteThreshold);
            _stats.AddCritChanceFlat(-_appliedCritChance);
        }
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }
}
