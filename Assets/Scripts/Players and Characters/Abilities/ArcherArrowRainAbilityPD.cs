using PurrNet.Prediction;
using UnityEngine;

// Archer's first class ability, built on the IAbilityPD architecture — gated by a cooldown, same shape as
// KnightBuffStunAbilityPD (instantaneous cast, no active window at all — IsActive/AbilityIsActive are
// always false, NotifyAbilityStart/NotifyAbilityEnd fire back to back inside the same Activate() call).
// Unlike Knight's Shockwave, this doesn't cast centered on the caster — it casts at a target point out in
// front of them, clamped to this ability's own cast range (see Activate()'s own comment for the clamp math),
// then spawns a long-tail damage-over-time zone there via the shared "AoE Effects" AreaEffectPD chain
// (dealDamage: true, TickDamagePercent per tick — luck/highroll/crit variance rolled automatically by
// the shared Cast() helper, same as every other damage-dealing effect in this codebase — notifyOnHit: false,
// no onhit triggers, no hasKnockback param passed at all so it defaults to false/no knockback, and a Slow
// layered on via onEnemyHit, same shape ShieldBreakBurstAugment's own Stun/KnightBuffStunAbilityPD's own
// Stun use). Authored as duration/strengthPerSecond/tickInterval (2026-10-05, replaces the old
// tickCount/strengthPercentPerTick pair — see FullTickCount/FinalTickPercent's own doc comments), so total
// damage is simply strengthPerSecond * duration, chunked into tickInterval-apart ticks with any leftover
// remainder squeezed into one final pro-rated tick rather than dropped or rounded away.
//
// AreaEffectPower/AreaEffectCount (the damage/onhit-power multiplier and the multi-cast repeat count) were
// removed entirely (2026-09-13) — this ability's damage/slow are now its own plain, unscaled baseline, and it
// only ever casts once per Activate(), no outer recast tier anymore.
public class ArcherArrowRainAbilityPD : PredictedIdentity<ArcherArrowRainAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float cooldownTimer;
        // Ability charge system — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment.
        public int   charges;
        public int   knownMaxCharges;
        // Growth support (2026-10-05, ArcherArrowRainGrowthAugment) — see growthPercentPerSecond's own
        // doc comment. activeChainId tracks only the MOST RECENTLY cast chain (same "last write wins"
        // limitation as MortarGloomPD.pendingHazardId) — an overlapping second cast before the first one's
        // duration elapses would silently stop growing the first chain. Accepted limitation (flagged to
        // Stuart) rather than building true N-chain tracking for what's currently a hypothetical edge case
        // — see the 4-player multiplayer scale reminder on why that'd be real added per-tick cost for every
        // player, every frame, for a case that may never come up. Safe to store directly (non-nullable) —
        // PredictedObjectID is a plain IPackedAuto struct, same precedent as MortarGloomPD's own field.
        public PredictedObjectID activeChainId;
        public float growthElapsedTime;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Arrow Rain";
    [SerializeField, TextArea] private string description =
        "Fire a rain of arrows at the target location, dealing 500% Unpenalized Strength over the duration and slowing enemies.";
    public float cooldown = 10f;
    // How many charges this ability starts with (1 = old off-cooldown behavior, unchanged) — see
    // IAbilityPD.MaxCharges's own doc comment. CharacterStatsPD.bonusAbility1Charges adds on top live.
    [SerializeField] private int authoredChargeCount = 1;
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
    // User-facing trio (2026-10-05, replaces the old tickCount/strengthPercentPerTick pair — see
    // FullTickCount/SpawnChain's own doc comments for the derivation): duration * strengthPerSecond is the
    // TOTAL damage over the whole rain (e.g. 5.25 * 0.6 = 3.15 = 315%, matching this ability's own current
    // live-authored tuning), chunked into tickInterval-apart ticks. Duration doesn't need to be an exact
    // multiple of tickInterval — see FullTickCount/FinalTickPercent for how a remainder becomes one final,
    // pro-rated tick instead of being dropped or rounded away.
    public float duration = 5.25f;
    public float strengthPerSecond = 0.6f;
    [SerializeField] private float tickInterval = 0.25f;

    // Live damage multiplier (2026-10-05, ArcherArrowRainGrowthAugment and any future ArrowRain-specific
    // "+X% damage" augment) — 1f (neutral) default, [HideInInspector] since it's augment-owned, never
    // hand-tuned here. Augments ADD to this (+0.25 each) rather than destructively multiplying
    // strengthPerSecond directly, so two DIFFERENT "+25% damage" augments (augments are never granted more
    // than once each — no same-augment repeat-pick case to worry about) stack to +50% (1.5x) total, not
    // 1.25*1.25=1.5625x. Read live by TickDamagePercent below (strengthPerSecond itself stays the pure
    // authored base value, never mutated), same "live multiplier the ability applies every read" shape
    // KnightBuffStunAbilityPD.buffStrengthMultiplier uses, just additive instead of multiplicative stacking.
    [HideInInspector] public float damageMultiplier = 1f;

    // Live duration multiplier (2026-10-05, ArcherArrowRainDurationAugment) — same additive-stacking shape
    // as damageMultiplier right above (1f neutral default, [HideInInspector], augments ADD to it rather
    // than mutating duration itself). EffectiveDuration (not the raw duration field) is what every tick-
    // count/remainder/VFX-duration/growth-cap calculation below actually uses, so a duration bonus
    // automatically lengthens the rain, adds however many extra ticks that implies, AND extends the growth
    // window (ArcherArrowRainGrowthAugment) for free — no separate interaction code needed between the two
    // augments.
    [HideInInspector] public float durationMultiplier = 1f;
    private float EffectiveDuration => duration * durationMultiplier;

    // Full tickInterval-spaced steps that fit inside EffectiveDuration AFTER the instant cast-time tick —
    // e.g. EffectiveDuration=5.25/tickInterval=0.25 = 21 steps (no remainder), landing at t=tickInterval,
    // 2*tickInterval, ..., 21*tickInterval==EffectiveDuration exactly. Replaces the old authored tickCount
    // field entirely; always derived, never hand-set, so it can never drift out of sync with
    // duration/tickInterval the way a separately-authored tickCount could.
    private int FullTickCount => Mathf.FloorToInt(EffectiveDuration / tickInterval);
    // Leftover time that doesn't fill a whole extra step — e.g. EffectiveDuration=0.3/tickInterval=0.25
    // leaves 0.05 (a 0.05/0.25 = 20% partial tick) after the 1 full step. Near-zero (EffectiveDuration an
    // exact multiple of tickInterval, e.g. the live-authored neutral 5.25/0.25 above) means no partial tick
    // at all.
    private float RemainderSeconds => EffectiveDuration - FullTickCount * tickInterval;
    private bool HasPartialTick => RemainderSeconds > 0.0001f;
    // Total casts AreaEffectPD.Launch() needs for this rain: the instant cast-time tick (+1, always — see
    // AreaEffectPD.Launch()'s own doc comment on why its first Cast() always fires immediately regardless
    // of totalCasts) PLUS the FullTickCount regular tickInterval-spaced steps PLUS one more if there's a
    // partial tick to squeeze in at the very end. For duration=5.25/tickInterval=0.25 (no remainder) this
    // is 1+21+0=22 total ticks, landing at t=0,0.25,...,5.25 — the LAST one exactly on duration, matching
    // this ability's own original live-authored tickCount(21)+1 total cast count.
    private int TotalCastCount => 1 + FullTickCount + (HasPartialTick ? 1 : 0);
    // Regular per-tick percent — strengthPerSecond (scaled live by damageMultiplier — see its own doc
    // comment) scaled down to however much one tickInterval covers, e.g. 0.6 (60%/sec) * 0.25 (tickInterval)
    // = 0.15 (15%) per tick at neutral damageMultiplier, matching this ability's own current live-authored
    // per-tick value exactly. TotalDamagePercent/LiveDamageRatio/SpawnChain's own damagePercent all read
    // this, so damageMultiplier is automatically reflected everywhere with no other edit point needed.
    private float TickDamagePercent => strengthPerSecond * damageMultiplier * tickInterval;
    // The LAST tick's own (smaller) percent when duration doesn't evenly divide by tickInterval — e.g.
    // (Stuart's own spec example) TickDamagePercent=100%, tickInterval=0.25, duration=0.3: instant tick
    // (t=0, 100%), one regular tick (t=0.25, 100%), then a final tick at t=0.3 worth
    // 100% * (0.05/0.25) = 100% * 20% = 20% of a full tick — exactly matching that example. Always
    // TickDamagePercent scaled by the leftover fraction of one tickInterval. Null (no override passed to
    // AreaEffectPD.Launch() at all) when there's no partial tick to begin with.
    private float? FinalTickPercent => HasPartialTick ? TickDamagePercent * (RemainderSeconds / tickInterval) : (float?)null;
    // Delay for the retrigger TRANSITION into that same final partial tick — RemainderSeconds instead of
    // the regular tickInterval, so it lands EXACTLY at t=duration instead of one full tickInterval later on
    // the regular grid (see AreaEffectPD._finalRetriggerDelayOverride's own doc comment for the general
    // mechanism this uses). Null (no override, every transition uses the regular tickInterval) when there's
    // no partial tick.
    private float? FinalRetriggerDelay => HasPartialTick ? RemainderSeconds : (float?)null;

    // Static total — NOT simply strengthPerSecond * duration (that formula assumes a continuous rate with
    // no extra tick, which undercounts by exactly one TickDamagePercent now that the instant cast-time tick
    // is a REAL extra tick on top of the regular tickInterval-spaced ones — see TotalCastCount's own doc
    // comment). The true total is every regular tick (the instant one plus FullTickCount more) at
    // TickDamagePercent, plus whatever the final partial tick contributes — e.g. for the live-authored
    // 5.25/0.6/0.25 values: 0.15 * 22 = 3.3 = 330%, not 315%. Percent-suffixed so
    // AugmentDescriptionFormatter auto-*100s it, for a description's static "(330%)"-style fallback
    // alongside the live number below.
    public float TotalDamagePercent => TickDamagePercent * (FullTickCount + 1) + (FinalTickPercent ?? 0f);

    // Summed across the whole rain, minus the luck/crit roll each tick independently rolls — baseline
    // preview, not a randomly-sampled one, same convention as every augment's own Live*Ratio.
    public string LiveDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(TotalDamagePercent * stats.Strength)} "
            : "";

    public string LiveSlowRatio(CharacterStatsPD stats) =>
        $"{AugmentDescriptionFormatter.FormatLivePercent(EffectiveSlowPercent)}";
    // How much slow this applies, as a straight percent (0.8 = 80% slower, i.e. 20% speed remains) — not
    // the "how much speed remains" multiplier EnemyBasePD.Slow() itself takes, converted at the call site
    // in SpawnChain instead so this field reads the way you'd actually want to type a value into it.
    // Reapplied on every tick an enemy is still standing in the zone, so it effectively stays refreshed the
    // whole time they remain inside, not just once on first contact.
    public float slowPercent = 0.35f;
    [SerializeField] private float slowDuration = 0.5f;

    // Live slow multiplier (2026-10-06, ArcherArrowRainSlowAugment) — same additive-stacking shape as
    // damageMultiplier/durationMultiplier above (1f neutral default, [HideInInspector], augments ADD to
    // it rather than mutating slowPercent itself). EffectiveSlowPercent is what SpawnChain/LiveSlowRatio
    // actually use. No clamp needed here — EnemyBasePD.ApplySlow/CharacterStatsPD.Slow both already clamp
    // the final multiplier to their own max-slow floor, so an arbitrarily large EffectiveSlowPercent from
    // stacked augments is always safe to send through uncapped.
    [HideInInspector] public float slowPercentMultiplier = 1f;
    private float EffectiveSlowPercent => slowPercent * slowPercentMultiplier;

    // Augment-owned (2026-10-05, ArcherArrowRainGrowthAugment) — same [HideInInspector] convention as every
    // other ability-specific augment hook (e.g. KnightBerserkAbilityPD.moveSpeedPercent): the augment's own
    // Inspector is the one real place to tune this, 0f default is a true no-op. Stacks ADDITIVELY with
    // Stats.AreaEffectSize (Stuart's own framing) — see SpawnChain/Simulate for the exact formula.
    [HideInInspector] public float growthPercentPerSecond = 0f;

    // Augment-owned (2026-10-06, ArcherArrowRainSelfTargetAugment) — Pattern B switch, no-op default false.
    // Checked directly in Activate() to fire a SECOND, fully independent SpawnChain() call centered on the
    // caster's own position, right after the normal cursor-targeted one — same "identical second instance"
    // shape as the cursor-targeted cast, not a shared/merged zone (two separate AreaEffectPD chain
    // instances, same "one spawned instance PER TRIGGER" architecture every other AreaEffectPD caller
    // already follows, so the two zones' damage/slow ticks never interfere with each other). Shares the
    // SAME activeChainId/growthElapsedTime tracking slot as the cursor-targeted cast though — whichever of
    // the two SpawnChain() calls happens SECOND (always the self-cast, since it's called after) is the one
    // ArcherArrowRainGrowthAugment's own growth tick actually grows; the cursor-targeted zone stays at its
    // cast-time radius for its whole lifetime if that augment is also equipped. This is the SAME
    // already-accepted "last cast wins" limitation State.activeChainId's own doc comment already flags for
    // overlapping casts in general — this augment just creates that overlap deliberately every single cast,
    // rather than it being a rare multi-press edge case. Not re-litigated with Stuart — already pre-approved.
    [HideInInspector] public bool castOnSelf = false;

    [Header("VFX")]
    // Duration is not exposed here — RadiusIndicatorVFXPD owns its own playback timing on the prefab itself.
    [SerializeField] private GameObject radiusVFXPrefab;

    // Must be an AreaEffectPD-carrying prefab registered in PredictedPrefabs.asset — can reuse the exact
    // same prefab DashExplosionAugment/DashHealAugment/ShieldBreakBurstAugment/KnightBuffStunAbilityPD
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
    // own doc comment), so readiness is purely whether a charge is available.
    public bool IsReady => currentState.charges > 0;
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
    public AbilityIdentity Identity => AbilityIdentity.ArcherArrowRain;
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
                currentState.charges++;
                currentState.cooldownTimer = currentState.charges < effectiveMax ? EffectiveCooldown : 0f;
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

        var chainId = SpawnChain(castPosition);
        if (chainId.HasValue)
        {
            currentState.activeChainId = chainId.Value;
            currentState.growthElapsedTime = 0f;
        }

        // ArcherArrowRainSelfTargetAugment — see castOnSelf's own doc comment for the accepted growth-
        // tracking tradeoff this creates.
        if (castOnSelf)
        {
            var selfChainId = SpawnChain(origin);
            if (selfChainId.HasValue)
            {
                currentState.activeChainId = selfChainId.Value;
                currentState.growthElapsedTime = 0f;
            }
        }

        if (cooldown > 0f) _stats.NotifyAbilityEnd(this);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true, so PlayerControllerPD.CancelSurgeBlockingAbility() never has
    // a real reason to call this — see IAbilityPD.CancelActiveWindow's own doc comment.
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
                state.charges++;
                state.cooldownTimer = state.charges < effectiveMax ? EffectiveCooldown : 0f;
            }
        }

        // Growth tick (2026-10-05, ArcherArrowRainGrowthAugment) — see growthPercentPerSecond/activeChainId's
        // own doc comments. Gated on growthPercentPerSecond != 0f so a player without the augment never pays
        // for the GetGameObject/TryGetComponent lookup below (4-player scale reminder). Also stops once
        // growthElapsedTime reaches EffectiveDuration (not the raw duration field — see its own doc comment,
        // this is what ArcherArrowRainDurationAugment extends) — the rain's own last tick has already
        // landed by then, so there's nothing left to grow into and no reason to keep resolving the chain
        // every frame forever.
        if (growthPercentPerSecond != 0f && state.growthElapsedTime < EffectiveDuration)
        {
            state.growthElapsedTime = Mathf.Min(EffectiveDuration, state.growthElapsedTime + delta);

            var go = hierarchy.GetGameObject(state.activeChainId);
            if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            {
                float grownAreaScale = _stats.AreaEffectSize + growthPercentPerSecond * state.growthElapsedTime;
                chain.SetRadius(castRadius * grownAreaScale);
            }
        }
    }

    // Called once per Activate() — the whole rain is a single cast now, no outer recast tier anymore.
    // Returns the spawned chain's id (null if the prefab/Create failed) so Activate() can track it for the
    // growth tick above — see State.activeChainId's own doc comment.
    private PredictedObjectID? SpawnChain(Vector3 position)
    {
        if (chainPrefab == null) return null;

        float radius = castRadius * _stats.AreaEffectSize;
        float slowMultiplier = 1f - EffectiveSlowPercent;

        var chainId = hierarchy.Create(chainPrefab, position, Quaternion.identity);
        if (!chainId.HasValue) return null;

        var go = hierarchy.GetGameObject(chainId.Value);
        // notifyOnHit: false — no onhit-triggered effects fire from this ability's own damage ticks. No
        // hasKnockback passed — defaults to false (no knockback at all), matching this ability's own spec.
        // overrideHitSfx: true, hitSfxOverride left null — silent. Up to 20 ticks * every enemy caught in the
        // zone would otherwise spam the caster's hitSfx into an unlistenable wall of noise (see
        // HitFeedbackData's own doc comment for the general mechanism this uses).
        // radiusVfxGrowthPerSecond: world-units/sec, not percent — castRadius * growthPercentPerSecond is the
        // real per-second growth rate of the gameplay radius at the CURRENT (cast-time) AreaEffectSize, which
        // the VFX then rides at a constant rate for its own doc-commented reasons (see
        // ArrowRainParticleRadiusPD.SetGrowth). null (no augment) when growthPercentPerSecond is 0.
        if (go != null && go.TryGetComponent(out AreaEffectPD chain))
            chain.Launch(_stats, position, radius, radiusVFXPrefab,
                dealDamage: true, damagePercent: TickDamagePercent, notifyOnHit: false,
                onEnemyHit: enemy => enemy.Slow(slowMultiplier, slowDuration),
                overrideHitSfx: true, totalCasts: TotalCastCount, retriggerRate: tickInterval,
                radiusVfxTintColor: _stats.PlayerColor,
                finalCastDamagePercentOverride: FinalTickPercent,
                finalRetriggerDelayOverride: FinalRetriggerDelay,
                radiusVfxAbilityDuration: EffectiveDuration,
                radiusVfxGrowthPerSecond: growthPercentPerSecond != 0f ? castRadius * growthPercentPerSecond : (float?)null);

        return chainId;
    }
}
