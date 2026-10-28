using PurrNet.Prediction;
using UnityEngine;

// Executioner's first class ability, built on the IAbilityPD architecture — gated by a cooldown, same
// single-active-window shape as KnightBerserkAbilityPD (that file is the template this mirrors): three stat
// grants applied at cast, all reversed with the identical snapshotted values once the active window ends.
// Never touches movement (ForcesSpecialAction is false).
//
// Originally also granted a CharacterStatsPD.OuterXZMultiplier bonus (tripling the axe's outer hit zone) —
// scrapped once it turned out to look bad even working exactly as intended (a proportionally bigger
// axehead just doesn't read well at 3x). Swapped for a flat +50% Total Weapon Range grant instead
// (AddWeaponRangeTotalPercent, same call KnightBerserkAbilityPD's own range buff uses). The OuterXZMultiplier
// stat itself was fully removed (2026-09-30, once WeaponAxePD's Normalized-system migration replaced it with
// a plain authored per-weapon multiplier — see WeaponAxePD's own class doc comment) — this comment stays only
// as design history for why this ability doesn't grant a "bigger axehead" bonus.
//
// No AbilityPower scaling (removed 2026-08-23) — duration and the three grants themselves (Weapon Range/
// Execute Threshold/Crit Chance) are all plain, fixed values, snapshotted at cast time so a live Inspector
// change mid-buff can't leave the active timer running against a duration that no longer matches what was
// granted.
public class ExeBigAxeAbilityPD : PredictedIdentity<ExeBigAxeAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float activeTimer;
        public bool  isActive;
        public float cooldownTimer;
        // Ability charge system — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment.
        public int   charges;
        public int   knownMaxCharges;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Big Axe";
    [SerializeField, TextArea] private string description =
        "Wield a massive axe for 10 seconds, gaining +50% Total Weapon Range, +25% Execute Threshold and +25% Crit Chance.";
    public float cooldown = 30f;
    // How many charges this ability starts with (1 = old off-cooldown behavior, unchanged) — see
    // IAbilityPD.MaxCharges's own doc comment. CharacterStatsPD.bonusAbility1Charges adds on top live.
    [SerializeField] private int authoredChargeCount = 1;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false (held at its full value
    // throughout the buff window) — see Simulate(). true: cooldown starts ticking immediately at cast,
    // running concurrently with the active window. Same convention/default as KnightBerserkAbilityPD's own.
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

    // Live, SINGLE shared multiplier (2026-10-06, ExeBigAxeBuffAugment) — 1f (neutral) default,
    // [HideInInspector] since it's augment-owned. Applied by the ability itself (see Activate()'s own
    // _appliedXPercent = X * buffStrengthMultiplier snapshot lines), same shape
    // KnightBuffStunAbilityPD.buffStrengthMultiplier uses — MULTIPLICATIVE (ExeBigAxeBuffAugment itself
    // does buffStrengthMultiplier *= buffStrengthMultiplierPercent, not additive), per explicit design
    // intent to mirror that augment's exact shape rather than the usual additive-multiplier default. Scales
    // ALL FOUR of this ability's buff amounts uniformly — weaponRangeTotalPercent/executeThresholdPercent/
    // critChancePercent above AND knockbackPercent/attackSpeedPercent below (ExeBigAxeKnockbackAttackSpeedAugment)
    // — so it correctly reaches whichever of these fields any OTHER augment sets, regardless of which was
    // picked first, same "amplifies everything regardless of pick order" reasoning
    // KnightBuffStunStrengthKnockbackAugment's own doc comment gives for the Knight equivalent.
    [HideInInspector] public float buffStrengthMultiplier = 1f;

    // Augment-owned (2026-10-07, ExeBigAxeRangeAugment) — 0f (true no-op) default, [HideInInspector].
    // ADDS directly onto buffStrengthMultiplier at the point weaponRangeTotalPercent is scaled (see
    // Activate()'s own `weaponRangeTotalPercent * (buffStrengthMultiplier + rangeMultiplierBonus)` line) —
    // deliberately ADDITIVE with buffStrengthMultiplier rather than its own separate multiplicative factor,
    // per the explicit "stacks additively with the other buff augment" design request. Only ever affects
    // Weapon Range, never Execute Threshold/Crit Chance/Knockback/Attack Speed — those stay scaled by
    // buffStrengthMultiplier alone. Worked example: base buffStrengthMultiplier=1 (no Buff augment) +
    // rangeMultiplierBonus=1 (this augment's own +100%) = 2x Range, same as picking this alone would
    // suggest; WITH ExeBigAxeBuffAugment too, buffStrengthMultiplier=2 + rangeMultiplierBonus=1 = 3x Range
    // — the two contributions ADD into one shared multiplier instead of compounding multiplicatively
    // (which would have given 4x instead of 3x).
    [HideInInspector] public float rangeMultiplierBonus = 0f;

    // Augment-owned (2026-10-06, ExeBigAxeKnockbackAttackSpeedAugment) — both 0f (true no-op) default,
    // same Pattern B shape as KnightBuffStunAbilityPD.strengthPercent/knockbackPercent. Granted/reverted
    // alongside the existing three buffs in Activate()/Simulate()'s own end-of-active block, all scaled by
    // the SAME buffStrengthMultiplier above.
    [HideInInspector] public float knockbackPercent = 0f;
    [HideInInspector] public float attackSpeedPercent = 0f;

    private CharacterStatsPD _stats;

    // Flat — not scaled by anything (see class doc comment) beyond the shared buffStrengthMultiplier, so
    // these are just plain snapshots of (field * buffStrengthMultiplier) at cast time, reversed with the
    // identical values at buff-end.
    private float _appliedWeaponRangeTotalPercent;
    private float _appliedExecuteThreshold;
    private float _appliedCritChance;
    private float _appliedKnockbackPercent;
    private float _appliedAttackSpeedPercent;
    // Snapshotted alongside the others — see IAbilityPD.ActiveTimeDuration's own doc comment for why the UI
    // needs this fixed rather than reading the raw duration field live, in case it changes mid-buff.
    private float _appliedDuration;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
    }

    public bool IsReady => _stats != null && !currentState.isActive && currentState.charges > 0;
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
    public AbilityIdentity Identity => AbilityIdentity.ExeBigAxe;
    public int CurrentCharges => currentState.charges;
    public int MaxCharges => EffectiveMaxCharges;
    // Live, not cached — see IAbilityPD.MaxCharges's own doc comment for why a mid-run bonus must be
    // reflected immediately. Reads whichever bonus matches this instance's own (Inspector-configurable)
    // Slot, not a hardcoded one.
    private int EffectiveMaxCharges => authoredChargeCount + (_stats != null
        ? (slot == AbilitySlot.Ability1 ? _stats.bonusAbility1Charges : _stats.bonusAbility2Charges)
        : 0);

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
    // MaxCharges > 1 (2026-10-02) — see Simulate()'s own identical addition for why a multi-charge ability
    // always ticks its regen timer regardless of isActive.
    private bool IsCooldownActivelyTicking =>
        currentState.cooldownTimer > 0f && (cooldownStartsOnCast || !currentState.isActive || EffectiveMaxCharges > 1);

    // See IAbilityPD's own doc comments for the exact contract of each of these three.
    // Shared by all three methods below — a reduction that would drop cooldownTimer below 0 now
    // "overflows" into granting the next charge (looping further if the reduction is large enough to cross
    // multiple charges at once), rather than being clamped away and wasted the instant cooldownTimer first
    // hits 0 (2026-10-03, explicit request — multi-charge abilities should let a big cooldown-reduction
    // effect spill into progress toward the NEXT charge, same as the ordinary passive regen tick already
    // does over many real ticks, just resolved atomically in one call). Caps at EffectiveMaxCharges — once
    // fully charged, any further excess is genuinely discarded, nothing left to speed up. The while loop's
    // own `cooldownTimer > 0f` check (re-tested every iteration) also makes this safe against a degenerate
    // EffectiveCooldown of 0 — the loop exits immediately rather than spinning.
    private void ApplyCooldownReduction(float seconds)
    {
        seconds = Mathf.Max(0f, seconds);
        int effectiveMax = EffectiveMaxCharges;
        while (seconds > 0f && currentState.charges < effectiveMax && currentState.cooldownTimer > 0f)
        {
            if (seconds < currentState.cooldownTimer)
            {
                currentState.cooldownTimer -= seconds;
                seconds = 0f;
            }
            else
            {
                seconds -= currentState.cooldownTimer;
                currentState.charges++;
                currentState.cooldownTimer = currentState.charges < effectiveMax ? EffectiveCooldown : 0f;
            }
        }
    }

    public void ReduceCooldownFlat(float seconds)
    {
        if (!IsCooldownActivelyTicking) return;
        ApplyCooldownReduction(seconds);
    }
    public void ReduceCooldownPercentRemaining(float percent01)
    {
        if (!IsCooldownActivelyTicking) return;
        ApplyCooldownReduction(currentState.cooldownTimer * Mathf.Clamp01(percent01));
    }
    public void ReduceCooldownPercentTotal(float percent01)
    {
        if (!IsCooldownActivelyTicking) return;
        ApplyCooldownReduction(CooldownDuration * Mathf.Clamp01(percent01));
    }

    public void Activate()
    {
        if (!IsReady || _stats == null) return;

        // No AbilityPower scaling (removed 2026-08-23) — see class doc comment. All four buff amounts
        // scaled by the shared buffStrengthMultiplier — see that field's own doc comment. Range ALSO adds
        // rangeMultiplierBonus (ExeBigAxeRangeAugment) directly into the multiplier — see that field's own
        // doc comment for why this is additive with buffStrengthMultiplier rather than a separate factor.
        _appliedWeaponRangeTotalPercent = weaponRangeTotalPercent * (buffStrengthMultiplier + rangeMultiplierBonus);
        _appliedExecuteThreshold        = executeThresholdPercent * buffStrengthMultiplier;
        _appliedCritChance              = critChancePercent * buffStrengthMultiplier;
        _appliedKnockbackPercent        = knockbackPercent * buffStrengthMultiplier;
        _appliedAttackSpeedPercent      = attackSpeedPercent * buffStrengthMultiplier;
        _appliedDuration                = duration;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        // Charge system — spend 1, and only (re)arm the regen timer if it wasn't already running — see
        // IAbilityPD.MaxCharges's own doc comment.
        currentState.charges--;
        if (currentState.cooldownTimer <= 0f) currentState.cooldownTimer = EffectiveCooldown;

        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in Simulate()'s own end-of-active block below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }

        // Direct cross-identity mutation, same pattern as KnightBerserkAbilityPD.Activate() — safe since
        // Activate() is only ever called from PlayerControllerPD's own Simulate(), deterministically,
        // identically on every machine.
        _stats.AddWeaponRangeTotalPercent(_appliedWeaponRangeTotalPercent);
        _stats.AddExecuteThresholdPercent(_appliedExecuteThreshold);
        _stats.AddCritChanceFlat(_appliedCritChance);
        // 0f (no-op) until ExeBigAxeKnockbackAttackSpeedAugment sets knockbackPercent/attackSpeedPercent —
        // see those fields' own doc comment.
        _stats.AddKnockbackPercent(_appliedKnockbackPercent);
        _stats.AddAttackSpeedFlat(_appliedAttackSpeedPercent);

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — see BWBladeDanceAbilityPD's own identical guard
        // for the full reasoning (moot here at the default 30s cooldown, kept for consistency).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true — see IAbilityPD.CancelActiveWindow's own doc comment.
    public void CancelActiveWindow() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Charge system (2026-10-02) — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment.
        int effectiveMax = EffectiveMaxCharges;
        if (effectiveMax > state.knownMaxCharges)
        {
            state.charges += effectiveMax - state.knownMaxCharges;
            state.knownMaxCharges = effectiveMax;
        }
        else if (effectiveMax < state.knownMaxCharges)
        {
            state.charges = Mathf.Min(state.charges, effectiveMax);
            state.knownMaxCharges = effectiveMax;
        }

        if (state.charges < effectiveMax)
        {
            // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole
            // active window below) or only once that window ends (state.isActive false) — see the field's
            // own doc comment. Same shape as KnightBerserkAbilityPD.Simulate().
            // effectiveMax > 1 (2026-10-02) — see RogueVanishAbilityPD.Simulate()'s own identical addition.
            if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive || effectiveMax > 1))
                state.cooldownTimer -= delta;

            if (state.cooldownTimer <= 0f)
            {
                state.charges++;
                state.cooldownTimer = state.charges < effectiveMax ? EffectiveCooldown : 0f;
            }
        }

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
            // RemoveExecuteThresholdPercent, not AddExecuteThresholdPercent(-x) (2026-08-14) — execute
            // threshold now stacks multiplicatively (see its own doc comment), so a straight negated Add no
            // longer correctly inverts what Activate()'s own AddExecuteThresholdPercent call applied.
            _stats.RemoveExecuteThresholdPercent(_appliedExecuteThreshold);
            _stats.AddCritChanceFlat(-_appliedCritChance);
            _stats.AddKnockbackPercent(-_appliedKnockbackPercent);
            _stats.AddAttackSpeedFlat(-_appliedAttackSpeedPercent);
        }
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }
}
