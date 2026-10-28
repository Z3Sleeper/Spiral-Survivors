using PurrNet.Prediction;
using UnityEngine;

// Bladeweaver's second class ability, built on the IAbilityPD architecture — gated by a cooldown. The
// teleport itself is instant (one Activate() call), but now has a real active window after it: a fixed-
// duration +Total-tier Move Speed buff (AddMoveSpeedTotalPercent, 2026-09-30 — was Bonus-tier via
// AddMoveSpeedPercent, migrated back to Total the same day that whole Bonus-tier percent pair was removed
// from CharacterStatsPD, see bonusMoveSpeedFlat's own doc comment there), same "instant effect immediately
// followed by a timed self-buff" shape as RogueVanishAbilityPD (that file is the template the buff plumbing
// below mirrors — see its own comments for the fuller reasoning behind each piece, only summarized here).
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
// No AbilityPower scaling (removed 2026-08-23) — duration and moveSpeedPercent are both plain, fixed
// values, snapshotted at cast time so a live Inspector change mid-buff can't leave the active timer running
// against a duration that no longer matches what was granted.
//
// Death force-ends the buff early, same convention as every other buff this session (ExeBigAxeAbilityPD/
// KnightBerserkAbilityPD/RogueVanishAbilityPD) — see Simulate()'s own comment.
public class BWBlinkAbilityPD : PredictedIdentity<BWBlinkAbilityPD.State>, IAbilityPD
{
    public struct State : IPredictedData<State>
    {
        public float activeTimer;
        public bool  isActive;
        public float cooldownTimer;
        // Ability charge system — see IAbilityPD.CurrentCharges/MaxCharges's own doc comment. Still fully
        // in effect for THIS ability despite the isActive exception below — the exception only means a
        // still-active buff doesn't block casting, not that a cast is free.
        public int   charges;
        public int   knownMaxCharges;
        public void Dispose() { }
    }

    [SerializeField] private Sprite icon;
    [SerializeField] private string abilityName = "Blink";
    [SerializeField, TextArea] private string description =
        "Instantly teleport to wherever your blades are currently orbiting, then gain +30% Move Speed for 2 seconds. Only usable while Blade Dance is active.";
    public float cooldown = 10f;
    // How many charges this ability starts with (1 = old off-cooldown behavior, unchanged) — see
    // IAbilityPD.MaxCharges's own doc comment. CharacterStatsPD.bonusAbility2Charges adds on top live.
    [SerializeField] private int authoredChargeCount = 1;
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

    [Header("Effect")]
    public float duration = 2f;
    public float moveSpeedPercent = 0.30f;

    // Live multipliers (2026-10-06, BWBlinkMoveSpeedDurationAugment) — 1f (neutral) default,
    // [HideInInspector] since both are augment-owned, never hand-tuned here. Augments ADD to these rather
    // than destructively multiplying duration/moveSpeedPercent directly — standing project convention
    // against multiplicative stacking for this kind of "+X% to Y" augment effect, same shape
    // ArcherArrowRainAbilityPD.durationMultiplier/BWBladeDanceAbilityPD.durationMultiplier established.
    // EffectiveMoveSpeedPercent/EffectiveDuration (not the raw fields) are what ApplyBuffAndCooldown's own
    // _appliedMoveSpeedPercent/_appliedDuration snapshots actually read.
    [HideInInspector] public float moveSpeedMultiplier = 1f;
    [HideInInspector] public float durationMultiplier = 1f;
    private float EffectiveMoveSpeedPercent => moveSpeedPercent * moveSpeedMultiplier;
    private float EffectiveDuration => duration * durationMultiplier;

    // Augment-owned (2026-10-06, BWBlinkInvisibleAugment) — Pattern B switch, no-op default false.
    // Deliberately NOT an Add-at-grant/Remove-at-end pairing like moveSpeedMultiplier above —
    // CharacterStatsPD.GrantInvisibility is its own "longest contribution wins" floor-timer, same contract
    // RogueVanishAbilityPD/ArcherFocusFireAbilityPD's own invisibility grants already rely on: called ONCE
    // in ApplyBuffAndCooldown() with this window's own EffectiveDuration, and deliberately never explicitly
    // turned back off — it just expires naturally on CharacterStatsPD's own shared timer, so a DIFFERENT
    // overlapping invisibility source (e.g. DeathPreventionInvisibleAugment) can never get cut short by
    // this window ending (natural expiry, death, or a mid-buff recast).
    [HideInInspector] public bool grantInvisibility = false;

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

    // Not scaled by anything (see class doc comment), so this is just a plain snapshot of the field at
    // cast time, reversed with the identical value at buff-end.
    private float _appliedMoveSpeedPercent;
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

