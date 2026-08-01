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
// Two independent scaling multipliers, both AreaEffectPower AND AbilityPower, ADDITIVELY combined (each
// stat's own bonus portion above its 1.0 neutral baseline sums with the other's, rather than the two
// multiplying together — see AreaEffectCastHelper.Cast's own doc comment for the exact shape and why):
// - Damage: AreaEffectPower is applied automatically inside Cast() itself (every "AoE Effects" family member
//   gets this for free); AbilityPower is passed straight through to Launch()'s own abilityPower parameter
//   (see SpawnChain) so the shared helper does the additive combine itself, rather than this ability
//   pre-multiplying its own separate layer into strengthPercentPerTick.
// - Slow: slowPercent (already "how much slow," an Inspector-friendly percent — see its own doc comment)
//   gets scaled by both stats directly (same additive combine, hand-rolled here since Slow isn't something
//   AreaEffectCastHelper knows about), then converted to the "speed remains" multiplier EnemyBasePD.Slow()
//   actually takes right at the end — see SpawnChain's own comment. EnemyBasePD.Slow's own 90%-max clamp
//   means it's safe to hand it an arbitrarily large scaled value without checking here first.
//
// Stats.AreaEffectCount is handled as its own, OUTER tier of casting — "recast the entire rain again at the
// same location" (not a fresh cursor read), spaced recastRetriggerRate apart — rather than folded into
// tickCount/tickInterval (the INNER, fixed-by-design 20-tick DoT this ability's whole spec is built around).
// Implemented via this ability's own predicted State (recastsRemaining/recastTimer/castPosition) rather than
// AreaEffectPD's own single-tier retrigger, since that only supports one tier of repeated casts, not "repeat
// an already-repeating thing again."
public class ArcherArrowRainAbilityPD : PredictedIdentity<ArcherArrowRainAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float cooldownTimer;
        // Outer-recast bookkeeping — see class doc comment for why Stats.AreaEffectCount is handled here,
        // as a second tier of casting, rather than folded into tickCount/tickInterval.
        public int     recastsRemaining;
        public float   recastTimer;
        public Vector3 castPosition;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Arrow Rain";
    [SerializeField, TextArea] private string description =
        "Fire a rain of arrows at the target location, dealing 500% Unpenalized Strength over the duration and slowing enemies.";
    [SerializeField] private float cooldown = 10f;
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
    [SerializeField] private float strengthPercentPerTick = 0.25f;
    // How much slow this applies, as a straight percent (0.8 = 80% slower, i.e. 20% speed remains) — not
    // the "how much speed remains" multiplier EnemyBasePD.Slow() itself takes, converted at the call site
    // in SpawnChain instead so this field reads the way you'd actually want to type a value into it.
    // Reapplied on every tick an enemy is still standing in the zone, so it effectively stays refreshed the
    // whole time they remain inside, not just once on first contact. Scaled by AreaEffectPower/AbilityPower
    // at cast time — see SpawnChain's own comment.
    [SerializeField] private float slowPercent = 0.35f;
    [SerializeField] private float slowDuration = 0.5f;
    // How long between each outer recast (a full repeat of the entire rain, at the same cast location) when
    // Stats.AreaEffectCount > 1 — see class doc comment for why this is a separate tier from tickInterval.
    [SerializeField] private float recastRetriggerRate = 0.5f;

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

    public void Activate()
    {
        if (!IsReady || _stats == null) return;

        currentState.cooldownTimer = EffectiveCooldown;

        // Start and End fire back to back — there's no active window in between (see class doc comment).
        // The rain itself (including any outer recasts) keeps going in the background via Simulate() below.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cast (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);

        Vector3 origin = _stats.transform.position;

        float trueReach = (_weaponManager != null && _weaponManager.TryGetFarthestWeaponReach(out float r)
            ? r
            : _stats.WeaponRange) * castRangeMultiplier;

        // Clamped to trueReach, not just aimed toward it — Mathf.Min naturally covers both halves of the
        // spec: cursor within range casts exactly at the cursor's own point, cursor beyond range casts at
        // max range in that same direction. cursorDir/cursorDistance come from PlayerControllerPD's own
        // predicted state (captured once, owner-only, from real input — see its own doc comments), so every
        // machine resimulates this cast at the identical point.
        Vector3 cursorDir      = _controller != null ? _controller.currentState.cursorDir : Vector3.forward;
        float   cursorDistance = _controller != null ? _controller.currentState.cursorDistance : 0f;
        float   castDistance   = Mathf.Min(cursorDistance, trueReach);
        Vector3 castPosition   = origin + cursorDir * castDistance;

        currentState.castPosition = castPosition;
        SpawnChain(castPosition);

        // AreaEffectCount recasts the WHOLE rain again at this SAME location (not a fresh cursor read),
        // spaced recastRetriggerRate apart — ticked down in Simulate() below. totalCasts - 1 since this
        // Activate() call already fired the first one, same "immediate cast now, N-1 more retriggers"
        // convention AreaEffectPD's own totalCasts uses.
        int totalCasts = Mathf.Max(1, _stats.AreaEffectCount);
        currentState.recastsRemaining = totalCasts - 1;
        currentState.recastTimer = recastRetriggerRate;

        if (cooldown > 0f) _stats.NotifyAbilityEnd(this);
    }

    public void NotifyActivationBlocked() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.cooldownTimer > 0f) state.cooldownTimer -= delta;

        if (state.recastsRemaining > 0)
        {
            state.recastTimer -= delta;
            if (state.recastTimer <= 0f)
            {
                SpawnChain(state.castPosition);
                state.recastsRemaining--;
                if (state.recastsRemaining > 0) state.recastTimer = recastRetriggerRate;
            }
        }
    }

    // Shared by the initial cast (Activate()) and every outer AreaEffectCount recast (Simulate()) — both
    // spawn an identical chain at the given position, just at different times, so the actual cast logic
    // lives in exactly one place. Reads Stats live each time rather than snapshotting at Activate() — a
    // recast picking up a stat change from an augment picked in between is correct here (each cast is a
    // fully self-contained instantaneous burst with nothing to reverse later), unlike RogueVanishAbilityPD/
    // KnightBigBuffAbilityPD's own cast-time snapshots, which exist specifically to keep an add/subtract
    // pair in lockstep — there's no such pair here.
    private void SpawnChain(Vector3 position)
    {
        if (chainPrefab == null) return;

        float radius = castRadius * _stats.AreaEffectSize;

        // AreaEffectPower is already applied automatically inside Cast() itself (every "AoE Effects" family
        // member gets this for free); AbilityPower is passed straight through to Launch() below instead of
        // being pre-multiplied in here, so the two combine ADDITIVELY inside the shared helper (see
        // AreaEffectCastHelper.Cast's own doc comment on its abilityPower parameter) rather than this
        // ability hand-rolling its own separate multiplicative layer on top.

        // Slow strength (not duration) scales with AreaEffectPower AND AbilityPower too — scaling those UP
        // means MORE slow, so this scales slowPercent (already "how much slow," see its own doc comment)
        // directly, then converts to the "speed remains" multiplier EnemyBasePD.Slow() actually takes right
        // at the end. EnemyBasePD.ApplySlow's own 90%-max clamp means this is safe to hand an arbitrarily
        // large scaled value without checking here first. ADDITIVE, not multiplicative, per the user's own
        // call — the two stats' own BONUS portions (each stat minus its own 1.0 neutral baseline) sum
        // together into one combined multiplier, same "1 + sum of bonuses" shape every other Total-tier stat
        // in this codebase already uses, rather than compounding as two separate multipliers. +30%
        // AreaEffectPower and +30% AbilityPower together means +60% total (1 + 0.3 + 0.3 = 1.6x), and
        // neutral (both at 100%/1.0, no bonuses) stays exactly 1x — NOT literally AreaEffectPower +
        // AbilityPower added together raw, which would double the baseline even with zero bonuses on either
        // stat (a first attempt at this got that wrong).
        float scaledSlowAmount    = slowPercent * (_stats.AreaEffectPower + _stats.AbilityPower - 1f);
        float scaledSlowMultiplier = 1f - scaledSlowAmount;

        var chainId = hierarchy.Create(chainPrefab, position, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        // notifyOnHit: false — no onhit-triggered effects fire from this ability's own damage ticks. No
        // hasKnockback passed — defaults to false (no knockback at all), matching this ability's own spec.
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(_stats, position, tickCount, tickInterval, radius, radiusVFXPrefab,
                dealDamage: true, damagePercent: strengthPercentPerTick, notifyOnHit: false,
                abilityPower: _stats.AbilityPower,
                onEnemyHit: enemy => enemy.Slow(scaledSlowMultiplier, slowDuration));
    }
}
