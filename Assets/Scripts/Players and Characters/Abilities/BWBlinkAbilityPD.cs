using PurrNet.Prediction;
using UnityEngine;

// Bladeweaver's second class ability, built on the IAbilityPD architecture — gated by a cooldown. The
// teleport itself is instant (one Activate() call), but now has a real active window after it: a fixed-
// duration +Total Move Speed buff, same "instant effect immediately followed by a timed self-buff" shape as
// RogueVanishAbilityPD (that file is the template the buff plumbing below mirrors — see its own comments for
// the fuller reasoning behind each piece, only summarized here).
//
// Only usable while BWBladeDanceAbilityPD is active (Outbound, Hold, OR Return — see IsReady) — teleports
// the caster straight to wherever the dance's blades are CURRENTLY orbiting (currentState.currentOrigin,
// read directly off the sibling ability component), not the dance's fixed targetPosition. That's
// deliberate: during Outbound/Return the blades are still mid-travel, so this reads whatever point they
// happen to be passing through at the exact instant Blink is cast, matching the ability's own literal
// framing ("reposition to where its blades currently are orbiting") rather than always jumping to the final
// hold point.
//
// The teleport itself goes through the caster's own PredictedRigidbody (direct .position assignment, NOT
// MovePosition — an instant snap, not a physics-interpolated move) followed by PredictedTransform.
// ResetInterpolation() on the sibling PredictedTransform component, per PurrDiction's own guidance for any
// hard teleport (https://purrnet.dev/docs/client-side-prediction/built-in-components/predicted-transform):
// without it, the smoothed Visuals child would visibly slide from the old position to the new one over the
// next several frames instead of snapping instantly, since ModifyRollbackViewState's own correction-error
// smoothing has no way to tell "legitimate teleport" apart from "normal position correction" otherwise.
//
// Immediately followed by _bladeDance.RecallInstantly() (see its own doc comment) — without that, the dance
// would keep running and the blades would spend their normal Return travel time chasing the player's new,
// blinked-to position instead of already being there. Net effect: player jumps to the blades, blades jump
// back to the player, both land on the same point with no lingering travel either direction.
//
// Also fires the same OnDashStarted/OnDashEnded listeners a real dash would (DashExplosionAugment,
// DashHealAugment, etc.) — same "this functions like a dash" convention RogueSpinAbilityPD's own leap
// already uses. Started is fired BEFORE the teleport (listeners that read Stats.transform.position at that
// point see the OLD position) and Ended AFTER (listeners see the NEW, blinked-to position) — so e.g.
// DashExplosionAugment's own OnDashEnded explosion lands at the destination, not where the blink began.
//
// cooldownStartsOnCast is true here (unlike most other buff abilities' own false default) — the cooldown
// starts ticking the instant Blink is cast, running concurrently with the Move Speed buff, rather than only
// starting once that buff ends. See the field's own doc comment for why.
//
// No AbilityPower scaling (removed 2026-08-23) — duration and moveSpeedTotalPercent are both plain, fixed
// values, snapshotted at cast time so a live Inspector change mid-buff can't leave the active timer running
// against a duration that no longer matches what was granted.
//
// Death force-ends the buff early, same convention as every other buff this session (ExeBigAxeAbilityPD/
// KnightBigBuffAbilityPD/RogueVanishAbilityPD) — see Simulate()'s own comment.
public class BWBlinkAbilityPD : PredictedIdentity<BWBlinkAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float activeTimer;
        public bool  isActive;
        public float cooldownTimer;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Blink";
    [SerializeField, TextArea] private string description =
        "Instantly teleport to wherever your blades are currently orbiting, then gain +25% Total Move Speed for 2 seconds. Only usable while Blade Dance is active.";
    public float cooldown = 10f;
    // true — cooldown starts ticking immediately at cast, running concurrently with the Move Speed buff,
    // rather than only starting once that buff ends. Deliberately NOT RogueVanishAbilityPD's default here —
    // gating the cooldown behind the buff's own duration felt bad specifically for Blink: the teleport
    // itself is the actual payoff (repositioning with the blades), the Move Speed buff is a minor bonus on
    // top, so the 10s cooldown shouldn't effectively become 12s just because a short buff happens to be
    // riding along with it. See Simulate().
    [SerializeField] private bool cooldownStartsOnCast = true;
    [SerializeField] private AbilitySlot slot = AbilitySlot.Ability2;
    [SerializeField] private string[] tags;
    // false (default) — see IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract.
    [SerializeField] private bool activeSurgeBlock = false;
    // Action — a committed teleport. Exposed for live playtesting — see IAbilityPD.IsActionAbility's own
    // doc comment.
    [SerializeField] private bool isActionAbility = true;

    [Header("Effect")]
    public float duration = 2f;
    public float moveSpeedTotalPercent = 0.25f;

    // Invincibility grace granted the instant the teleport lands (see Activate()) — same GrantInvincibility
    // mechanism RogueSpinAbilityPD's own postActiveInvincibilityGrace uses, just at a different moment (right
    // after landing, not at the end of some later window) since it's the teleport itself that's disorienting,
    // not the Move Speed buff wearing off. Deliberately NOT added to duration/currentState.activeTimer —
    // GrantInvincibility is its own independent floor-based timer (see CharacterStatsPD.GrantInvincibility's
    // own doc comment: every source just contributes its own duration, longest currently active wins), so
    // this doesn't extend AbilityIsActive/the cooldown-start point at all — pure QoL so a stray hit doesn't
    // punish that split second of reorientation right after blinking.
    [SerializeField] private float postBlinkInvincibility = 0.2f;

    private CharacterStatsPD _stats;
    // All fetched via GetComponent in Awake() — PredictedRigidbody, PredictedTransform, and every ability
    // component all live on the same root GameObject on this project's player prefabs (unlike
    // PlayerControllerPD's own _rigidbody field, which is Inspector-wired since that particular component
    // predates this assumption — nothing to misassign here either way since PredictedRigidbody's own
    // [RequireComponent(typeof(PredictedTransform))] guarantees the second is always right there too).
    private BWBladeDanceAbilityPD _bladeDance;
    private PredictedRigidbody _rigidbody;
    private PredictedTransform _predictedTransform;
    private PlayerControllerPD _controller;

    // Flat — not scaled by anything (see class doc comment), so this is just a plain snapshot of the field
    // at cast time, reversed with the identical value at buff-end.
    private float _appliedMoveSpeedTotalPercent;
    // Snapshotted alongside it — see IAbilityPD.ActiveTimeDuration's own doc comment for why the UI needs
    // this fixed rather than reading the raw duration field live, in case it changes mid-buff.
    private float _appliedDuration;

    protected override State GetInitialState() => default;

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
        _bladeDance = GetComponent<BWBladeDanceAbilityPD>();
        _rigidbody = GetComponent<PredictedRigidbody>();
        if (_rigidbody != null) _predictedTransform = _rigidbody.GetComponent<PredictedTransform>();
        _controller = GetComponent<PlayerControllerPD>();
    }

    public Sprite Icon => icon;

    // Gated on BWBladeDanceAbilityPD.IsActive (Outbound/Hold/Return, never Idle) on top of the usual
    // active/cooldown checks — see class doc comment for why this can only ever be CAST mid-dance (the
    // resulting Move Speed buff itself has nothing to do with the dance's own state once it's running).
    public bool IsReady => _stats != null && !currentState.isActive && currentState.cooldownTimer <= 0f
        && _bladeDance != null && _bladeDance.IsActive;
    public bool IsActive => currentState.isActive;
    // Only ever flips Move Speed — never touches movement itself beyond the instant teleport (already
    // resolved by the time this would matter), so it never locks dash or the other ability slot out while
    // active (see IAbilityPD.ForcesSpecialAction's own doc comment).
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
    public bool IsActionAbility => isActionAbility;
    // Same underlying window as IsActive for this ability (the whole Move Speed buff) — see IAbilityPD.
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
    // hasn't ended yet to let it start ticking. Note this ability defaults cooldownStartsOnCast to TRUE (see
    // its own doc comment), so in practice IsCooldownActivelyTicking is just cooldownTimer > 0f here — but
    // the full condition is kept for consistency/safety in case that default is ever flipped per-instance.
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
        if (!IsReady) return;

        // Fired at the OLD position, before anything moves — see class doc comment for why Started/Ended
        // straddle the teleport instead of both firing together.
        _controller?.NotifyDashStarted();
        _controller?.NotifyDashMovementStarted();

        // See class doc comment for why this is currentOrigin (the blades' LIVE position), not
        // _bladeDance.currentState.targetPosition (their final resting point).
        Vector3 blinkTarget = _bladeDance.currentState.currentOrigin;

        if (_rigidbody != null) _rigidbody.position = blinkTarget;
        // Redundant-looking on top of the Rigidbody write above, but not actually redundant: a
        // Rigidbody.position write made from PurrDiction's own custom simulation tick (not Unity's native
        // FixedUpdate) isn't guaranteed to be reflected in transform.position within this same synchronous
        // continuation, so anything reading Stats.transform.position later in THIS same call (e.g.
        // NotifyDashEnded's own listeners just below — DashExplosionAugment, DashHealAugment,
        // PhantomBladeOnDashAugment) could still see the OLD, pre-teleport position. Same staleness risk
        // RecallInstantly's own explicit blinkTarget parameter (below) was already written to avoid — this
        // is the same fix applied to the Transform itself.
        _stats.transform.position = blinkTarget;
        _predictedTransform?.ResetInterpolation();

        // See postBlinkInvincibility's own doc comment — granted the instant we land, not at buff-end.
        _stats.GrantInvincibility(postBlinkInvincibility);

        // Recall the blades home the instant we land — see BWBladeDanceAbilityPD.RecallInstantly()'s own
        // doc comment: without this the blades would keep chasing the player's (now-teleported) position
        // over their normal Return travel time instead of coinciding with them immediately. Passed
        // blinkTarget explicitly (not left for RecallInstantly() to re-read _stats.transform.position
        // itself) — that read could still be stale for a few frames after the teleport (Rigidbody
        // interpolation catching the visual transform up to the just-assigned position), which was
        // previously causing a visible "blades chasing the player" shimmy instead of an instant snap.
        _bladeDance.RecallInstantly(blinkTarget);

        // Fired at the NEW (blinked-to) position — see class doc comment.
        _controller?.NotifyDashEnded();
        _controller?.NotifyDashMovementEnded();

        ApplyBuffAndCooldown();
    }

    // Shared by a real cast (Activate(), above) and the auto-cast that fires when the Blade Dance finishes
    // on its own (AutoCastOnDanceReturn(), below) — everything about Blink EXCEPT the teleport itself: the
    // Move Speed buff, its duration (same cast-time-snapshot convention as RogueVanishAbilityPD), the
    // cooldown, the surge-block pairing, and the Ability Started notification. Kept as one shared method
    // rather than duplicated across both call sites so the two can never drift out of sync — e.g. a future
    // surge-block change would otherwise need to be made twice.
    private void ApplyBuffAndCooldown()
    {
        // No AbilityPower scaling (removed 2026-08-23) — see class doc comment.
        _appliedMoveSpeedTotalPercent = moveSpeedTotalPercent;
        _appliedDuration              = duration;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        currentState.cooldownTimer = EffectiveCooldown;

        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in Simulate()'s own end-of-active block below.
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }
        _stats.AddMoveSpeedTotalPercent(_appliedMoveSpeedTotalPercent);

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    // Called directly by BWBladeDanceAbilityPD.Simulate() the instant the dance's blades arrive back home
    // under their own power (natural Return-phase completion ONLY — that call site deliberately never
    // reaches here from the death-forced end, since there's no point buffing a caster who's already dead).
    // Grants Blink's usual Move Speed buff/duration/cooldown/Ability Started notification exactly as if it
    // had really been cast, and still fires DashStarted/DashEnded (so DashExplosionAugment/DashHealAugment
    // etc. still trigger off an auto-cast Blink same as a manual one) — but back to back, with no teleport
    // straddled in between, since there's nowhere left to teleport TO (the blades already got there on
    // their own) and so nothing for Started/Ended to meaningfully bracket. Every OTHER teleport-specific
    // side effect (position snap, invincibility grace, RecallInstantly) is still skipped for the same
    // reason. Only gated on cooldown + not already active, deliberately NOT the full IsReady (which also
    // requires _bladeDance.IsActive) — that gate exists so a MANUAL cast can't teleport into a dance that
    // isn't running; this only ever fires the instant the dance STOPS running, so that same check would
    // incorrectly fail every single time here. A manual Blink cast mid-dance already puts this on cooldown
    // itself, so when RecallInstantly's own follow-up Return-arrival tick reaches this same call site one
    // tick later, the cooldown check here naturally no-ops it — no separate guard needed against
    // double-triggering off a player-initiated Blink.
    public void AutoCastOnDanceReturn()
    {
        if (_stats == null || _stats.IsDead || currentState.isActive || currentState.cooldownTimer > 0f) return;
        _controller?.NotifyDashStarted();
        _controller?.NotifyDashMovementStarted();
        _controller?.NotifyDashEnded();
        _controller?.NotifyDashMovementEnded();
        ApplyBuffAndCooldown();
    }

    public void NotifyActivationBlocked() { }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // cooldownStartsOnCast picks whether this ticks down immediately (concurrent with the whole Move
        // Speed window) or only once that window ends (state.isActive false) — see the field's own doc
        // comment. Either way Activate() already set cooldownTimer to its full value at cast time.
        if (state.cooldownTimer > 0f && (cooldownStartsOnCast || !state.isActive))
            state.cooldownTimer -= delta;

        if (!state.isActive) return;

        state.activeTimer -= delta;
        // Death force-ends the buff early — see ExeBigAxeAbilityPD.Simulate()'s own identical comment for
        // the full reasoning (same shape here: a dead caster gets nothing from Move Speed, and the cooldown
        // otherwise couldn't start ticking until this window ends anyway).
        bool forceEndOnDeath = _stats != null && _stats.IsDead;
        if (state.activeTimer > 0f && !forceEndOnDeath) return;

        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        _stats?.AddMoveSpeedTotalPercent(-_appliedMoveSpeedTotalPercent);
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }
}
