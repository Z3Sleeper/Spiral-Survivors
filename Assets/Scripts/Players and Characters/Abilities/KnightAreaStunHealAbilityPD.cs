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
// (50% -> 25%), all moved over from KnightBigBuffAbilityPD, which used to grant them for its own Berserk
// Surge's duration — per explicit user request, this ability now owns EVERY stat buff between the two
// Knight abilities, and KnightBigBuffAbilityPD is purely a Berserk Surge trigger with no stat grants of its
// own at all anymore. Cooldown also bumped 10s -> 20s in the same pass (now matching KnightBigBuffAbilityPD's
// own 20s). Duration went 5s -> 10s that same pass, then retuned again to 8s shortly after. Renamed from
// "Shockwave" to "Steadfast" and swapped from Ability1 to Ability2 (KnightBigBuffAbilityPD/"Resolve" took
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
// rangePercent/sizePercent are all flat, fixed values, and the AoE's own stunDuration now scales by
// CharacterStatsPD.AreaEffectPower alone (a direct multiplier, neutral at 1.0) rather than the old additive
// AreaEffectPower+AbilityPower-1f combination. The "AoE Effects" family's own radius/count/retrigger scaling
// (AreaEffectSize via RangeScalingAoEBase, AreaEffectCount) is untouched — that's owned by the shared
// AreaEffectCastHelper/AreaEffectPD chain, not this ability's own AbilityPower term.
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
// (KnightBigBuffAbilityPD/ArcherFocusFireAbilityPD/RogueVanishAbilityPD/ExeBigAxeAbilityPD/BWBlinkAbilityPD)
// — see Simulate()'s own comment.
public class KnightAreaStunHealAbilityPD : PredictedIdentity<KnightAreaStunHealAbilityPD.State>, IAbilityPD
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
        // Guards FirstSuccessfulInterrupt (FSI) — reset once per AoE trigger (natural end OR recast), not
        // per retrigger, so a whole AoE (including every AreaEffectCount retrigger) fires FSI at most once,
        // the first time any enemy is actually interrupted. See OnEnemyInterrupted/CharacterStatsPD.
        // NotifyInterrupted.
        public bool interruptedThisCast;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Steadfast";
    [SerializeField, TextArea] private string description =
        "Gain +25% Max Health, +50% Range, and +50% Size for 8 seconds. At the end of the duration, interrupt and stun all nearby enemies. Recast to trigger the stun early.";
    public float cooldown = 20f;
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
    // Recast-to-trigger-early (see IsReady's own doc comment) is blocked for this long after a fresh cast —
    // purely a misclick guard, same shape/reasoning as ArcherFocusFireAbilityPD's own recastBlockDuration.
    [SerializeField] private float recastBlockDuration = 0.5f;

    [Header("AoE Payoff (triggers when the buff ends, or early via recast)")]
    // Flat multiplier on top of CharacterStatsPD.RangeScalingAoEBase (see TriggerAoE) — exposed purely for
    // tuning "feel" — this just scales the whole result up/down without touching either of that shared
    // formula's own inputs.
    [SerializeField] private float radiusMultiplier = 2f;
    // Base stun duration BEFORE CharacterStatsPD.AreaEffectPower scales it (see TriggerAoE) — a direct
    // multiplier, neutral at 1.0, so a 2s base still stuns for exactly 2s with no AreaEffectPower investment.
    public float stunDuration = 2f;
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
        || (!currentState.isActive && currentState.cooldownTimer <= 0f));
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

    // Mirrors TriggerAoE()'s own stunDuration formula exactly (AreaEffectPower alone, no more AbilityPower —
    // see class doc comment). Outside a live match, falls back to the base field itself — exact at
    // AreaEffectPower's own neutral 1.0 baseline, same "neutral stat value == the base field" convention as
    // every other Live*Ratio in this file.
    public string LiveStunDurationRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? stunDuration * stats.AreaEffectPower
            : stunDuration;
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

        if (currentState.isActive)
        {
            // Recast — triggers the AoE early instead of waiting for the buff to naturally expire, but no
            // longer ends the buff itself (2026-08-23 change) — see class doc comment. Only fires if this
            // cast's AoE hasn't already gone off (a second recast press, or a recast after the natural end
            // already fired it, are both no-ops).
            if (!currentState.aoeTriggered) TriggerAoE(ref currentState);
            return;
        }

        _appliedMaxHealthPercent       = maxHealthPercent;
        _appliedRangePercent           = rangePercent;
        _appliedSizePercent            = sizePercent;
        _appliedDuration                = duration;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        currentState.aoeTriggered  = false;
        currentState.cooldownTimer = EffectiveCooldown;

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
        }

        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        // See the matching NotifyAbilityStart call in Activate() for why this is gated on cooldown > 0 too.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }

    // The delayed payoff — interrupt + stun every nearby enemy, no damage, no knockback, no heal (all tossed,
    // see class doc comment). Goes through the shared "AoE Effects" AreaEffectCastHelper.Cast (via the
    // spawned AreaEffectPD chain — the same generic chain class DashExplosionAugment/DashHealAugment/
    // ShieldBreakBurstAugment all use, not a Knight-specific one), so Stats.AreaEffectCount > 1 gets automatic
    // multi-cast support (retriggerRate apart) without this class needing to track that itself. Every caller
    // is responsible for checking state.aoeTriggered first (see its own doc comment) — this method just marks
    // it unconditionally, it doesn't check it itself, so it stays a plain "do the thing" method callable from
    // either the recast or the natural-end path.
    private void TriggerAoE(ref State state)
    {
        state.aoeTriggered = true;

        if (_stats == null || chainPrefab == null) return;

        float radius = _stats.RangeScalingAoEBase * radiusMultiplier;
        int totalCasts = Mathf.Max(1, _stats.AreaEffectCount);
        Vector3 castPosition = _stats.transform.position;

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
        chain.Launch(_stats, castPosition, totalCasts, retriggerRate, radius, radiusVFXPrefab,
            dealDamage: false, hasKnockback: true, knockback: knockback, interrupt: true,
            onEnemyHit: enemy => enemy.Stun(stunDuration * _stats.AreaEffectPower),
            onSuccessfulInterrupt: OnEnemyInterrupted,
            vfxPerCast: true);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true — see IAbilityPD.CancelActiveWindow's own doc comment.
    public void CancelActiveWindow() { }

    // Fired by AreaEffectCastHelper.Cast for every enemy this cast actually interrupted (see its own doc
    // comment on onSuccessfulInterrupt) — feeds the generic SI/FSI events, gated to fire FSI at most once per
    // AoE trigger via currentState.interruptedThisCast (FSI itself isn't used for anything stun-related here
    // — InterruptTrueStun fires on every SI, see NotifyInterrupted's own doc comment). Passes this same
    // cast's own (AreaEffectPower-scaled) stun duration as the paired-stun amount, added to InterruptTrueStun's
    // flat portion for every enemy this cast successfully interrupts. The onEnemyHit Stun() call in TriggerAoE
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
        _stats.NotifyInterrupted(enemy, ref currentState.interruptedThisCast, stunDuration * _stats.AreaEffectPower);

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole active
        // window below) or only once that window ends (state.isActive false) — see the field's own doc
        // comment. Either way Activate() already set cooldownTimer to its full value at cast time.
        if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive))
            state.cooldownTimer -= delta;

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
        if (!state.aoeTriggered) TriggerAoE(ref state);
        EndBuff(ref state);
    }
}