    // Gated on BWBladeDanceAbilityPD.IsActive (Outbound/Hold/Return, never Idle) on top of the usual charge
    // check — see class doc comment for why this can only ever be CAST mid-dance (the resulting Move Speed
    // buff itself has nothing to do with the dance's own state once it's running). Deliberately NOT gated on
    // !currentState.isActive (2026-09-30, BWBlink-specific — not a general ability convention) — having a
    // charge available while the previous Move Speed buff is still ticking is allowed to recast:
    // ApplyBuffAndCooldown itself handles cleanly expiring the old buff before granting the new one, see its
    // own doc comment. Still spends a real charge either way (2026-10-02) — this exception only waives the
    // isActive gate, not the charge cost itself.
    public bool IsReady => _stats != null && currentState.charges > 0
        && _bladeDance != null && _bladeDance.IsActive;
    public bool IsActive => currentState.isActive;
    // Only ever flips Move Speed — never touches movement itself beyond the instant teleport (already
    // resolved by the time this would matter), so it never locks dash or the other ability slot out while
    // active (see IAbilityPD.ForcesSpecialAction's own doc comment).
    public bool ForcesSpecialAction => false;
    public bool ActiveSurgeBlock => activeSurgeBlock;
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
    public AbilityIdentity Identity => AbilityIdentity.BWBlink;
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
    // hasn't ended yet to let it start ticking. Note this ability defaults cooldownStartsOnCast to TRUE (see
    // its own doc comment), so in practice IsCooldownActivelyTicking is just cooldownTimer > 0f here — but
    // the full condition is kept for consistency/safety in case that default is ever flipped per-instance.
    // MaxCharges > 1 (2026-10-02) — moot in practice here since cooldownStartsOnCast already defaults true
    // for this ability (see that field's own doc comment), but kept for consistency/safety same as the rest
    // of that comment already notes.
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
        if (!IsReady) return;

        // Cleanly ends a real dash still in progress at blink time (2026-10-18 fix, same root cause/fix as
        // RogueVanishAbilityPD's blinkOnCast — see its own doc comment for the full mechanism). This
        // ability's ForcesSpecialAction is false ("never touches movement itself beyond the instant
        // teleport, already resolved by the time this would matter") — true for the TARGET position's own
        // safety, but that reasoning missed that an active dash's state.isDashing/dashVelocity survives
        // completely untouched through the teleport otherwise, then keeps being applied from the NEW
        // post-teleport position on every later tick with zero re-validation. Called first, before
        // fromPosition is read below, so any correction this applies is reflected in the blink's own
        // starting point too.
        _controller?.EndDashIfActive();

        // Fired at the OLD position, before anything moves — see class doc comment for why Started/Ended
        // straddle the teleport instead of both firing together. Also what DashEffectDoubleTriggerAugment
        // listens to now (2026-10-10, replaces the removed NotifyRealDashStarted/RealDashEnded pair) — see
        // PlayerControllerPD._onDashMovementEnded's own field comment.
        _controller?.NotifyDashStarted();
        _controller?.NotifyDashMovementStarted();

        // See class doc comment for why this is currentOrigin (the blades' LIVE position), not
        // _bladeDance.currentState.targetPosition (their final resting point).
        Vector3 rawBlinkTarget = _bladeDance.currentState.currentOrigin;

