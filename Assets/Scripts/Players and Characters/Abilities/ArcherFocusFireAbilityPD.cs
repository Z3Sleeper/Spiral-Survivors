using PurrNet.Prediction;
using UnityEngine;

// Archer's second class ability, built on the IAbilityPD architecture — gated by a cooldown, same
// fixed-duration active-window shape as RogueVanishAbilityPD (that file is the template the buff-timer
// plumbing below mirrors), but the "buff" here is two CharacterStatsPD flags WeaponRangedPD itself reads
// rather than a stat multiplier:
// - CharacterStatsPD.ContinuousFire (see its own doc comment): while true, WeaponRangedPD skips its normal
//   FindTarget()/line-of-sight requirement entirely and just fires straight forward every time its fire-rate
//   timer allows, target or not.
// - CharacterStatsPD.FocusFireAimAngle (see its own doc comment): continuously updated every tick this is
//   active (not snapshotted once at cast — Focus Fire is meant to keep tracking a target throughout its
//   whole window, not just at the instant it's cast), in the same Atan2-based convention OrbitalPosition's
//   own baseAngle already uses. Every weapon this owner has substitutes this shared angle in place of the
//   normal ever-incrementing WeaponOrbitAngle while ContinuousFire is set, which freezes the whole ring from
//   sweeping — the SlotIndex-0 weapon ends up pointing exactly at the aim direction, and every other weapon
//   keeps its own normal relative spacing around that point instead of independently trying to aim on its
//   own (multiple weapons don't all converge on one point).
//
// Auto-aim, not free aim — reworked from the original cursor-driven version: this game is meant to stay
// low-input, and requiring the player to actively aim a whole ability's worth of continuous fire was too
// much manual input for that design goal. UpdateAutoAimAngle() (below) sources the direction from
// WeaponRangedPD.TryFindAutoAimDirection instead of cursor input — the closest living enemy within
// WeaponRange, found via a physics query, same deterministic/replay-safe reasoning that method's own doc
// comment gives. The original cursor-driven UpdateAimAngle() is deliberately left in place, just unused —
// kept in case free aim is ever wanted again (e.g. as a togglable variant) without needing to rebuild it.
//
// Never touches player movement — only weapon orbit/firing — so ForcesSpecialAction is false, same as
// RogueVanishAbilityPD/KnightBerserkAbilityPD.
//
// Also multiplies CharacterStatsPD.FireRate by (1 - fireRatePenaltyPercent) for the whole window (the
// tradeoff for never-miss continuous fire) via SetFocusFireRateMultiplier — a genuine percent-of-TOTAL
// reduction (2026-08-21 fix; see CharacterStatsPD.focusFireRateMultiplier's own doc comment for why an
// earlier additive-stacking version under-delivered once other fire-rate bonuses were involved), gated
// entirely on ContinuousFire so no separate reversal is needed at the window's end.
// No AbilityPower scaling (removed 2026-08-23) — duration and fireRatePenaltyPercent are both plain, fixed
// values. Duration snapshotted at cast time (_appliedDuration) so a live Inspector change mid-window can't
// leave the active timer running against a duration that no longer matches what was granted.
//
// Recastable while already active — a second press ends the window immediately instead of being blocked by
// IsReady (see its own doc comment) or queuing a fresh cast; EndFocusFire() is the single shared reversal
// both this and the natural/death end in Simulate() call, so the two paths can never drift apart.
//
// Death force-ends the buff early, same convention as every other buff this session (ExeBigAxeAbilityPD/
// KnightBerserkAbilityPD/RogueVanishAbilityPD/BWBlinkAbilityPD) — see Simulate()'s own comment.
public class ArcherFocusFireAbilityPD : PredictedIdentity<ArcherFocusFireAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float activeTimer;
        public bool  isActive;
        public float cooldownTimer;
        // Ability charge system — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment. Does NOT gate
        // the recast-to-cancel branch below (that's a free early-end on an already-paid-for cast, not a new
        // one) — only the fresh-cast branch of IsReady.
        public int charges;
        public int knownMaxCharges;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Focus Fire";
    [SerializeField, TextArea] private string description =
        "Stop orbiting and fire continuously at the closest enemy for 3 seconds, ignoring the need for a target in your weapons' path, at -50% Fire Rate. Recast to end early.";
    public float cooldown = 10f;
    // How many charges this ability starts with (1 = old off-cooldown behavior, unchanged) — see
    // IAbilityPD.MaxCharges's own doc comment. CharacterStatsPD.bonusAbility2Charges adds on top live.
    [SerializeField] private int authoredChargeCount = 1;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false (held at its full value
    // throughout the active window) — see Simulate(). true: cooldown starts ticking immediately at cast,
    // running concurrently with the window. Same convention/default as RogueVanishAbilityPD's own.
    [SerializeField] private bool cooldownStartsOnCast = false;
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability2;
    [SerializeField] private string[] tags;
    // false (default) — see IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract.
    [SerializeField] private bool activeSurgeBlock = false;

    [Header("Effect")]
    public float duration = 3f;
    public float fireRatePenaltyPercent = 0.5f;

    // Recast-to-cancel (see IsReady's own doc comment) is blocked for this long after a fresh cast — purely
    // a misclick guard, so a double-press/held-down button can't immediately end the window and burn the
    // whole cooldown for essentially nothing. Elapsed-since-cast is _appliedDuration - currentState.
    // activeTimer (activeTimer counts DOWN from _appliedDuration), not a separate timer field — one less
    // thing in State.
    [SerializeField] private float recastBlockDuration = 0.25f;

    // Augment-owned (2026-10-06, ArcherFocusFireRicochetAugment) — same [HideInInspector]/Add-at-Activate/
    // Remove-at-end pairing KnightBerserkAbilityPD.moveSpeedPercent established, 0f (no-op) default. Granted
    // via CharacterStatsPD.AddRicochetFlat only while this ability's own window is active, snapshotted into
    // _appliedRicochetFlat at Activate() time (same reasoning as _appliedDuration below — protects against a
    // live Inspector change mid-window leaving EndFocusFire() reversing a different amount than was granted).
    [HideInInspector] public float ricochetFlat = 0f;
    private float _appliedRicochetFlat;

    // Augment-owned (2026-10-06, ArcherFocusFireMultishotHomingAugment) — same Add-at-Activate/Remove-at-end
    // pairing as ricochetFlat above, just for CharacterStatsPD.AddMultishotFlat instead.
    [HideInInspector] public float multishotFlat = 0f;
    private float _appliedMultishotFlat;

    // Augment-owned (2026-10-06, ArcherFocusFireMultishotHomingAugment) — Pattern B switches/tunables, NOT
    // Add-at-Activate/Remove-at-end like ricochetFlat/multishotFlat above, since there's no permanent
    // CharacterStatsPD stat being granted here — these just steer what UpdateAutoAimAngle() writes to
    // CharacterStatsPD.FocusFireHomingTargetId/FocusFireHomingDelay every active tick (see that field's own
    // doc comment), which is already fully scoped to "only while this ability is active" by virtue of only
    // ever being called from Activate()/Simulate()'s own isActive-gated branch. homingProjectiles false
    // (default) means UpdateAutoAimAngle always writes `default` (no homing) regardless of what the
    // auto-aim search itself finds, same "augment absent == true no-op" shape every other Pattern B switch
    // in this codebase uses.
    [HideInInspector] public bool  homingProjectiles = false;
    [HideInInspector] public float homingDelay = 0.1f;

    // Augment-owned (2026-10-06, ArcherFocusFireMoveSpeedKnockbackAugment) — same Add-at-Activate/Remove-at-
    // end pairing as ricochetFlat/multishotFlat above, just for CharacterStatsPD.AddMoveSpeedTotalPercent/
    // AddKnockbackPercent instead.
    [HideInInspector] public float moveSpeedPercent = 0f;
    private float _appliedMoveSpeedPercent;
    [HideInInspector] public float knockbackPercent = 0f;
    private float _appliedKnockbackPercent;

    // Augment-owned (2026-10-06, ArcherFocusFireInvisibleAugment) — Pattern B switch, no-op default false.
    // Deliberately NOT an Add-at-Activate/Remove-at-end pairing like moveSpeedPercent/knockbackPercent above
    // — CharacterStatsPD.GrantInvisibility is its own "longest contribution wins" floor-timer, same contract
    // RogueVanishAbilityPD's own invisibility grant already relies on (see that class's doc comment): called
    // ONCE at Activate() time with this window's own _appliedDuration, and deliberately never explicitly
    // turned back off in EndFocusFire() — it just expires naturally on CharacterStatsPD's own shared timer,
    // so a DIFFERENT overlapping invisibility source (e.g. DeathPreventionInvisibleAugment) can never get
    // cut short by this ability's own window ending (early via recast-to-cancel, or on death).
    [HideInInspector] public bool grantInvisibility = false;

    private CharacterStatsPD   _stats;
    private PlayerControllerPD _controller;
    private WeaponManagerPD    _weaponManager;

    // Snapshotted at cast time (the plain duration field, no scaling — see class doc comment) — protects
    // against a live Inspector change mid-window. Also what IsReady's own recast-block elapsed-time math is
    // measured against (see recastBlockDuration's own doc comment).
    private float _appliedDuration;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats         = GetComponent<CharacterStatsPD>();
        _controller    = GetComponent<PlayerControllerPD>();
        _weaponManager = GetComponent<WeaponManagerPD>();
    }

    public Sprite Icon => icon;
    // True during the active window too (not just off cooldown), but only once recastBlockDuration has
    // elapsed since the cast that started it — see that field's own doc comment for why (misclick guard).
    // Before that grace window passes, a press correctly falls through to NotifyActivationBlocked() instead
    // of silently doing nothing, same as any other not-yet-ready press. Not blocked by the normal cooldown
    // gate once past the grace window (currentState.cooldownTimer sits at its full, unticked
    // EffectiveCooldown value throughout the window anyway, since cooldownStartsOnCast is false by default —
    // see Simulate()). Same "two distinct ready conditions" shape as BWBladeDanceAbilityPD's own Hold-phase
    // recast, and the same reason its BG-visibility side effect applies here too: LocalUIManagerPD/
    // PlayerUIWorldSpace's cooldown-BG derives straight off !IsReady, so it correctly stops greying out once
    // a press genuinely does something again (i.e. only after the grace window, not the instant of cast).
    public bool IsReady => _stats != null && (
        (currentState.isActive && _appliedDuration - currentState.activeTimer >= recastBlockDuration)
        || (!currentState.isActive && currentState.charges > 0));
    public bool IsActive => currentState.isActive;
    // Only ever flips weapon orbit/firing behavior — never touches player movement, so it never locks dash
    // or the other ability slot out while active (see IAbilityPD.ForcesSpecialAction's own doc comment).
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    // Same underlying window as IsActive for this ability (the whole Focus Fire window) — see IAbilityPD.
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
    public string Name => abilityName;
    public string Description => description;
    public string[] Tags => tags;
    public AbilitySlot Slot => slot;
    public AbilityIdentity Identity => AbilityIdentity.ArcherFocusFire;
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

        if (currentState.isActive)
        {
            // Recast-to-cancel — see IsReady's own doc comment. Shares the exact same reversal the natural/
            // death end in Simulate() uses, so the two paths can never drift apart.
            EndFocusFire(ref currentState);
            return;
        }

        // No AbilityPower scaling (removed 2026-08-23) — see class doc comment.
        _appliedDuration = duration;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        // Charge system — spend 1, and only (re)arm the regen timer if it wasn't already running — see
        // IAbilityPD.MaxCharges's own doc comment.
        currentState.charges--;
        if (currentState.cooldownTimer <= 0f) currentState.cooldownTimer = EffectiveCooldown;

        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in EndFocusFire() below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }

        _stats.SetContinuousFire(true);
        // Multiplicative against the FULLY computed FireRate (2026-08-21 fix — see
        // CharacterStatsPD.focusFireRateMultiplier's own doc comment for why this used to be an additive
        // AddFireRateTotalPercent call, which got diluted into near-nothing by any other fire-rate bonus a
        // player had stacked, instead of reliably taking fireRatePenaltyPercent off the TOTAL). Read straight
        // off the Inspector field, no snapshot needed since there's nothing left to "reverse" (see
        // EndFocusFire below).
        _stats.SetFocusFireRateMultiplier(1f - fireRatePenaltyPercent);
        UpdateAutoAimAngle();

        // 0f (no-op) until an augment bumps ricochetFlat — see that field's own doc comment.
        _appliedRicochetFlat = ricochetFlat;
        if (_appliedRicochetFlat != 0f) _stats.AddRicochetFlat(_appliedRicochetFlat);

        // 0f (no-op) until an augment bumps multishotFlat — see that field's own doc comment.
        _appliedMultishotFlat = multishotFlat;
        if (_appliedMultishotFlat != 0f) _stats.AddMultishotFlat(_appliedMultishotFlat);

        // 0f (no-op) until an augment bumps moveSpeedPercent/knockbackPercent — see those fields' own doc
        // comments.
        _appliedMoveSpeedPercent = moveSpeedPercent;
        if (_appliedMoveSpeedPercent != 0f) _stats.AddMoveSpeedTotalPercent(_appliedMoveSpeedPercent);
        _appliedKnockbackPercent = knockbackPercent;
        if (_appliedKnockbackPercent != 0f) _stats.AddKnockbackPercent(_appliedKnockbackPercent);

        // See grantInvisibility's own doc comment — no-op false until ArcherFocusFireInvisibleAugment sets
        // it; no revert needed in EndFocusFire(), same as RogueVanishAbilityPD's own identical grant.
        if (grantInvisibility) _stats.GrantInvisibility(_appliedDuration);

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    // Shared by both the recast-to-cancel path in Activate() and the natural/death end in Simulate() — see
    // class doc comment for why this must be the single source of truth for the reversal.
    private void EndFocusFire(ref State state)
    {
        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        // No separate fire-rate reversal needed — CharacterStatsPD.FireRate only ever reads
        // focusFireRateMultiplier while ContinuousFire is true, so clearing that flag here already fully
        // restores normal fire rate on its own (see focusFireRateMultiplier's own doc comment).
        _stats?.SetContinuousFire(false);
        if (_appliedRicochetFlat != 0f) _stats?.AddRicochetFlat(-_appliedRicochetFlat);
        if (_appliedMultishotFlat != 0f) _stats?.AddMultishotFlat(-_appliedMultishotFlat);
        if (_appliedMoveSpeedPercent != 0f) _stats?.AddMoveSpeedTotalPercent(-_appliedMoveSpeedPercent);
        if (_appliedKnockbackPercent != 0f) _stats?.AddKnockbackPercent(-_appliedKnockbackPercent);
        // Hygiene only — WeaponRangedPD.Fire()'s ContinuousFire branch is already gated behind
        // _stats.ContinuousFire (cleared right above), so a stale target id here could never actually be
        // read, but clearing it keeps no ability-owned temp CharacterStatsPD state lingering past this
        // window's end, same spirit as every other reversal in this method.
        _stats?.SetFocusFireHomingTargetId(default);
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true — see IAbilityPD.CancelActiveWindow's own doc comment.
    public void CancelActiveWindow() { }

    // Free-aim version — no longer called (see class doc comment), kept in place in case free aim is ever
    // wanted again. Live cursor direction converted to OrbitalPosition's own Atan2-based baseAngle
    // convention — see CharacterStatsPD.FocusFireAimAngle's own doc comment for the full formula reasoning.
    private void UpdateAimAngle()
    {
        if (_stats == null) return;
        Vector3 dir = _controller != null && _controller.currentState.cursorDir != Vector3.zero
            ? _controller.currentState.cursorDir
            : transform.forward;
        _stats.SetFocusFireAimAngle(Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg);
    }

    // Auto-aim version — what's actually called now (see class doc comment). Sources the direction from
    // WeaponRangedPD.TryFindAutoAimDirection (closest living enemy within the caster's own true
    // weapon reach —
    // WeaponManagerPD.TryGetFarthestWeaponReach, same "inherit from the actual weapon" pattern
    // RogueSpinAbilityPD/ExeAxeThrowAbilityPD/ArcherArrowRainAbilityPD/BWBladeDanceAbilityPD already use,
    // falling back to the plain WeaponRange stat if no weapon is registered) instead of cursor input, then
    // converts it the same Atan2 way UpdateAimAngle() above does. Automatically picks up Archer's own
    // weaponRangeScalingPercent this way (see CharacterStatsPD.EffectiveWeaponRange's own doc comment) without
    // needing a separate hand-tuned multiplier here. Falls back to the character's current forward facing if
    // no enemy is currently in range, same "always have SOME defined direction to freeze the ring at"
    // fallback the free-aim version used for a zero cursorDir. Called every active tick (not just once at
    // Activate()) so the aim keeps tracking the closest enemy — which can change — for the whole window.
    private void UpdateAutoAimAngle()
    {
        if (_stats == null) return;
        // EffectiveWeaponRange fallback (2026-09-16, was plain WeaponRange) — matches TryGetFarthestWeaponReach's
        // own EffectiveWeaponRange-based GetOuterReach() scale, same reasoning ExeAxeThrowAbilityPD's own
        // identical trueReach fallback uses.
        float searchRange = _weaponManager != null && _weaponManager.TryGetFarthestWeaponReach(out float trueReach)
            ? trueReach
            : _stats.EffectiveWeaponRange;
        bool found = WeaponRangedPD.TryFindAutoAimDirection(_stats.transform.position, searchRange, out Vector3 aimDir, out PredictedObjectID targetId);
        Vector3 dir = found ? aimDir : transform.forward;
        _stats.SetFocusFireAimAngle(Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg);
        // See CharacterStatsPD.ContinuousFireHasTarget's own doc comment — the actual firing weapon's own
        // LateSimulate() reads this to decide whether ContinuousFire should actually fire this tick, not just
        // where to aim (WeaponRangedPD or WeaponRangedPD, whichever is currently wired).
        _stats.SetContinuousFireHasTarget(found);
        // See CharacterStatsPD.FocusFireHomingTargetId's own doc comment — written every tick regardless
        // (never left stale from a previous tick), only ever non-default when homingProjectiles is actually
        // true (ArcherFocusFireMultishotHomingAugment) AND the same search above found a live enemy.
        _stats.SetFocusFireHomingTargetId(homingProjectiles && found ? targetId : default);
        _stats.SetFocusFireHomingDelay(homingDelay);
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

        UpdateAutoAimAngle();

        state.activeTimer -= delta;
        // Death force-ends the window early — see ExeBigAxeAbilityPD.Simulate()'s own identical comment for
        // the full reasoning (same shape here: a dead caster gets nothing from Focus Fire, and the cooldown
        // otherwise couldn't start ticking until this window ends anyway).
        bool forceEndOnDeath = _stats != null && _stats.IsDead;
        if (state.activeTimer > 0f && !forceEndOnDeath) return;

        EndFocusFire(ref state);
    }
}
