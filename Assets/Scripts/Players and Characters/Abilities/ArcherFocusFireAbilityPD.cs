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
// RogueVanishAbilityPD/KnightBigBuffAbilityPD.
//
// Also applies a -fireRatePenaltyPercent hit to CharacterStatsPD.FireRate for the whole window (the tradeoff
// for never-miss continuous fire) via AddFireRateTotalPercent, reversed by the identical amount at end.
// CharacterStatsPD.AbilityPower scales TWO things for this ability, in opposite directions:
// - The Fire Rate penalty SHRINKS rather than a bonus growing (higher AbilityPower = a lighter Fire Rate hit
//   while Focus Fire is up) — appliedPenalty = fireRatePenaltyPercent / AbilityPower, so the neutral
//   AbilityPower == 1 baseline reproduces the flat fireRatePenaltyPercent unchanged, shrinking toward 0 as
//   AbilityPower climbs.
// - The window's own duration GROWS, same ordinary "AbilityPower extends a buff's length" convention as
//   every other duration-scaling buff this session (ExeBigAxeAbilityPD/KnightBigBuffAbilityPD/
//   RogueVanishAbilityPD) — appliedDuration = duration * AbilityPower.
// Both snapshotted at cast time (_appliedFireRatePenaltyPercent/_appliedDuration) — a live AbilityPower
// change mid-window must not desync either add/subtract pair, or leave the active timer running against a
// duration that no longer matches what was granted.
//
// Recastable while already active — a second press ends the window immediately instead of being blocked by
// IsReady (see its own doc comment) or queuing a fresh cast; EndFocusFire() is the single shared reversal
// both this and the natural/death end in Simulate() call, so the two paths can never drift apart.
//
// Death force-ends the buff early, same convention as every other buff this session (ExeBigAxeAbilityPD/
// KnightBigBuffAbilityPD/RogueVanishAbilityPD/BWBlinkAbilityPD) — see Simulate()'s own comment.
public class ArcherFocusFireAbilityPD : PredictedIdentity<ArcherFocusFireAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float activeTimer;
        public bool  isActive;
        public float cooldownTimer;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Focus Fire";
    [SerializeField, TextArea] private string description =
        "Stop orbiting and fire continuously at the closest enemy for 3 seconds, ignoring the need for a target in your weapons' path, at -50% Fire Rate. Recast to end early.";
    [SerializeField] private float cooldown = 10f;
    // false (default): cooldown only starts ticking once AbilityIsActive goes false (held at its full value
    // throughout the active window) — see Simulate(). true: cooldown starts ticking immediately at cast,
    // running concurrently with the window. Same convention/default as RogueVanishAbilityPD's own.
    [SerializeField] private bool cooldownStartsOnCast = false;
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability2;
    [SerializeField] private string[] tags;
    // false (default) — see IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract.
    [SerializeField] private bool activeSurgeBlock = false;

    [Header("Effect")]
    [SerializeField] private float duration = 3f;
    [SerializeField] private float fireRatePenaltyPercent = 0.5f;
    // Recast-to-cancel (see IsReady's own doc comment) is blocked for this long after a fresh cast — purely
    // a misclick guard, so a double-press/held-down button can't immediately end the window and burn the
    // whole cooldown for essentially nothing. Elapsed-since-cast is _appliedDuration - currentState.
    // activeTimer (activeTimer counts DOWN from _appliedDuration), not a separate timer field — one less
    // thing in State.
    [SerializeField] private float recastBlockDuration = 0.25f;

    private CharacterStatsPD   _stats;
    private PlayerControllerPD _controller;

    // Snapshotted at cast time (fireRatePenaltyPercent / AbilityPower at that instant) — see class doc
    // comment. Reversed with this identical value at end regardless of whether AbilityPower changed mid-
    // window.
    private float _appliedFireRatePenaltyPercent;
    // Snapshotted at cast time (duration * AbilityPower at that instant) — AbilityPower's OTHER
    // interpretation for this ability: grows the window's own length, same convention as every duration-
    // scaling buff this session (ExeBigAxeAbilityPD/KnightBigBuffAbilityPD/RogueVanishAbilityPD). Also what
    // IsReady's own recast-block elapsed-time math is measured against (see recastBlockDuration's own doc
    // comment) rather than the raw, un-hasted duration field, so that math stays correct regardless of
    // whatever AbilityPower actually was at the moment of cast.
    private float _appliedDuration;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats      = GetComponent<CharacterStatsPD>();
        _controller = GetComponent<PlayerControllerPD>();
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
        || (!currentState.isActive && currentState.cooldownTimer <= 0f));
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

    // Same formula as CharacterStatsPD.DashCooldown (baseCooldown / (1 + haste/100)) — a haste augment
    // shortens this ability's cooldown the same way it already does for dash, or lengthens it if
    // EffectiveAbilityHaste ever goes negative (a penalty).
    private float EffectiveCooldown => _stats != null ? cooldown / (1f + _stats.EffectiveAbilityHaste / 100f) : cooldown;

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

        if (currentState.isActive)
        {
            // Recast-to-cancel — see IsReady's own doc comment. Shares the exact same reversal the natural/
            // death end in Simulate() uses, so the two paths can never drift apart.
            EndFocusFire(ref currentState);
            return;
        }

        // AbilityPower's interpretation for this ability: grows the window's own length — see
        // _appliedDuration's own doc comment.
        _appliedDuration = duration * _stats.AbilityPower;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        currentState.cooldownTimer = EffectiveCooldown;

        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in EndFocusFire() below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }

        _stats.SetContinuousFire(true);
        // AbilityPower's interpretation for this ability: shrinks the penalty rather than growing a bonus —
        // see class doc comment.
        _appliedFireRatePenaltyPercent = fireRatePenaltyPercent / _stats.AbilityPower;
        _stats.AddFireRateTotalPercent(-_appliedFireRatePenaltyPercent);
        UpdateAutoAimAngle();

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    // Shared by both the recast-to-cancel path in Activate() and the natural/death end in Simulate() — see
    // class doc comment for why this must be the single source of truth for the reversal.
    private void EndFocusFire(ref State state)
    {
        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        _stats?.SetContinuousFire(false);
        _stats?.AddFireRateTotalPercent(_appliedFireRatePenaltyPercent);
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }

    public void NotifyActivationBlocked() { }

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
    // WeaponRangedPD.TryFindAutoAimDirection (closest living enemy within WeaponRange of this character)
    // instead of cursor input, then converts it the same Atan2 way UpdateAimAngle() above does. Falls back
    // to the character's current forward facing if no enemy is currently in range, same "always have SOME
    // defined direction to freeze the ring at" fallback the free-aim version used for a zero cursorDir.
    // Called every active tick (not just once at Activate()) so the aim keeps tracking the closest enemy —
    // which can change — for the whole window.
    private void UpdateAutoAimAngle()
    {
        if (_stats == null) return;
        bool found = WeaponRangedPD.TryFindAutoAimDirection(_stats.transform.position, _stats.WeaponRange, out Vector3 aimDir);
        Vector3 dir = found ? aimDir : transform.forward;
        _stats.SetFocusFireAimAngle(Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg);
        // See CharacterStatsPD.ContinuousFireHasTarget's own doc comment — WeaponRangedPD.LateSimulate()
        // reads this to decide whether ContinuousFire should actually fire this tick, not just where to aim.
        _stats.SetContinuousFireHasTarget(found);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole active
        // window below) or only once that window ends (state.isActive false) — see the field's own doc
        // comment. Either way Activate() already set cooldownTimer to its full value at cast time.
        if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive))
            state.cooldownTimer -= delta;

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
