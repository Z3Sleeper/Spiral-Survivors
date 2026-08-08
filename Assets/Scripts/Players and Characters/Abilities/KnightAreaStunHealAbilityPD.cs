using PurrNet.Prediction;
using UnityEngine;

// Knight's first class ability, built on the IAbilityPD architecture — gated by a cooldown, same shape as
// the two Rogue abilities. Instantaneous: stuns every enemy within range and heals a flat percent of the
// caster's own MaxHealth per enemy actually hit, with no active window at all — IsActive/AbilityIsActive are
// always false (nothing to lock dash/the other ability slot out for, see ForcesSpecialAction below), and
// NotifyAbilityStart/NotifyAbilityEnd fire back to back inside the same Activate() call rather than
// bracketing a real duration.
//
// Radius is the caster's true weapon reach (WeaponManagerPD.TryGetFarthestWeaponReach — same "real hitbox
// edge, tip offset included" tech RogueSpinAbilityPD's own damage capsule and PlayerRangeIndicatorPD's ring
// already use, falling back to the plain WeaponRange stat if no weapon is currently registered), scaled by
// CharacterStatsPD.AreaEffectSize and then by this ability's own flat radiusMultiplier (tuning-only, doesn't
// affect how it responds to weapons/AreaEffectSize). The actual cast — stun + self-heal, no damage — goes through the shared
// "AoE Effects" AreaEffectCastHelper.Cast (dealDamage: false, stun layered on via onEnemyHit same as
// ShieldBreakBurstAugment's own, self-heal via onCastComplete), via a spawned AreaEffectPD chain — the same
// generic chain class DashExplosionAugment/DashHealAugment/ShieldBreakBurstAugment all use, not a
// Knight-specific one — so Stats.AreaEffectCount > 1 gets automatic multi-cast support (retriggerRate apart)
// without this class needing to track that itself. Pooled on the prefab, so the common single-cast case
// (AreaEffectCount == 1, i.e. always unless something grants more) costs a pooled reactivate, not a real spawn.
public class KnightAreaStunHealAbilityPD : PredictedIdentity<KnightAreaStunHealAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float cooldownTimer;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Shockwave";
    [SerializeField, TextArea] private string description = "Stun all nearby enemies for 2 seconds and heal 3% Max Health per enemy hit.";
    public float cooldown = 10f;
    // Exposed for interface uniformity (see IAbilityPD.CooldownStartsOnCast's own doc comment) but currently
    // inert either way — this ability has no active window at all (AbilityIsActive is always false, see
    // class doc comment), so "wait for the active window to end" and "start immediately at cast" collapse to
    // the same behavior: the cooldown always starts ticking the instant Activate() runs (see Simulate()).
    // Ready to become meaningful if this ability ever gains a real active/channeled window later.
    [SerializeField] private bool cooldownStartsOnCast = false;
    // Which action-bar slot this occupies — Inspector-configurable per character rather than hardcoded per
    // class, so which ability goes where is a prefab-authoring choice, not a recompile.
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability1;
    [SerializeField] private string[] tags;
    // Moot in practice — this ability has no active window at all (see class doc comment), so there's
    // nothing for a Surge block to actually bracket. Still exposed for interface consistency — see
    // IAbilityPD.ActiveSurgeBlock's own doc comment.
    [SerializeField] private bool activeSurgeBlock = false;

    [Header("Effect")]
    // Flat multiplier on top of the true-weapon-reach * AreaEffectSize radius below (see Activate()) —
    // exposed purely for tuning "feel" (2x felt right; the base scaling already responds correctly to
    // weapons/AreaEffectSize on its own, this just scales the whole result up/down without touching either
    // of those inputs).
    [SerializeField] private float radiusMultiplier = 2f;
    // Base stun duration BEFORE CharacterStatsPD.AreaEffectPower/AbilityPower scale it (see Activate()) —
    // the two stats' own BONUS portions are summed, not the stats themselves multiplied (see Activate()'s
    // own comment for why), so a 2s base still stuns for exactly 2s at the 100%/100% neutral baseline for
    // both stats, and e.g. +30% AreaEffectPower with +30% AbilityPower stuns for 2s * 1.6 = 3.2s (the two
    // +30% bonuses just add to +60% total).
    public float stunDuration = 2f;
    // Flat percent of the caster's own MaxHealth healed per enemy actually hit, BEFORE AreaEffectPower/
    // AbilityPower scale the total (see Activate()) — passed to Stats.Heal() as a raw amount, same as every
    // other heal source now; Heal() itself rolls luck variance and HealPower/CritHealPower internally, no
    // separate roll needed here.
    public float healPercentPerEnemy = 0.03f;

    // Mirrors this ability's own Activate() lambda formulas exactly (both stats' own bonus-over-neutral
    // portions summed additively, see class doc comment). Outside a live match, falls back to the base
    // field itself — exact at neutral (AreaEffectPower + AbilityPower - 1f == 1 when both stats sit at
    // their own 1.0 baseline, see ArcherFocusFireAbilityPD.LiveDurationRatio's own doc comment for why this
    // is precise, not approximate), so one token covers both contexts.
    public string LiveStunDurationRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? stunDuration * (stats.AreaEffectPower + stats.AbilityPower - 1f)
            : stunDuration;
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(value)} ";
    }

    public string LiveHealPercentRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? healPercentPerEnemy * (stats.AreaEffectPower + stats.AbilityPower - 1f)
            : healPercentPerEnemy;
        return $"{AugmentDescriptionFormatter.FormatLivePercent(value)}";
    }
    // Flat knockback override, NOT derived from Stats.Knockback (unlike ShieldBreakBurstAugment's own
    // Stats.Knockback * multiplier) — this ability's oomph is a fixed push regardless of the caster's own
    // Knockback stat. Passed straight through to AreaEffectCastHelper.Cast's knockback param, which applies
    // independently of dealDamage (see its own doc comment), so this still knocks enemies back even though
    // this ability deals no damage at all.
    [SerializeField] private float knockback = 10f;
    // How long between each retrigger cast when Stats.AreaEffectCount > 1 — exposed for tuning, same
    // convention/default as every other "AoE Effects" family member (DashExplosionAugment/
    // ShieldBreakBurstAugment's own retriggerRate).
    [SerializeField] private float retriggerRate = 0.5f;

    [Header("VFX")]
    // Duration is not exposed here — RadiusIndicatorVFXPD owns its own playback timing on the prefab itself.
    [SerializeField] private GameObject radiusVFXPrefab;

    // Spawned on every cast (see Activate()) — pooled, so this is cheap even when AreaEffectCount is just 1.
    // Must be an AreaEffectPD-carrying prefab registered in PredictedPrefabs.asset — can reuse the exact same
    // prefab DashExplosionAugment/DashHealAugment/ShieldBreakBurstAugment already spawn, since the class
    // itself isn't tied to any one of them, or a separate prefab instance if you'd rather pool it independently.
    [Header("Chain (spawned on every cast)")]
    [SerializeField] private GameObject chainPrefab;

    private CharacterStatsPD _stats;
    private WeaponManagerPD  _weaponManager;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
        _weaponManager = GetComponent<WeaponManagerPD>();
    }

    public Sprite Icon => icon;

    // No isActive concept at all — this ability never has an active window to gate against (see the
    // class's own doc comment), so readiness is purely the cooldown.
    public bool IsReady => currentState.cooldownTimer <= 0f;
    // Always false — there is no active window, instantaneous start-to-end (see class doc comment).
    public bool IsActive => false;
    // Never forces a special action — doesn't touch movement at all, and IsActive is always false anyway.
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    // Always false, for the same reason as IsActive above — NotifyAbilityStart/NotifyAbilityEnd both fire
    // from within the same Activate() call instead of bracketing an observable window.
    public bool AbilityIsActive => false;
    // No active window at all — instantaneous cast, see class doc comment.
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
        // Skipped entirely at 0 (raw, un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cast (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);

        if (chainPrefab != null)
        {
            float reach = _weaponManager != null && _weaponManager.TryGetFarthestWeaponReach(out float r)
                ? r
                : _stats.WeaponRange;
            float radius = reach * _stats.AreaEffectSize * radiusMultiplier;
            int totalCasts = Mathf.Max(1, _stats.AreaEffectCount);
            Vector3 castPosition = _stats.transform.position;

            var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
            if (chainId.HasValue)
            {
                var go = hierarchy.GetGameObject(chainId.Value);
                // dealDamage: false — no damage source at all, just knockback (its own independent gate in
                // Cast(), see its own doc comment for why that no longer requires dealDamage), stun
                // (onEnemyHit, same layering shape ShieldBreakBurstAugment's own Stun uses), and a self-heal
                // based on total enemies hit (onCastComplete). stunDuration/heal scale by AreaEffectPower AND
                // AbilityPower here (in the lambdas) rather than inside the shared helper, since Cast() itself
                // no longer knows anything about "stun"/"heal per hit" specifically. ADDITIVE, not
                // multiplicative, per the user's own call — the two stats' own BONUS portions (each stat
                // minus its own 1.0 neutral baseline) sum into one combined multiplier, same "1 + sum of
                // bonuses" shape every other Total-tier stat in this codebase uses, so neutral (both at
                // 100%/1.0) stays exactly 1x and +30%/+30% together means +60% total, not +160%.
                // interrupt: true — this stun is meant to cancel whatever attack an enemy is mid-sequence on,
                // not just land and wait for it to finish (see EnemyBasePD.Interrupt()'s own doc comment);
                // Cast() applies it before knockback/onEnemyHit for exactly that reason.
                if (go != null && go.TryGetComponent(out AreaEffectPD chain))
                    chain.Launch(_stats, castPosition, totalCasts, retriggerRate, radius, radiusVFXPrefab,
                        dealDamage: false, hasKnockback: true, knockback: knockback, interrupt: true,
                        onEnemyHit: enemy => enemy.Stun(stunDuration * (_stats.AreaEffectPower + _stats.AbilityPower - 1f)),
                        onCastComplete: hitCount =>
                        {
                            if (hitCount <= 0) return;
                            _stats.Heal(hitCount * healPercentPerEnemy * _stats.MaxHealth * (_stats.AreaEffectPower + _stats.AbilityPower - 1f));
                        },
                        vfxPerCast: true);
            }
        }

        if (cooldown > 0f) _stats.NotifyAbilityEnd(this);
    }

    public void NotifyActivationBlocked() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.cooldownTimer > 0f) state.cooldownTimer -= delta;
    }
}
