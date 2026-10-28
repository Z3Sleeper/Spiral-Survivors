using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Executioner's second class ability, built on the IAbilityPD architecture — gated by a cooldown, same
// instantaneous-cast shape as ArcherArrowRainAbilityPD/KnightBuffStunAbilityPD (no active window at
// all — IsActive/AbilityIsActive are always false, NotifyAbilityStart/NotifyAbilityEnd fire back to back
// inside the same Activate() call).
//
// Throws a single ExeAxeThrowProjectilePD straight in the cursor's direction (not clamped to a cast
// distance the way ArcherArrowRainAbilityPD/BWBladeDanceAbilityPD's own target points are — this is a
// throw, not a placement). maxRange (how far the throw is allowed to FLY before expiring) = true weapon
// reach (TryGetFarthestWeaponReach, same "real reach including orbit radius + blade length" value every
// other cast-range ability already uses) * maxRangeMultiplier. The projectile's own physical hitbox size is
// a fixed, prefab-authored constant instead (see ExeAxeThrowProjectilePD's own boxHalfExtents) — WeaponRange
// no longer feeds its size at all, only how far this ability lets it travel.
//
// Damage/knockback/pierce rules all live on ExeAxeThrowProjectilePD itself — see its own class doc comment.
public class ExeAxeThrowAbilityPD : PredictedIdentity<ExeAxeThrowAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float cooldownTimer;
        // Ability charge system — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment.
        public int   charges;
        public int   knownMaxCharges;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Axe Throw";
    [SerializeField, TextArea] private string description =
        "Throw an axe dealing 200% Unpenalized Strength with +500% Unpenalized Knockback that pierces every enemy in its path. Crits deal double crit damage.";
    public float cooldown = 6f;
    // How many charges this ability starts with (1 = old off-cooldown behavior, unchanged) — see
    // IAbilityPD.MaxCharges's own doc comment. CharacterStatsPD.bonusAbility2Charges adds on top live.
    [SerializeField] private int authoredChargeCount = 1;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false — moot here (see class
    // doc comment, AbilityIsActive is always false), kept only for interface/Inspector consistency with
    // every other ability.
    [SerializeField] private bool cooldownStartsOnCast = false;
    // Which action-bar slot this occupies — Inspector-configurable per character rather than hardcoded per
    // class, so which ability goes where is a prefab-authoring choice, not a recompile.
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability2;
    [SerializeField] private string[] tags;
    // Moot in practice — this ability has no active window at all (see class doc comment), so there's
    // nothing for a Surge block to actually bracket. Still exposed for interface consistency — see
    // IAbilityPD.ActiveSurgeBlock's own doc comment.
    [SerializeField] private bool activeSurgeBlock = false;

    [Header("Effect")]
    public float strengthPercent    = 2f;

    // Live multiplier (2026-10-06, ExeAxeThrowKnockbackAugment) — 1f (neutral) default, [HideInInspector]
    // since it's augment-owned. ADDITIVE onto this rather than destructively multiplying strengthPercent
    // directly — see [[feedback_additive_over_multiplicative_default]]. EffectiveStrengthPercent (not the
    // raw field) is what Activate() actually passes into the projectile's own Launch() as its damage
    // baseline.
    [HideInInspector] public float damageMultiplier = 1f;
    // Ability's own innate Knockback baseline (2026-10-08, nerfed same day from 5f/+500% — overshot on
    // first pass, +200% feels right) — NOT augment-owned, hence visible (not [HideInInspector]). Authored
    // as a flat "+2f = +200%" bonus-to-1, same additive-to-neutral shape damageMultiplier above uses.
    public float baseKnockbackBonusPercent = 2f;
    // Augment-owned (2026-10-08, ExeAxeThrowKnockbackAugment) — reworked from an ADDITIVE accumulator
    // (+= 5f onto a 1f baseline) to a genuine MULTIPLICATIVE total, once the ability's own
    // baseKnockbackBonusPercent above made the old flat +500% redundant with what players already had for
    // free ("devalues the augment"). Multiplying the WHOLE existing total (baseline
    // included) instead of adding another flat chunk means the augment's value always scales proportionally
    // with whatever the ability's own baseline happens to be tuned to, rather than shrinking in relative
    // impact if that baseline ever goes up. 1f neutral default (every throw without the augment); the
    // augment itself sets this to 2f ("doubles the multiplier" — nerfed same day from 3f, since even the
    // +200% baseline alone already proved 500% was "super wonky," so 3x on top of that was never getting
    // tested as-is).
    [HideInInspector] public float knockbackMultiplier = 1f;
    private float EffectiveStrengthPercent => strengthPercent * damageMultiplier;
    private float EffectiveKnockbackMultiplier => (1f + baseKnockbackBonusPercent) * knockbackMultiplier;

    // Baseline (unrolled) damage — this ability has no AreaEffectPower scaling at all (see class doc
    // comment), so this is a straight preview of EffectiveStrengthPercent * Strength, same "baseline, not a
    // randomly-sampled roll" convention every other Live*Ratio in this codebase uses.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(EffectiveStrengthPercent * stats.Strength)} "
            : "";
    // How far the throw can fly before expiring = true weapon reach * this — see class doc comment.
    [SerializeField] private float maxRangeMultiplier = 3f;
    [SerializeField] private float projectileSpeed    = 20f;
    // Spawn offset in front of the caster, along the throw direction — avoids the projectile immediately
    // self-colliding with the caster's own hurtbox at spawn, same base idea as ShooterGloomPD's own
    // spawnOffset, but not a flat constant: the projectile's own visual model grows with WeaponRange too
    // (see ExeAxeThrowProjectilePD's own rangeScaleTarget), so a fixed offset either buries it back inside
    // the caster at high range or sits oddly close at low range. spawnOffsetBase is the flat starting
    // clearance, scaled by the caster's own Size directly (a bigger caster via Size augments needs
    // proportionally more clearance so the throw doesn't spawn back inside their now-bigger body) — reads
    // the stat itself, not _stats.transform.lossyScale.x (numerically identical for a player root with no
    // scaled parent, since CharacterStatsPD.AddSizePercent sets transform.localScale = Vector3.one * Size
    // uniformly, but the stat is the direct, canonical source rather than reading it back out of the
    // transform). spawnOffsetRangeMultiplier scales separately, additively, with WeaponRange —
    // same "base + range * multiplier" shape WeaponBasePD.OrbitalPosition's own radius formula uses for the
    // equipped weapon's own clearance (WeaponRadius + character clearance + halfRange).
    //
    // spawnOffsetRangeMultiplier pulled in from 0.5 to 0.375 (25% less) — at high WeaponRange the old 0.5x
    // pushed the spawn point (and therefore the projectile's own sphere hit-check, which only ever starts
    // sweeping FROM that point onward — see ExeAxeThrowProjectilePD.LateSimulate()) far enough out in front
    // of the caster that anything standing close to the caster but slightly off the exact throw-direction
    // line sat in the gap between them and never got swept at all. Not pulled all the way back to 0 — some
    // forward clearance is still needed so the projectile doesn't spawn overlapping the caster's own
    // hurtbox; this just shrinks the blind gap rather than eliminating the clearance's whole purpose. Not
    // Inspector fields — these are tuned-in-play values, not meant to vary per prefab.
    private const float spawnOffsetBase            = 0.5f;
    private const float spawnOffsetRangeMultiplier = 0.375f;

    [Header("Projectile")]
    [SerializeField] private GameObject projectilePrefab;

    // Augment-owned (2026-10-06, ExeAxeThrowChargeAugment) — 1f (neutral) default, [HideInInspector] since
    // it's augment-owned. ADDITIVE onto this multiplier rather than destructively multiplying cooldown
    // itself — see [[feedback_additive_over_multiplicative_default]]. EffectiveCooldown (not the raw
    // cooldown field) is what every consumer (Simulate()'s regen timer, ApplyCooldownReduction,
    // LiveCooldownRatio, CooldownDuration/BaseCooldownDuration) actually reads.
    [HideInInspector] public float cooldownMultiplier = 1f;
    // Augment-owned (2026-10-06, ExeAxeThrowChargeAugment) — Pattern B switch, no-op default false. When
    // true, the charge-regen system below grants ALL missing charges the instant cooldownTimer hits 0,
    // instead of the usual one-charge-at-a-time loop (each completed cooldown only ever refills exactly 1).
    // Checked in BOTH Simulate()'s own natural-regen tick AND ApplyCooldownReduction's artificial-speedup
    // loop — the mechanic is "a completed cooldown means full charges," not just "the passive tick
    // specifically," so an external cooldown-reduction effect crossing this ability's threshold gets the
    // same full-restore treatment rather than looping one charge at a time toward it.
    [HideInInspector] public bool restoreAllChargesOnCooldown = false;

    // Augment-owned (2026-10-06, ExeAxeThrowMiniAxesAugment) — all no-op defaults (miniAxeCount 0 is both
    // the gate AND the quantity, so no separate enable bool is needed; the four percent fields default to
    // 1f/neutral and are only ever meaningful once miniAxeCount > 0). Fixed values rather than live
    // additive-stacking multipliers — unlike cooldownMultiplier/knockbackMultiplier/damageMultiplier above,
    // there's only ever ONE mini-axe-granting augment, with nothing else to compose with, so a plain
    // assignment in that single augment's own OnApply() is enough (no cross-augment concern — see
    // [[project_augments_never_duplicate_granted]]). See Activate()'s own mini-axe spawn block for how these
    // feed into ExeAxeThrowProjectilePD.Launch()'s existing parameters.
    [HideInInspector] public int   miniAxeCount = 0;
    [HideInInspector] public float miniAxeArcDegrees = 0f;
    [HideInInspector] public float miniAxeSizePercent = 1f;
    [HideInInspector] public float miniAxeRangePercent = 1f;
    [HideInInspector] public float miniAxeDamagePercent = 1f;
    [HideInInspector] public float miniAxeKnockbackPercent = 1f;
    [HideInInspector] public float miniAxeOnHitPowerPercent = 1f;

    // Augment-owned (2026-10-06, ExeAxeThrowHealAugment) — both 0f (true no-op) default. Additive flat+
    // percent heal formula, same shape every other heal augment in this codebase uses — no multiplicative
    // stacking concern to design around here at all, unlike the Knockback/MiniAxes augments' own
    // multiplier fields. Passed into the main throw's own Launch() call at full value, and HALVED for each
    // mini axe (explicit design: "miniaxes only heal half this amount") — see Activate()'s own
    // mini-axe spawn block.
    [HideInInspector] public float healFlat = 0f;
    [HideInInspector] public float healRegenPercent = 0f;

    // Shared scratch list for MultishotMath.ComputeAngles — same "shared, immediately drained within one
    // synchronous call" convention as WeaponRangedPD's own _multishotAngles.
    private static readonly List<float> _miniAxeAngles = new();

    private CharacterStatsPD   _stats;
    private WeaponManagerPD    _weaponManager;
    private PlayerControllerPD _controller;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats         = GetComponent<CharacterStatsPD>();
        _weaponManager = GetComponent<WeaponManagerPD>();
        _controller    = GetComponent<PlayerControllerPD>();
    }

    public Sprite Icon => icon;

    // No isActive concept at all — this ability never has an active window to gate against (see the class's
    // own doc comment), so readiness is purely whether a charge is available.
    public bool IsReady => currentState.charges > 0;
    public bool IsActive => false;
    // Never forces a special action — doesn't touch movement at all, and IsActive is always false anyway.
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    public bool AbilityIsActive => false;
    // No active window at all — see class doc comment.
    public bool HasActiveWindow => false;
    public float ActiveTimeRemaining => 0f;
    public float ActiveTimeDuration => 0f;
    public float CooldownRemaining => Mathf.Max(0f, currentState.cooldownTimer);
    public float CooldownDuration => EffectiveCooldown;
    // Reflects cooldownMultiplier too (not just the raw field) — this augment genuinely raises the TRUE
    // base cooldown, not just an after-haste effective value, so the UI's own "base cooldown" display
    // should show the real new base.
    public float BaseCooldownDuration => cooldown * cooldownMultiplier;
    public bool CooldownStartsOnCast => cooldownStartsOnCast;
    public string Name => abilityName;
    public string Description => description;
    public string[] Tags => tags;
    public AbilitySlot Slot => slot;
    public AbilityIdentity Identity => AbilityIdentity.ExeAxeThrow;
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
    // EffectiveAbilityHaste ever goes negative (a penalty). cooldown * cooldownMultiplier (not the raw
    // field) — see that multiplier's own doc comment.
    private float EffectiveCooldown => _stats != null ? cooldown * cooldownMultiplier / (1f + _stats.EffectiveAbilityHaste / 100f) : cooldown * cooldownMultiplier;

    // Mirrors EffectiveCooldown's own formula exactly. Outside a live match, falls back to the base*multiplier
    // value itself — exact at neutral EffectiveAbilityHaste (0, i.e. cooldown / (1+0/100) == cooldown), same
    // "neutral stat value == the base field" convention as every other Live*Ratio in this file.
    public string LiveCooldownRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? cooldown * cooldownMultiplier / (1f + stats.EffectiveAbilityHaste / 100f)
            : cooldown * cooldownMultiplier;
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(value)} ";
    }

    // See IAbilityPD's own doc comments for the exact contract of each of these three.
    // Shared by all three methods below — see the "with gate" ability files' own identical doc comment
    // (e.g. KnightBerserkAbilityPD.ApplyCooldownReduction) for the full overflow-into-next-charge reasoning
    // (2026-10-03, explicit request). No IsCooldownActivelyTicking gate needed here — this ability has no
    // active window at all (see class doc comment), so cooldown always ticks/can always be reduced.
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
                // See restoreAllChargesOnCooldown's own doc comment — a completed cooldown means FULL
                // charges under that mode, not just +1, so any further excess reduction is genuinely
                // discarded here too (nothing left to speed up once already fully charged).
                if (restoreAllChargesOnCooldown)
                {
                    currentState.charges = effectiveMax;
                    currentState.cooldownTimer = 0f;
                    seconds = 0f;
                }
                else
                {
                    currentState.charges++;
                    currentState.cooldownTimer = currentState.charges < effectiveMax ? EffectiveCooldown : 0f;
                }
            }
        }
    }

    public void ReduceCooldownFlat(float seconds) => ApplyCooldownReduction(seconds);
    public void ReduceCooldownPercentRemaining(float percent01) =>
        ApplyCooldownReduction(currentState.cooldownTimer * Mathf.Clamp01(percent01));
    public void ReduceCooldownPercentTotal(float percent01) =>
        ApplyCooldownReduction(CooldownDuration * Mathf.Clamp01(percent01));

    public void Activate()
    {
        if (!IsReady || _stats == null) return;

        // Charge system — spend 1, and only (re)arm the regen timer if it wasn't already running — see
        // IAbilityPD.MaxCharges's own doc comment.
        currentState.charges--;
        if (currentState.cooldownTimer <= 0f) currentState.cooldownTimer = EffectiveCooldown;

        // Start and End fire back to back — there's no active window in between (see class doc comment).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);

        if (projectilePrefab != null)
        {
            Vector3 origin = _stats.transform.position;
            Vector3 throwDir = _controller != null && _controller.currentState.cursorDir != Vector3.zero
                ? _controller.currentState.cursorDir
                : transform.forward;

            // EffectiveWeaponRange fallback (2026-09-16, was plain WeaponRange) — TryGetFarthestWeaponReach's
            // own GetOuterReach() is itself EffectiveWeaponRange-based real weapon geometry, so this fallback
            // (only reached with zero weapons equipped at all) matches that same real-world-distance scale
            // instead of the uncompressed player-facing stat number.
            float trueReach = _weaponManager != null && _weaponManager.TryGetFarthestWeaponReach(out float r)
                ? r
                : _stats.EffectiveWeaponRange;
            float maxRange = trueReach * maxRangeMultiplier;

            // spawnOffsetBase scales with the caster's own Size — see that field's own doc comment — so a
            // bigger caster (Size augments) doesn't spawn the throw back inside their own now-bigger body.
            // RangeWorldUnits (2026-09-16, was plain WeaponRange) — this is the ABILITY's own spawn
            // positioning, not weapon geometry, so it reads the TRUE (un-Archer/Bladeweaver-scaled) Range
            // stat as a world-space distance, same "class ability reads true Range" convention
            // EffectiveWeaponRange's own doc comment establishes.
            float spawnOffset = spawnOffsetBase * _stats.Size + _stats.RangeWorldUnits * spawnOffsetRangeMultiplier;
            Vector3 spawnPos = origin + throwDir * spawnOffset;

            var id = hierarchy.Create(projectilePrefab, spawnPos, Quaternion.LookRotation(throwDir));
            if (id.HasValue)
            {
                var go = hierarchy.GetGameObject(id.Value);
                if (go != null && go.TryGetComponent(out ExeAxeThrowProjectilePD projectile))
                    // spawnOffset doubles as the return-trip's arrival radius — see
                    // ExeAxeThrowProjectilePD.Launch()'s own doc comment for why. EffectiveStrengthPercent/
                    // EffectiveKnockbackMultiplier/healFlat/healRegenPercent — see those fields' own doc comment.
                    projectile.Launch(_stats, throwDir, projectileSpeed, maxRange, EffectiveStrengthPercent, spawnOffset, EffectiveKnockbackMultiplier,
                        healFlat: healFlat, healRegenPercent: healRegenPercent);
            }

            // ExeAxeThrowMiniAxesAugment (2026-10-06) — miniAxeCount 0 (no-op default) skips this entirely.
            // Same fan-angle math WeaponRangedPD.Fire() uses for its own Multishot (MultishotMath.
            // ComputeAngles), just with a fixed count/arc instead of the player's own Multishot stat —
            // index 0 of the result is always the straight 0° angle, which the main throw above already
            // covers, so this skips it and only spawns the extra fanned-out angles. All mini axes share the
            // SAME spawnPos/maxRange/spawnOffset the main axe just used (rotating only the throw DIRECTION
            // per angle), same "shared origin point, each shot gets its own direction" shape multishot
            // itself already uses.
            if (miniAxeCount > 0)
            {
                MultishotMath.ComputeAngles(miniAxeCount, miniAxeArcDegrees, _miniAxeAngles);
                for (int i = 1; i < _miniAxeAngles.Count; i++)
                {
                    Vector3 miniDir = Quaternion.Euler(0f, _miniAxeAngles[i], 0f) * throwDir;
                    var miniId = hierarchy.Create(projectilePrefab, spawnPos, Quaternion.LookRotation(miniDir));
                    if (!miniId.HasValue) continue;

                    var miniGo = hierarchy.GetGameObject(miniId.Value);
                    if (miniGo != null && miniGo.TryGetComponent(out ExeAxeThrowProjectilePD miniProjectile))
                        // healFlat/healRegenPercent halved — "miniaxes only heal half this amount" (explicit
                        // design spec), a fixed ratio independent of miniAxeDamagePercent/whatever else
                        // this augment's own values happen to be tuned to.
                        miniProjectile.Launch(_stats, miniDir, projectileSpeed, maxRange * miniAxeRangePercent,
                            EffectiveStrengthPercent * miniAxeDamagePercent, spawnOffset,
                            EffectiveKnockbackMultiplier * miniAxeKnockbackPercent, miniAxeSizePercent, miniAxeOnHitPowerPercent,
                            healFlat * 0.5f, healRegenPercent * 0.5f);
                }
            }
        }

        if (cooldown > 0f) _stats.NotifyAbilityEnd(this);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true — see IAbilityPD.CancelActiveWindow's own doc comment.
    public void CancelActiveWindow() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Charge system (2026-10-02) — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment. No
        // isActive/cooldownStartsOnCast interplay to preserve here (this ability has no active window at
        // all), so the regen timer just always ticks while below max.
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
            if (state.cooldownTimer > 0f) state.cooldownTimer -= delta;

            if (state.cooldownTimer <= 0f)
            {
                // See restoreAllChargesOnCooldown's own doc comment — full restore instead of the usual
                // one-at-a-time regen when that augment-owned switch is on.
                if (restoreAllChargesOnCooldown)
                    state.charges = effectiveMax;
                else
                    state.charges++;
                state.cooldownTimer = state.charges < effectiveMax ? EffectiveCooldown : 0f;
            }
        }
    }
}