        // Clamped through TeleportSafetyPD (2026-09-19) — see that class's own doc comment for the full
        // algorithm/design discussion. rawBlinkTarget can legitimately land inside a wall/obstacle (the
        // blades' own live orbit position has no obstacle-awareness of its own), which would otherwise strand
        // the player inside solid geometry. radius/stepSize both come off the player's own real physics
        // collider (PlayerControllerPD.PhysicsColliderRadius) — stepSize is deliberately the RAW radius * 0.5.
        // checkRadius adds a small margin ON TOP of the true radius (2026-09-21 fix, was physicsRadius - 0.05f
        // — a MORE permissive margin that could accept a spot before the player's real collider had actually
        // cleared the wall by a hair, causing a visible "boop" the instant the player appeared there as
        // physics resolved that residual overlap; same root cause and same fix as the equivalent dash-landing
        // bug, see PlayerControllerPD.ResolveSafeTravelVelocity's own doc comment for the full derivation) so
        // the resolved blink target has genuine clearance up front instead.
        //
        // FindSafeTeleportPositionMultiDirectional, not the plain two-direction FindSafeTeleportPosition
        // (2026-09-21) — a teleport, unlike a dash, is free to resolve to ANY direction around the target, not
        // just backward-toward-start/forward-continuing, since it never physically travels through the space
        // in between. Confirmed live this matters: blinking well past the midpoint of a long, narrow obstacle
        // used to always resolve to whichever END of the obstacle was closer along the travel axis, even when
        // stepping straight out the SIDE would have been a dramatically shorter, more natural-looking escape
        // that the two-direction search had no way to even consider. Layer mask covers both wall colliders
        // (Bounds) and decorative obstacles (Obstacle) — confirmed these are the two real layers a player can
        // physically get stuck in, NOT BoundsCollision (that one's the player's own layer, unrelated to what
        // blocks them). Direction is still straight from the pre-teleport position toward rawBlinkTarget (only
        // used as the search's own "forward"/tie-break-winning reference now, not a hard axis) — total failure
        // (null) falls back to the pre-teleport position itself, i.e. "don't move at all," same as this
        // ability's own original design; that fallback is this call site's own choice, not something
        // TeleportSafetyPD bakes in generically (see its own doc comment for why).
        //
        // isValidPosition (2026-09-20) — the blades' live origin has no awareness of the playable area's own
        // boundary either, so without this a blink could land (or, worse, get "corrected" by the search above
        // into landing) past the wall entirely, in the genuinely open, uncollided space beyond it — confirmed
        // live. PlayableAreaPD.IsInsidePlayableArea is a real point-in-polygon test (correct for a concave
        // playable area too), not a rough bounding-box approximation — see TeleportSafetyPD's own doc comment.
        // PlayableAreaPD.instance is null-checked since not every scene necessarily has one (e.g. a menu or
        // test scene) — no boundary constraint applies there, same as before this fix.
        Vector3 fromPosition = _stats.transform.position;
        Vector2 blinkDirection2D = new Vector2(rawBlinkTarget.x - fromPosition.x, rawBlinkTarget.z - fromPosition.z);
        float physicsRadius = _controller != null ? _controller.PhysicsColliderRadius : 0.5f;
        float blinkStepSize = physicsRadius * 0.5f;
        Vector3 blinkTarget = TeleportSafetyPD.FindSafeTeleportPositionMultiDirectional(
            rawBlinkTarget,
            blinkDirection2D,
            blinkStepSize,
            physicsRadius + blinkStepSize,
            LayerMask.GetMask("Bounds", "Obstacle"),
            isValidPosition: PlayableAreaPD.instance != null ? PlayableAreaPD.instance.IsInsidePlayableArea : (System.Func<Vector3, bool>)null) ?? fromPosition;

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

        // Fired at the NEW (blinked-to) position — see class doc comment. interrupted: false — an instant
        // teleport has nothing mid-flight to interrupt.
        _controller?.NotifyDashEnded();
        _controller?.NotifyDashMovementEnded(false);

