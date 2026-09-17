using PurrNet.Prediction;
using UnityEngine;

// Archer's first class ability, built on the IAbilityPD architecture — gated by a cooldown, same shape as
// KnightAreaStunHealAbilityPD (instantaneous cast, no active window at all — IsActive/AbilityIsActive are
// always false, NotifyAbilityStart/NotifyAbilityEnd fire back to back inside the same Activate() call).
// Unlike Knight's Shockwave, this doesn't cast centered on the caster — it casts at a target point out in
// front of them, clamped to this ability's own cast range (see Activate()'s own comment for the clamp math),
// then spawns a long-tail damage-over-time zone there via the shared "AoE Effects" AreaEffectPD chain
// (dealDamage: true, strengthPercentPerTick per tick — luck/highroll/crit variance rolled automatically by
// the shared Cast() helper, same as every other damage-dealing effect in this codebase, so the ability's own
// "500%*" spec is satisfied for free — notifyOnHit: false, no onhit triggers, no hasKnockback param passed
// at all so it defaults to false/no knockback, and a Slow layered on via onEnemyHit, same shape
// ShieldBreakBurstAugment's own Stun/KnightAreaStunHealAbilityPD's own Stun use). tickCount/tickInterval are
// chosen so the total damage dealt over one full rain comes out to exactly the ability's stated 500%
// Unpenalized Strength (20 ticks * 25% = 500%, spaced 0.25s apart = 5s total).
//
// AreaEffectPower/AreaEffectCount (the damage/onhit-power multiplier and the multi-cast repeat count) were
// removed entirely (2026-09-13) — this ability's damage/slow are now its own plain, unscaled baseline, and it
// only ever casts once per Activate(), no outer recast tier anymore.
public class ArcherArrowRainAbilityPD : PredictedIdentity<ArcherArrowRainAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float cooldownTimer;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Arrow Rain";
    [SerializeField, TextArea] private string description =
        "Fire a rain of arrows at the target location, dealing 500% Unpenalized Strength over the duration and slowing enemies.";
    public float cooldown = 10f;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false — moot here (see class
    // doc comment, AbilityIsActive is always false), kept only for interface/Inspector consistency with
    // every other ability.
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
    // Cast range = true weapon reach * this — 2x by default, so the rain can be dropped well past actual
    // weapon range instead of being clamped to it exactly (see Activate()'s own clamp math).
    [SerializeField] private float castRangeMultiplier = 2f;
    // Radius of the actual rain zone at the cast point — scaled by Stats.AreaEffectSize like every other
    // "AoE Effects" family member.
    [SerializeField] private float castRadius = 4f;
    // 20 ticks * 25% = 500% total, spaced 0.25s apart = 5s total duration — see class doc comment for why
    // this is a fixed pair rather than derived from Stats.AreaEffectCount.
    [SerializeField] private int tickCount = 20;
    [SerializeField] private float tickInterval = 0.25f;
    public float strengthPercentPerTick = 0.25f;

    // Static total (tickCount * strengthPercentPerTick, e.g. 20*0.25=5=500%) — Percent-suffixed so
    // AugmentDescriptionFormatter auto-*100s it, for a description's static "(500%)"-style fallback
    // alongside the live number below.
    public float TotalDamagePercent => tickCount * strengthPercentPerTick;

    // Summed across the whole rain, minus the luck/crit roll each tick independently rolls — baseline
    // preview, not a randomly-sampled one, same convention as every augment's own Live*Ratio.
    public string LiveDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(tickCount * strengthPercentPerTick * stats.Strength)} "
            : "";

    public string LiveSlowRatio(CharacterStatsPD stats) =>
        $"{AugmentDescriptionFormatter.FormatLivePercent(slowPercent)}";
    // How much slow this applies, as a straight percent (0.8 = 80% slower, i.e. 20% speed remains) — not
    // the "how much speed remains" multiplier EnemyBasePD.Slow() itself takes, converted at the call site
    // in SpawnChain instead so this field reads the way you'd actually want to type a value into it.
    // Reapplied on every tick an enemy is still standing in the zone, so it effectively stays refreshed the
    // whole time they remain inside, not just once on first contact.
    public float slowPercent = 0.35f;
    [SerializeField] private float slowDuration = 0.5f;

    [Header("VFX")]
    // Duration is not exposed here — RadiusIndicatorVFXPD owns its own playback timing on the prefab itself.
    [SerializeField] private GameObject radiusVFXPrefab;

    // Must be an AreaEffectPD-carrying prefab registered in PredictedPrefabs.asset — can reuse the exact
    // same prefab DashExplosionAugment/DashHealAugment/ShieldBreakBurstAugment/KnightAreaStunHealAbilityPD
    // already spawn, since the class itself isn't tied to any one of them, or a separate prefab instance if
    // you'd rather pool it independently.
    [Header("Chain (spawned on every cast)")]
    [SerializeField] private GameObject chainPrefab;

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
        // The rain itself keeps ticking in the background via the spawned AreaEffectPD chain's own Simulate()
        // (this ability's own Simulate() below only ticks its cooldown). Skipped entirely at 0 (raw,
        // un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cast (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);

        Vector3 origin = _stats.transform.position;

        // EffectiveWeaponRange fallback (2026-09-16, was plain WeaponRange) — matches TryGetFarthestWeaponReach's
        // own EffectiveWeaponRange-based GetOuterReach() scale, same reasoning ExeAxeThrowAbilityPD's own
        // identical trueReach fallback uses.
        float trueReach = (_weaponManager != null && _weaponManager.TryGetFarthestWeaponReach(out float r)
            ? r
            : _stats.EffectiveWeaponRange) * castRangeMultiplier;

        // Clamped to trueReach, not just aimed toward it — Mathf.Min naturally covers both halves of the
        // spec: cursor within range casts exactly at the cursor's own point, cursor beyond range casts at
        // max range in that same direction. cursorDir/cursorDistance come from PlayerControllerPD's own
        // predicted state (captured once, owner-only, from real input — see its own doc comments), so every
        // machine resimulates this cast at the identical point.
        Vector3 cursorDir      = _controller != null ? _controller.currentState.cursorDir : Vector3.forward;
        float   cursorDistance = _controller != null ? _controller.currentState.cursorDistance : 0f;
        float   castDistance   = Mathf.Min(cursorDistance, trueReach);
        Vector3 castPosition   = origin + cursorDir * castDistance;

        SpawnChain(castPosition);

        if (cooldown > 0f) _stats.NotifyAbilityEnd(this);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true, so PlayerControllerPD.CancelSurgeBlockingAbility() never has
    // a real reason to call this — see IAbilityPD.CancelActiveWindow's own doc comment.
    public void CancelActiveWindow() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.cooldownTimer > 0f) state.cooldownTimer -= delta;
    }

    // Called once per Activate() — the whole rain is a single cast now, no outer recast tier anymore.
    private void SpawnChain(Vector3 position)
    {
        if (chainPrefab == null) return;

        float radius = castRadius * _stats.AreaEffectSize;
        float slowMultiplier = 1f - slowPercent;

        var chainId = hierarchy.Create(chainPrefab, position, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        // notifyOnHit: false — no onhit-triggered effects fire from this ability's own damage ticks. No
        // hasKnockback passed — defaults to false (no knockback at all), matching this ability's own spec.
        // overrideHitSfx: true, hitSfxOverride left null — silent. Up to 20 ticks * every enemy caught in the
        // zone would otherwise spam the caster's hitSfx into an unlistenable wall of noise (see
        // HitFeedbackData's own doc comment for the general mechanism this uses).
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(_stats, position, radius, radiusVFXPrefab,
                dealDamage: true, damagePercent: strengthPercentPerTick, notifyOnHit: false,
                onEnemyHit: enemy => enemy.Slow(slowMultiplier, slowDuration),
                overrideHitSfx: true, totalCasts: tickCount, retriggerRate: tickInterval);
    }
}
