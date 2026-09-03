using PurrNet.Prediction;
using UnityEngine;

// Executioner's second class ability, built on the IAbilityPD architecture — gated by a cooldown, same
// instantaneous-cast shape as ArcherArrowRainAbilityPD/KnightAreaStunHealAbilityPD (no active window at
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
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Axe Throw";
    [SerializeField, TextArea] private string description =
        "Throw an axe dealing 200% Unpenalized Strength that pierces every enemy in its path. Crits deal double crit damage.";
    public float cooldown = 6f;
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

    // Baseline (unrolled) damage — this ability has no AreaEffectPower scaling at all (see class doc
    // comment), so this is a straight preview of strengthPercent * Strength, same "baseline, not a
    // randomly-sampled roll" convention every other Live*Ratio in this codebase uses.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(strengthPercent * stats.Strength)} "
            : "";
    // How far the throw can fly before expiring = true weapon reach * this — see class doc comment.
    [SerializeField] private float maxRangeMultiplier = 3f;
    [SerializeField] private float projectileSpeed    = 20f;
    // Spawn offset in front of the caster, along the throw direction — avoids the projectile immediately
    // self-colliding with the caster's own hurtbox at spawn, same base idea as ShooterGloomPD's own
    // spawnOffset, but not a flat constant: the projectile's own visual model grows with WeaponRange too
    // (see ExeAxeThrowProjectilePD's own rangeScaleTarget), so a fixed offset either buries it back inside
    // the caster at high range or sits oddly close at low range. spawnOffsetBase is the flat starting
    // clearance, scaled by the caster's own bonusSizePercent directly (a bigger caster via Size augments
    // needs proportionally more clearance so the throw doesn't spawn back inside their now-bigger body) —
    // reads the stat itself, not _stats.transform.lossyScale.x (numerically identical for a player root with
    // no scaled parent, since CharacterStatsPD.AddSizePercent sets transform.localScale = Vector3.one *
    // bonusSizePercent uniformly, but the stat is the direct, canonical source rather than reading it back
    // out of the transform). spawnOffsetRangeMultiplier scales separately, additively, with WeaponRange —
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
    // own doc comment), so readiness is purely the cooldown.
    public bool IsReady => currentState.cooldownTimer <= 0f;
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
    public float BaseCooldownDuration => cooldown;
    public bool CooldownStartsOnCast => cooldownStartsOnCast;
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

        currentState.cooldownTimer = EffectiveCooldown;

        // Start and End fire back to back — there's no active window in between (see class doc comment).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);

        if (projectilePrefab != null)
        {
            Vector3 origin = _stats.transform.position;
            Vector3 throwDir = _controller != null && _controller.currentState.cursorDir != Vector3.zero
                ? _controller.currentState.cursorDir
                : transform.forward;

            float trueReach = _weaponManager != null && _weaponManager.TryGetFarthestWeaponReach(out float r)
                ? r
                : _stats.WeaponRange;
            float maxRange = trueReach * maxRangeMultiplier;

            // spawnOffsetBase scales with the caster's own Size (bonusSizePercent) — see that field's own
            // doc comment — so a bigger caster (Size augments) doesn't spawn the throw back inside their
            // own now-bigger body.
            float spawnOffset = spawnOffsetBase * _stats.bonusSizePercent + _stats.WeaponRange * spawnOffsetRangeMultiplier;
            Vector3 spawnPos = origin + throwDir * spawnOffset;

            var id = hierarchy.Create(projectilePrefab, spawnPos, Quaternion.LookRotation(throwDir));
            if (id.HasValue)
            {
                var go = hierarchy.GetGameObject(id.Value);
                if (go != null && go.TryGetComponent(out ExeAxeThrowProjectilePD projectile))
                    // spawnOffset doubles as the return-trip's arrival radius — see
                    // ExeAxeThrowProjectilePD.Launch()'s own doc comment for why.
                    projectile.Launch(_stats, throwDir, projectileSpeed, maxRange, strengthPercent, spawnOffset);
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
        if (state.cooldownTimer > 0f) state.cooldownTimer -= delta;
    }
}