        ApplyBuffAndCooldown();
    }

    // Shared by a real cast (Activate(), above) and the auto-cast that fires when the Blade Dance finishes
    // on its own (AutoCastOnDanceReturn(), below) — everything about Blink EXCEPT the teleport itself: the
    // Move Speed buff, its duration (same cast-time-snapshot convention as RogueVanishAbilityPD), the
    // cooldown, the surge-block pairing, and the Ability Started notification. Kept as one shared method
    // rather than duplicated across both call sites so the two can never drift out of sync — e.g. a future
    // surge-block change would otherwise need to be made twice.
    // BWBlink-specific recast rule (2026-09-30, see IsReady's own doc comment): if the previous cast's Move
    // Speed buff is STILL active when this fires again (coming off cooldown mid-buff), the old grant is
    // reverted first — surgeBlockCount is a strict 1:1-paired ref count (see AddSurgeBlock's own doc
    // comment), so an unpaired extra Add here would leak a permanent +1 that Simulate()'s single end-of-active
    // RemoveSurgeBlock call could never fully unwind on a buff that's been refreshed more than once. Net
    // effect reads as "expires the old buff, grants a fresh one" — currentState.isActive never actually
    // flips false/true in between (no visible flicker), just activeTimer/the granted amount resetting to
    // this cast's own values.
    private void ApplyBuffAndCooldown()
    {
        if (currentState.isActive)
        {
            if (activeSurgeBlock) _stats.RemoveSurgeBlock();
            _stats.AddMoveSpeedTotalPercent(-_appliedMoveSpeedPercent);
        }

        // No AbilityPower scaling (removed 2026-08-23) — see class doc comment. Reads the Effective*
        // properties (moveSpeedPercent/duration scaled by their own live multipliers), not the raw fields —
        // see those multipliers' own doc comment.
        _appliedMoveSpeedPercent = EffectiveMoveSpeedPercent;
        _appliedDuration              = EffectiveDuration;

        currentState.activeTimer   = _appliedDuration;
        currentState.isActive      = true;
        // Charge system — spend 1, and only (re)arm the regen timer if it wasn't already running — see
        // IAbilityPD.MaxCharges's own doc comment. Applies identically whether this came from a real
        // Activate() or AutoCastOnDanceReturn()'s own auto-cast — both genuinely consume a charge.
        currentState.charges--;
        if (currentState.cooldownTimer <= 0f) currentState.cooldownTimer = EffectiveCooldown;

        // See IAbilityPD.ActiveSurgeBlock's own doc comment for the generic contract — RemoveSurgeBlock's
        // matching call sits in Simulate()'s own end-of-active block below (or in the revert block above, on
        // a mid-buff recast).
        if (activeSurgeBlock)
        {
            _stats.ForceEndSurge();
            _stats.AddSurgeBlock();
        }
        _stats.AddMoveSpeedTotalPercent(_appliedMoveSpeedPercent);

        // See grantInvisibility's own doc comment — no-op false until BWBlinkInvisibleAugment sets it; no
        // revert needed anywhere, same as RogueVanishAbilityPD/ArcherFocusFireAbilityPD's own identical
        // grants.
        if (grantInvisibility) _stats.GrantInvisibility(_appliedDuration);

        // Fired the instant AbilityIsActive flips true — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats.NotifyAbilityStart(this);
    }

    // Called directly by BWBladeDanceAbilityPD.Simulate() the instant the dance's blades arrive back home
    // under their own power (natural Return-phase completion ONLY — that call site deliberately never
    // reaches here from the death-forced end, since there's no point buffing a caster who's already dead).
    // Grants Blink's usual Move Speed buff/duration/cooldown/Ability Started notification exactly as if it
    // had really been cast. Deliberately does NOT fire DashStarted/DashEnded, DashMovementStarted/
    // DashMovementEnded, or RealDashStarted/RealDashEnded (2026-10-10, all six removed — was "back to back,
    // no teleport straddled in between," since there's nowhere left to teleport TO; on review this wasn't
    // good enough reasoning to justify DashExplosionAugment/DashHealAugment/DashEffectDoubleTriggerAugment
    // etc. still paying out for an auto-cast that never actually dashed) — all six now only ever fire from
    // a REAL Blink cast (Activate(), above). Position snap, invincibility grace, and RecallInstantly were
    // already skipped here for the same "nothing to teleport to" reason and remain skipped. Only gated on
    // cooldown, deliberately NOT the full IsReady (which also requires
    // _bladeDance.IsActive) — that gate exists so a MANUAL cast can't teleport into a dance that isn't
    // running; this only ever fires the instant the dance STOPS running, so that same check would
    // incorrectly fail every single time here. Also deliberately NOT gated on !currentState.isActive
    // (2026-09-30, same BWBlink-specific recast rule as IsReady/ApplyBuffAndCooldown — see their own doc
    // comments) — an auto-cast landing while the previous buff is still active should refresh it exactly
    // like a manual recast does, not be skipped. A manual Blink cast mid-dance already puts this on cooldown
    // itself, so when RecallInstantly's own follow-up Return-arrival tick reaches this same call site one
    // tick later, the cooldown check here still naturally no-ops it — no separate guard needed against
    // double-triggering off a player-initiated Blink. Charge-gated now (2026-10-02), not cooldown-gated —
    // see IAbilityPD.CurrentCharges/MaxCharges's own doc comment; a manual cast's own charge spend still
    // naturally no-ops this the same way the old cooldown check did, since spending the last charge leaves
    // currentState.charges at 0 for however long it takes to regen one back.
    public void AutoCastOnDanceReturn()
    {
        if (_stats == null || _stats.IsDead || currentState.charges <= 0) return;
        ApplyBuffAndCooldown();
    }

    public void NotifyActivationBlocked() { }
    // Never sets its own ActiveSurgeBlock true — see IAbilityPD.CancelActiveWindow's own doc comment.
    public void CancelActiveWindow() { }

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
            // Move Speed window) or only once that window ends (state.isActive false) — see the field's own
            // doc comment.
            // effectiveMax > 1 (2026-10-02) — moot here too, same reasoning as IsCooldownActivelyTicking's
            // own comment above.
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
        // Death force-ends the buff early — see ExeBigAxeAbilityPD.Simulate()'s own identical comment for
        // the full reasoning (same shape here: a dead caster gets nothing from Move Speed, and the cooldown
        // otherwise couldn't start ticking until this window ends anyway).
        bool forceEndOnDeath = _stats != null && _stats.IsDead;
        if (state.activeTimer > 0f && !forceEndOnDeath) return;

        state.isActive = false;
        if (activeSurgeBlock) _stats?.RemoveSurgeBlock();
        _stats?.AddMoveSpeedTotalPercent(-_appliedMoveSpeedPercent);
        // Fired the instant AbilityIsActive flips false — see IAbilityPD.AbilityIsActive's own doc comment.
        if (cooldown > 0f) _stats?.NotifyAbilityEnd(this);
    }
}
