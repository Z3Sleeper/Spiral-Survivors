using PurrNet.Prediction;
using UnityEngine;

// Knight's first class ability, built on the IAbilityPD architecture — gated by a cooldown. Reworked
// (2026-08-23) from an instantaneous stun+self-heal into a real timed buff with a delayed payoff: casting
// grants +25% Total Max Health, +50% Total Weapon Range, and +50% Size for a fixed duration, and once per
// cast — either when that duration naturally ends, OR early via a recast (see below), whichever happens
// first — the AoE triggers: every nearby enemy gets interrupted, stunned, and knocked back. No damage, no
// heal-per-enemy-hit — both gone; the AoE keeps its knockback but exists purely for the interrupt+stun+
// knockback utility now, no self-sustain. Damage Reduction (was +25%, one of the original four grants)
// removed entirely (2026-08-31, explicit user request) — this ability now grants exactly three stats.
//
// Same-day rework (2026-08-23): the Weapon Range and Size grants, and Damage Reduction's own percentage
// (50% -> 25%), all moved over from KnightBerserkAbilityPD, which used to grant them for its own Berserk
// Surge's duration — per explicit user request, this ability now owns EVERY stat buff between the two
// Knight abilities, and KnightBerserkAbilityPD is purely a Berserk Surge trigger with no stat grants of its
// own at all anymore. Cooldown also bumped 10s -> 20s in the same pass (now matching KnightBerserkAbilityPD's
// own 20s). Duration went 5s -> 10s that same pass, then retuned again to 8s shortly after. Renamed from
// "Shockwave" to "Steadfast" and swapped from Ability1 to Ability2 (KnightBerserkAbilityPD/"Resolve" took
// Ability1) in that same later pass.
//
// Recastable while already active — a second press triggers the AoE immediately instead of waiting for the
// buff to naturally expire, but (2026-08-23 change, per explicit user request) does NOT end the buff itself
// early anymore — the stat grants stay up for the full duration regardless of when the AoE actually fires.
// Exactly one AoE trigger per cast: State.aoeTriggered is set the instant TriggerAoE()
// runs (recast or natural end, whichever happens first) and gates the OTHER path out — a natural expiry after
// an early recast does nothing further, and IsReady itself now reads false the instant aoeTriggered flips
// (2026-08-24 — was previously still "ready," just silently no-oping deeper inside Activate()) so the ability
// icon greys out immediately once the recast's own utility is spent, rather than staying lit for a press that
// does nothing. Deliberate, explicit design intent (user's own words): lets a player trigger the stun right
// away if they'd rather interrupt a key enemy attack or clear a horde off them immediately, without
// sacrificing the rest of the buff's duration to do it. recastBlockDuration is a short misclick guard, same
// shape/reasoning as ArcherFocusFireAbilityPD's own — a double-press/held-down button
// can't immediately burn the early-trigger for essentially nothing. EndBuff()/TriggerAoE() are the single
// shared reversal/payoff every ending path (natural expiry, death, recast) calls, so they can never drift
// apart.
//
// No AbilityPower scaling anywhere (2026-08-23, explicit removal) — duration/maxHealthPercent/
// rangePercent/sizePercent are all flat, fixed values. The AoE's own stunDuration used to also scale by
// CharacterStatsPD.AreaEffectPower; that stat (and AreaEffectCount, the multi-cast repeat count) were removed
// entirely (2026-09-13) — stunDuration is now this ability's own plain, unscaled baseline, and TriggerAoE
// always fires a flat single cast. Radius still scales via AreaEffectSize/RangeScalingAoEBase, owned by the
// shared AreaEffectCastHelper/AreaEffectPD chain, unaffected by any of this.
//
// All three stat grants use the Total tier (AddMaxHealthTotalPercent/AddWeaponRangeTotalPercent/
// AddSizePercent) rather than the plain Bonus tier the generic stat-pick augments
// use, same "outlier/special effect, not a per-point-buy pick" reasoning as every other buff ability in this
// codebase. Max Health also tops current health up by the same delta the ceiling just rose by for free:
// CharacterStatsPD.ApplyStatChange (which AddMaxHealthTotalPercent routes through) already bumps current
// health by the exact same amount the ceiling just rose by, no separate Heal() call needed. The reverse
// direction is handled by that same shared method too — it only ever pulls current health DOWN to the new
// (lower) ceiling if actually sitting above it, never below, so a caster who took damage mid-buff keeps
// whatever health they had.
//
// Death force-ends the buff early (and still triggers the AoE if a recast hasn't already, same as natural
// expiry — see State.aoeTriggered's own doc comment), same convention as every other buff this session
// (KnightBerserkAbilityPD/ArcherFocusFireAbilityPD/RogueVanishAbilityPD/ExeBigAxeAbilityPD/BWBlinkAbilityPD)
// — see Simulate()'s own comment.
public class KnightBuffStunAbilityPD : PredictedIdentity<KnightBuffStunAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float activeTimer;
        public bool  isActive;
        public float cooldownTimer;
        // True once TriggerAoE() has fired for the CURRENT cast (recast or natural/death end, whichever
        // happens first) — reset to false only at a fresh Activate(). Gates every other would-be trigger for
        // the same cast out, so the AoE only ever goes off once per cast regardless of how it actually ends —
        // see class doc comment.
        public bool aoeTriggered;
        // Guards FirstSuccessfulInterrupt (FSI) — reset once per AoE trigger (natural end OR recast), so a
        // whole AoE fires FSI at most once, the first time any enemy is actually interrupted. See
        // OnEnemyInterrupted/CharacterStatsPD.
        // NotifyInterrupted.
        public bool interruptedThisCast;
        // Captured inside TriggerAoE, the instant it actually fires (2026-09-26, shockwave VFX support) —
        // NOT recomputed later, since RangeScalingAoEBase is genuinely time-sensitive here (see TriggerAoE's
        // own doc comment on the Simulate()-tail ordering: the Size buff can already be reverted by the time
        // anything downstream would otherwise re-read it). UpdateView reads these off VERIFIED state to fire
        // the VFX safely — see its own doc comment for why TriggerAoE itself can't just call the VFX
        // directly.
        public Vector3 lastAoeCastPosition;
        public float lastAoeRadius;
        // Same time-sensitivity as lastAoeRadius above — Stats.Size (bonusSizePercent) reflects this
        // ability's own +50% Size grant while it's still applied; reading it later in UpdateView (after
        // EndBuff has reverted it) would shrink the VFX's height lift back down incorrectly.
        public float lastAoeCasterSize;
        // Monotonic, never reset (unlike aoeTriggered below) — incremented once per ACTUAL TriggerAoE() call,
        // regardless of how many times that is per cast. UpdateView compares this against its own
        // last-seen count to fire the shockwave VFX exactly once per real trigger — see
        // KnightBuffStunMoreStunsAugment's own doc comment for why a single bool edge-detector (what this
        // replaced) can't tell 2-3 same-cast triggers apart from just 1.
        public int aoeVfxTriggerCount;
        // Ability charge system — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment. Does NOT gate
        // the recast-to-trigger-early branch below (that's a free utility action on an already-paid-for
        // cast, not a new one) — only the fresh-cast branch of IsReady.
        public int charges;
        public int knownMaxCharges;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Steadfast";
    [SerializeField, TextArea] private string description =
        "Gain +25% Max Health, +50% Range, and +50% Size for 8 seconds. At the end of the duration, interrupt and stun all nearby enemies. Recast to trigger the stun early.";
    public float cooldown = 20f;
    // How many charges this ability starts with (1 = old off-cooldown behavior, unchanged) — see
    // IAbilityPD.MaxCharges's own doc comment. CharacterStatsPD.bonusAbility2Charges adds on top live.
    [SerializeField] private int authoredChargeCount = 1;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false (held at its full value
    // throughout the active window) — see Simulate(). true: cooldown starts ticking immediately at cast,
    // running concurrently with the window. Same convention/default as RogueVanishAbilityPD's own.
    [SerializeField] private bool cooldownStartsOnCast = false;
    // Which action-bar slot this occupies — Inspector-configurable per character rather than hardcoded per
    // class, so which ability goes where is a prefab-authoring choice, not a recompile.
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability2;
    [SerializeField] private string[] tags;
    // false (default) — see IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract. No longer
    // moot now that this ability has a real active window (unlike its pre-rework instantaneous self).
    [SerializeField] private bool activeSurgeBlock = false;

    [Header("Effect")]
    public float duration = 8f;
    public float maxHealthPercent       = 0.25f;
    public float rangePercent           = 0.5f;
    public float sizePercent            = 0.5f;
    // 0f (no-op) until KnightBuffStunStrengthKnockbackAugment sets them — two MORE buffs added to this
    // ability's active window, same lifecycle (Activate()/EndBuff()) as the three above. [HideInInspector]
    // — the augment owns these, same reasoning as KnightBerserkAbilityPD's own augment-owned fields.
    [HideInInspector] public float strengthPercent  = 0f;
    [HideInInspector] public float knockbackPercent = 0f;
    // 1f (no-op) until KnightBuffStunBuffStrengthAugment sets it — amplifies ALL FIVE buffs above, live,
    // at cast time (see Activate()'s own _appliedXPercent snapshot lines) — see that augment's own doc
    // comment for why this is a live multiplier rather than a one-shot destructive multiply.
    // [HideInInspector] — same reasoning.
    [HideInInspector] public float buffStrengthMultiplier = 1f;
    // 0f (no-op) until KnightBuffStunHealAugment sets it — raw heal amount per enemy actually touched by
    // the stun AoE is Stats.MaxHealth * healPercentPerHit, summed across the WHOLE cast's hit count (see
    // OnAoECastComplete below), not stun/interrupt/knockback success. [HideInInspector] — same reasoning as
    // the other augment-owned fields above.
    [HideInInspector] public float healPercentPerHit = 0f;
    // Recast-to-trigger-early (see IsReady's own doc comment) is blocked for this long after a fresh cast —
    // purely a misclick guard, same shape/reasoning as ArcherFocusFireAbilityPD's own recastBlockDuration.
    [SerializeField] private float recastBlockDuration = 0.5f;

    // 0f/false (no-op) until KnightBuffStunMoreStunsAugment sets it — see that augment's own doc comment.
    // [HideInInspector] — the augment owns this switch, nothing on this ability's own prefab should need
    // hand-authoring it (same reasoning as KnightBerserkAbilityPD's own augment-owned fields).
    [HideInInspector] public bool extraStunsEnabled = false;

    [Header("AoE Payoff (triggers when the buff ends, or early via recast)")]
    // Flat multiplier on top of CharacterStatsPD.RangeScalingAoEBase (see TriggerAoE) — exposed purely for
    // tuning "feel" — this just scales the whole result up/down without touching either of that shared
    // formula's own inputs.
    [SerializeField] private float radiusMultiplier = 2f;
    public float stunDuration = 2f;
    // Stats.Knockback * knockbackMultiplier (2026-09-16, was a flat 10f override NOT derived from
    // Stats.Knockback at all) — now scales with the caster's own Knockback stat, same
    // "Stats.Knockback * multiplier" shape DashExplosionAugment/ShieldBreakBurstAugment already use, so
    // KnockbackAugment (and any future Knockback-scaling source) actually affects this ability's own push
    // too. 2f preserves this ability's previous authored feel exactly (10f / baseKnockback(5f) = 2x).
    // Passed straight through to AreaEffectCastHelper.Cast's knockback param, which applies independently of
    // dealDamage (see its own doc comment), so this still knocks enemies back even though this ability deals
    // no damage at all.
    [SerializeField] private float knockbackMultiplier = 2f;

    // Spawned when the AoE actually triggers (buff end or recast — see TriggerAoE), not at Activate() itself
    // anymore. Pooled, so this is cheap even when AreaEffectCount is just 1. Must be an AreaEffectPD-carrying
    // prefab registered in PredictedPrefabs.asset — can reuse the exact same prefab DashExplosionAugment/
    // DashHealAugment/ShieldBreakBurstAugment already spawn, since the class itself isn't tied to any one of
    // them, or a separate prefab instance if you'd rather pool it independently.
    [Header("Chain (spawned when the AoE triggers)")]
    [SerializeField] private GameObject chainPrefab;

    private CharacterStatsPD _stats;

    // Snapshots of the fields above at cast time, reversed with the identical values at buff-end — protects
    // against the Inspector field changing mid-buff (same convention as every other buff ability in this
    // codebase). Also what IsReady's own recast-block elapsed-time math is measured against.
    private float _appliedMaxHealthPercent;
    private float _appliedRangePercent;
    private float _appliedSizePercent;
    private float _appliedStrengthPercent;
    private float _appliedKnockbackPercent;
    private float _appliedDuration;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
    }

    public Sprite Icon => icon;
    // True during the active window too (not just off cooldown), but only once recastBlockDuration has
    // elapsed since the cast that started it — see that field's own doc comment for why (misclick guard).
    // Same "two distinct ready conditions" shape as ArcherFocusFireAbilityPD's own recast. The active-window
    // branch ALSO requires !aoeTriggered (2026-08-24 addition) — once a recast (or the natural end) has
    // already fired the AoE this cast, pressing again is a guaranteed no-op (see Activate()'s own guard), so
    // IsReady correctly reads false for the rest of the buff window instead of staying "ready" for an action
    // that does nothing. Driving LocalUIManagerPD's own showBG = !IsReady, this is what greys the ability
    // icon out immediately after a recast — an icon that isn't greyed out is supposed to promise "pressing
    // this does something," so once the recast's own utility is spent, the icon needs to say so even though
    // the buff itself (AbilityIsActive) is still genuinely running underneath (the duration bar stays up).
    // Scoped to ONLY this first branch, not the whole expression — the second branch (off cooldown, fully
    // inactive) must stay independent of aoeTriggered, or the ability would never read ready again after its
    // cooldown finishes (aoeTriggered only resets at the START of the next fresh Activate() cast, which itself
    // requires IsReady to be true first — anding it in globally would deadlock the ability permanently unready).
    public bool IsReady => _stats != null && (
        (currentState.isActive && !currentState.aoeTriggered && _appliedDuration - currentState.activeTimer >= recastBlockDuration)
        || (!currentState.isActive && currentState.charges > 0));
    public bool IsActive => currentState.isActive;
    // Only ever flips stat totals — never touches movement, so it never locks dash or the other ability slot
    // out while active (see IAbilityPD.ForcesSpecialAction's own doc comment).
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    // Same underlying window as IsActive for this ability (the whole buff) — see IAbilityPD.AbilityIsActive's
    // own doc comment for why these are separate interface members even though they agree here.
    public bool AbilityIsActive => currentState.isActive;
    public bool HasActiveWindow => true;
    public float ActiveTimeRemaining => Mathf.Max(0f, currentState.activeTimer);
    public float ActiveTimeDuration => _appliedDuration;
    public float CooldownRemaining => Mathf.Max(0f, currentState.cooldownTimer);
    public float CooldownDuration => EffectiveCooldown;
    public float BaseCooldownDuration => cooldown;
    public bool CooldownStartsOnCast => cooldownStartsOnCast;
    public string Name => abilityName;
    public string Description => description;
    public string[] Tags => tags;
    public AbilitySlot Slot => slot;
    public AbilityIdentity Identity => AbilityIdentity.KnightBuffStun;
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

    public string LiveStunDurationRatio(CharacterStatsPD stats) =>
        $"{AugmentDescriptionFormatter.FormatLiveNumber(stunDuration)} ";

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

        if (currentState.isActive)
        {
            // Recast — triggers the AoE early instead of waiting for the buff to naturally expire, but no
            // longer ends the buff itself (2026-08-23 change) — see class doc comment. Only fires if this
            // cast's AoE hasn't already gone off (a second recast press, or a recast after the natural end
            // already fired it, are both no-ops).
            if (!currentState.aoeTriggered) TriggerAoE(ref currentState);
            return;
        }

        // buffStrengthMultiplier (2026-10-02, KnightBuffStunBuffStrengthAugment) is 1f/no-op by default — see
        // that augment's own doc comment for why this is applied live here instead of destructively baked
        // into the fields themselves (order-independence against KnightBuffStunStrengthKnockbackAugment's
        // own strengthPercent/knockbackPercent below, regardless of which of the two was picked first).
        _appliedMaxHealthPercent       = maxHealthPercent * buffStrengthMultiplier;
        _appliedRangePercent           = rangePercent * buffStrengthMultiplier;
        _appliedSizePercent            = sizePercent * buffStrengthMultiplier;
        // 0f (no-op) until KnightBuffStunStrengthKnockbackAugment sets strengthPercent/knockbackPercent —
        // see those fields' own doc comment.
        _appliedStrengthPercent        = strengthPercent * buffStrengthMultiplier;
        _appliedKnockbackPercent       = knockbackPercent * buffStrengthMultiplier;
        _appliedDuration                = duration;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        currentState.aoeTriggered  = false;
        // Charge system — spend 1, and only (re)arm the regen timer if it wasn't already running — see
        // IAbilityPD.MaxCharges's own doc comment.
        currentState.charges--;
        if (currentState.cooldownTimer <= 0f) currentState.cooldownTimer = EffectiveCooldown;

        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in EndBuff() below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }

        // Also tops current health up by the same delta the ceiling just rose by — see class doc comment.
        _stats.AddMaxHealthTotalPercent(_appliedMaxHealthPercent);
        _stats.AddWeaponRangeTotalPercent(_appliedRangePercent);
        _stats.AddSizePercent(_appliedSizePercent);
        // KnightBuffStunStrengthKnockbackAugment's two grants (2026-10-02) — harmless no-ops at 0f/default.
        _stats.AddStrengthTotalPercent(_appliedStrengthPercent);
        _stats.AddKnockbackPercent(_appliedKnockbackPercent);

        // KnightBuffStunMoreStunsAugment's on-cast trigger (2026-10-02) — fires immediately, AFTER the Size
        // buff above so it gets the same buffed radius the recast/natural-end triggers do. setsRecastGate:
        // false means this does NOT consume the recast opportunity below — see TriggerAoE's own doc comment.
        if (extraStunsEnabled) TriggerAoE(ref currentState, setsRecastGate: false);

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        // Skipped entirely at 0 (raw, un-hasted) cooldown — an ability that can be spammed with no downtime
        // shouldn't be firing "a real ability cast" notifications on every cycle (PhantomBladeOnAbilityStartAugment
        // and any future listener would otherwise see it fire far more often than intended).
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    // Ends the buff itself — only ever called from the natural-expiry/death path in Simulate() now (a recast
    // no longer ends the buff, see class doc comment). Takes the `ref State state` actually in scope at the
    // call site (the possibly-replayed `state` parameter from Simulate()) rather than assuming currentState,
    // same reasoning as KingOrbiterPD.OnAoEInterrupt.
    private void EndBuff(ref State state)
    {
        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        if (_stats != null)
        {
            _stats.AddMaxHealthTotalPercent(-_appliedMaxHealthPercent);
            _stats.AddWeaponRangeTotalPercent(-_appliedRangePercent);
            _stats.AddSizePercent(-_appliedSizePercent);
            _stats.AddStrengthTotalPercent(-_appliedStrengthPercent);
            _stats.AddKnockbackPercent(-_appliedKnockbackPercent);
        }

        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        // See the matching NotifyAbilityStart call in Activate() for why this is gated on cooldown > 0 too.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }

    // The delayed payoff — interrupt + stun every nearby enemy, no damage, no knockback, no heal (all tossed,
    // see class doc comment). Goes through the shared "AoE Effects" AreaEffectCastHelper.Cast (via the
    // spawned AreaEffectPD chain — the same generic chain class DashExplosionAugment/DashHealAugment/
    // ShieldBreakBurstAugment all use, not a Knight-specific one) — a flat single cast now (Stats.
    // AreaEffectCount, the stat that used to let this repeat, was removed entirely 2026-09-13). Every caller
    // is responsible for checking state.aoeTriggered first (see its own doc comment) — this method just marks
    // it unconditionally, it doesn't check it itself, so it stays a plain "do the thing" method callable from
    // either the recast or the natural-end path.
    // setsRecastGate (2026-10-02, KnightBuffStunMoreStunsAugment) — true for every pre-existing call site
    // (recast, natural end), preserving the original "fire at most once per cast" gate exactly. The NEW
    // on-cast call from Activate() passes false specifically so firing immediately on cast doesn't also burn
    // the separate recast opportunity — the two are independent triggers under this augment, not the same
    // one. aoeVfxTriggerCount increments UNCONDITIONALLY on every call regardless of setsRecastGate, purely
    // so UpdateView can fire the shockwave VFX once per actual trigger even when this fires up to 3 times in
    // one cast (a single bool edge-detector on aoeTriggered alone couldn't tell those apart — see State's
    // own doc comment on that field).
    private void TriggerAoE(ref State state, bool setsRecastGate = true)
    {
        if (setsRecastGate) state.aoeTriggered = true;
        state.aoeVfxTriggerCount++;

        if (_stats == null || chainPrefab == null) return;

        float radius = _stats.RangeScalingAoEBase * radiusMultiplier;
        Vector3 castPosition = _stats.transform.position;
        state.lastAoeCastPosition = castPosition;
        state.lastAoeRadius = radius;
        state.lastAoeCasterSize = _stats.Size;

        state.interruptedThisCast = false;

        var chainId = hierarchy.Create(chainPrefab, castPosition, Quaternion.identity);
        if (!chainId.HasValue) return;

        var go = hierarchy.GetGameObject(chainId.Value);
        if (go == null || !go.TryGetComponent(out AreaEffectPD chain)) return;

        // dealDamage: false — no damage source at all, just knockback (its own independent gate in Cast(),
        // see its own doc comment for why that no longer requires dealDamage), and stun (onEnemyHit, same
        // layering shape ShieldBreakBurstAugment's own Stun uses). No more heal-per-hit (see class doc
        // comment). interrupt: true — this stun is meant to cancel whatever attack an enemy is mid-sequence
        // on, not just land and wait for it to finish (see EnemyBasePD.Interrupt()'s own doc comment);
        // Cast() applies it before knockback/onEnemyHit for exactly that reason. Every enemy this cast
        // actually interrupts also gets IEnemyPD.InterruptTrueStun's own flat, CC-resist-immune bonus
        // duration on top of the ordinary onEnemyHit Stun() below (this cast's own stunDuration passed
        // through as the paired amount — see OnEnemyInterrupted/CharacterStatsPD.NotifyInterrupted's own
        // doc comments) — an enemy NOT successfully interrupted (already uninterruptible this tick) still
        // gets the plain onEnemyHit stun alone, no bonus.
        chain.Launch(_stats, castPosition, radius, null,
            dealDamage: false, hasKnockback: true, knockback: _stats.Knockback * knockbackMultiplier, interrupt: true,
            onEnemyHit: enemy => enemy.Stun(stunDuration),
            onCastComplete: OnAoECastComplete,
            onSuccessfulInterrupt: OnEnemyInterrupted,
            vfxPerCast: true);
    }

    // KnightBuffStunHealAugment's hook (2026-10-03) — healPercentPerHit is 0f/no-op until that augment sets
    // it. onCastComplete fires with the TOTAL hit count for this one TriggerAoE cast (every enemy actually
    // touched by the AoE — AreaEffectCastHelper.Cast's own hitCount, incremented regardless of
    // interrupt/stun success, see that method's own onEnemyHit/onCastComplete doc comments), so this scales
    // correctly with however many enemies were caught, not just whether any were. Named method, not a
    // lambda, purely for consistency with OnEnemyInterrupted just below (no ref-capture issue here, since
    // this only touches _stats/healPercentPerHit, neither of which is currentState) — direct Stats.Heal()
    // call, same safety class as every other direct stat mutation already called from Activate()/EndBuff()
    // in this file (TriggerAoE is itself only ever reached from those same discrete, input/edge-triggered
    // call sites, never a continuous per-tick Simulate() read).
    private void OnAoECastComplete(int hitCount)
    {
        if (healPercentPerHit == 0f || hitCount <= 0 || _stats == null) return;
        _stats.Heal(_stats.MaxHealth * healPercentPerHit * hitCount);
        // Heal VFX (2026-10-03, user-curated per-augment — see SurgeHealAugment's own identical opt-in).
        // Safe to call directly here, unlike KnightBuffStunAbilityPD's OWN shockwave VFX just below (which
        // needs the verified-state/counter dance) — PlayHealVfx has its own per-target cooldown dedup
        // built in (VFXManager's own healVfxCooldown), so it tolerates a direct call from this same
        // discrete, input/edge-triggered call site fine.
        VFXManager.instance?.PlayHealVfx(_stats.transform);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true — see IAbilityPD.CancelActiveWindow's own doc comment.
    public void CancelActiveWindow() { }

    // Fired by AreaEffectCastHelper.Cast for every enemy this cast actually interrupted (see its own doc
    // comment on onSuccessfulInterrupt) — feeds the generic SI/FSI events, gated to fire FSI at most once per
    // AoE trigger via currentState.interruptedThisCast (FSI itself isn't used for anything stun-related here
    // — InterruptTrueStun fires on every SI, see NotifyInterrupted's own doc comment). Passes this same
    // cast's own stun duration as the paired-stun amount, added to InterruptTrueStun's flat portion for every
    // enemy this cast successfully interrupts. The onEnemyHit Stun() call in TriggerAoE
    // still fires independently for every hit enemy regardless (including this one) — ApplyStun's own
    // Mathf.Max semantics mean the two calls can never fight each other; whichever produces the longer
    // duration simply wins.
    //
    // Uses currentState directly (not a ref state param) — safe because this fires asynchronously from the
    // spawned AreaEffectPD chain's own Simulate() context (its own multi-cast retrigger timing), not
    // synchronously within this ability's own Simulate(), so by the time it runs, currentState is once again
    // just this ability's own latest state, not a replayed one — same established pattern this file already
    // used before the rework.
    private void OnEnemyInterrupted(IEnemyPD enemy) =>
        _stats.NotifyInterrupted(enemy, ref currentState.interruptedThisCast, stunDuration);

    // Shockwave VFX opt-in (2026-09-26, user-curated, replaces the old flat-cylinder radiusVFXPrefab
    // stand-in) — deliberately NOT called directly inside TriggerAoE. Unlike DashExplosionAugment/
    // DashHealAugment/TipStrikeRangeExplosionAugment/SurgeInterruptAoEAugment (all gated behind a genuine
    // PredictedEvent listener, so their own trigger method is guaranteed to truly execute exactly once per
    // real occurrence), TriggerAoE here is called directly from Activate()'s recast branch — a raw
    // Simulate()-context call with no PredictedEvent protection at all. hierarchy.Create()/chain.Launch()
    // tolerate that fine (idempotent under replay — a resimulated replay of the same tick just re-resolves
    // the same physical chain instance), but VFXManager.PlayShockwaveVfx has no such dedup: calling it
    // directly here could visibly fire more than once for a single real cast under ordinary resimulation.
    // Deferred to here instead — comparing state.aoeVfxTriggerCount off VERIFIED state against this view's
    // own last-seen count (2026-10-02, was a plain bool edge-detector on aoeTriggered until
    // KnightBuffStunMoreStunsAugment needed to tell up to 3 same-cast triggers apart — see that field's own
    // doc comment), the same "fires exactly once per real occurrence, immune to replay" guarantee used
    // everywhere else in this codebase (death/dash-start detection, etc.), just counter-based instead of
    // bool-based so it scales past one trigger per cast. lastAoeCastPosition/lastAoeRadius are read from
    // verified state too
    // (captured at the exact moment TriggerAoE actually ran), not recomputed here, since radius is
    // genuinely time-sensitive (see State field's own doc comment).
    // Counts how many of verified.aoeVfxTriggerCount's increments this view has already played a VFX for —
    // see State.aoeVfxTriggerCount's own doc comment for why a monotonic counter replaced the old bool
    // edge-detector (needs to tell apart up to 3 real triggers per cast under KnightBuffStunMoreStunsAugment,
    // not just one). Never reset to 0 on a missing verified snapshot (unlike the old bool, which reset to
    // false) — there's nothing to catch up on with no data, and the field's own zero-initialized default
    // already matches a genuinely fresh identity's first real read.
    private int _lastAoeVfxTriggerCountView;

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!verified.HasValue || _stats == null) return;

        if (verified.Value.aoeVfxTriggerCount != _lastAoeVfxTriggerCountView)
            VFXManager.instance?.PlayShockwaveVfx(verified.Value.lastAoeCastPosition, verified.Value.lastAoeCasterSize, _stats.PlayerColor, _stats.PlayerColor, verified.Value.lastAoeRadius);
        _lastAoeVfxTriggerCountView = verified.Value.aoeVfxTriggerCount;
    }

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
            // own doc comment.
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
        // Death force-ends the buff early — see ExeBigAxeAbilityPD.Simulate()'s own identical comment for the
        // full reasoning (same shape here: a dead caster gets nothing from these stats, and the cooldown
        // otherwise couldn't start ticking until this window ends anyway).
        bool forceEndOnDeath = _stats != null && _stats.IsDead;
        if (state.activeTimer > 0f && !forceEndOnDeath) return;

        // Fired BEFORE EndBuff, deliberately — TriggerAoE's own radius reads Stats.RangeScalingAoEBase, which
        // now factors in bonusSizePercent (see that property's own doc comment), so the AoE needs to go off
        // while the Size buff is still actually applied to get its full radius. Triggering it after EndBuff
        // would silently shrink the natural-end AoE back down before it even fires (2026-08-23 fix — the
        // recast path was never affected, since a recast always fires TriggerAoE while the buff is still
        // fully up by construction). Only fires here if an earlier recast hasn't already triggered it this
        // cast — see State.aoeTriggered's own doc comment.
        // extraStunsEnabled (2026-10-02, KnightBuffStunMoreStunsAugment) overrides the gate below: the
        // natural end ALWAYS fires under this augment, regardless of whether recast already went off this
        // cast — see that augment's own doc comment for why recast and natural-end are independent triggers
        // under it, not the same one.
        if (extraStunsEnabled || !state.aoeTriggered) TriggerAoE(ref state);
        EndBuff(ref state);
    }
}
