using PurrNet.Prediction;
using UnityEngine;

[RequireComponent(typeof(CharacterStatsPD))]
public class PlayerControllerPD : PredictedIdentity<PlayerControllerPD.Input, PlayerControllerPD.State>
{
    [SerializeField] private PredictedRigidbody _rigidbody;
    [SerializeField] private Transform _cameraTarget;
    // Scoped to the literal Dash input only (played directly in StartDash() below, not off the shared
    // _onDashStarted event) — RogueSpinAbilityPD's leap and BWBlinkAbilityPD's teleport also fire that same
    // event (see its own doc comment), so listening there instead would also sound for those. RogueSpin
    // plays its own distinct SFX directly at its own trigger site instead; Blink has none yet.
    [SerializeField] private AudioClip dashSfx;

    // 0f (no-op) until SurgeDashCDRateAugment sets it — extra multiplier added on top of the normal 1x
    // dash-charge-regen tick rate while CharacterStatsPD.IsSurging is true (1f = twice as fast overall).
    // Checked directly in Simulate()'s own charge-regen tick below — a plain cross-identity READ of
    // _stats.IsSurging, same safety class as the dashCharges/IsDead reads already in this file's Simulate()
    // — same shape as KnightBerserkAbilityPD.lowHealthCooldownBonusMultiplier (that ability's own
    // low-health cooldown haste), just gated on IsSurging instead of a health threshold. [HideInInspector]
    // — the augment owns both the initial value and all tuning; nothing on this component's own prefab
    // should ever need hand-authoring it.
    [HideInInspector] public float dashCooldownRateBonus = 0f;

    private CharacterStatsPD _stats;
    private Collider _physicsCollider;
    // Fetched via GetComponent in Awake() — same convention as BWBlinkAbilityPD's own identical field. Used
    // by EndDash() to snap the visual cleanly to a TeleportSafetyPD-corrected position instead of letting the
    // normal correction-smoothing layer visibly slide/drift into it (see EndDash()'s own comment).
    private PredictedTransform _predictedTransform;
    // Public passthrough (2026-09-19) — TeleportSafetyPD's callers (BWBlinkAbilityPD, any future teleport
    // ability) read this instead of independently re-finding/duplicating the "PhysicsCollider" child lookup
    // this class already does in Awake(). Falls back to 0.5 if the collider is missing or isn't actually a
    // SphereCollider (every player prefab's own PhysicsCollider child is one today, per its own prefab setup,
    // but this stays defensive rather than assuming).
    public float PhysicsColliderRadius => _physicsCollider is SphereCollider sc ? sc.radius : 0.5f;
    // Class-unique ability components, if this character has any — resolved once in Awake() (same-
    // GameObject siblings, same as _stats), generic behind IAbilityPD so PlayerControllerPD never needs to
    // know which concrete class it's holding. A character can carry one of each (e.g. Rogue's
    // RogueSpinAbilityPD in the Ability1 slot, RogueVanishAbilityPD in the Ability2 slot) — both implement
    // the same IAbilityPD interface, so GetComponents + IAbilityPD.Slot is what routes each instance to the
    // right field below instead of an ambiguous single GetComponent<IAbilityPD>() guessing which one it found.
    private IAbilityPD _ability1;
    private IAbilityPD _ability2;

    // Distance is just an emergent property of speed and duration, not a separately-tuned input. Flat value,
    // not a MoveSpeed multiplier anymore — used to scale with the player's own current MoveSpeed (so a
    // MoveSpeed buff also sped up the dash), which stopped feeling right once MoveSpeed buffs got common;
    // 50 preserves how the dash already felt at baseline (no buffs) before this change.
    // DashSpeed retuned 30 -> 25 -> 20 (2026-09-30) — the 2026-09-22 drag-removal commit (0fc7b56) made
    // dashes travel their full undamped distance every tick instead of decaying via the Rigidbody's own
    // linear drag over the window, going noticeably farther than before at the old 30/0.35 values. A
    // from-first-principles equivalent-distance calc (treating the old drag=9 decay as continuous
    // exponential, matching this codebase's own ln(force/threshold)/drag knockback-stun math) landed around
    // ~9.1, but live testing said that felt way too slow — 20 is the user's own live-feel retune instead
    // (tried 25 first, still felt too far), duration left untouched.
    private const float DashDuration = 0.35f;
    private const float DashSpeed    = 20f;
    // Public passthrough (2026-10-04) — real-world distance a normal dash travels, exposed for anything
    // that wants to match dash's own range without duplicating these two private constants (e.g.
    // RogueVanishBlinkAugment's own Blink-range cap). Not itself augmentable by anything — DashSpeed/
    // DashDuration are fixed constants, not stats.
    public float DashRange => DashSpeed * DashDuration;

    // The player Rigidbody's own real linearDamping value outside of a dash/special-movement window — used
    // for TWO things now (2026-09-21, renamed from knockbackStunDrag once it took on this second role):
    // 1. Knockback() derives an approximate stun duration from the force it's given, same shape as
    //    EnemyBasePD.AddKnockbackVelocity's own formula (stunDuration = ln(force/threshold)/damping when force
    //    exceeds threshold, else 0) — this tunes that ESTIMATE, not to actually apply damping itself (that's
    //    the Rigidbody's own real linearDamping doing the real decay); its value is meant to always EXACTLY
    //    MATCH the Rigidbody's own configured Linear Damping (confirmed live, 2026-09-21: both are 9).
    // 2. The canonical value StartDash/StartSpecialMovement's own linearDamping-zeroing restores to once a
    //    dash/special-movement window ends (EndDash/EndSpecialMovement/the death path in Simulate()) — see
    //    StartDash's own comment for why that zeroing exists at all. Reused directly here rather than caching
    //    a second copy of the same number.
    // Private, not [SerializeField] (2026-09-21, was Inspector-editable) — per explicit design, this and the
    // two fields below are meant to be entirely script-controlled with no Inspector exposure, specifically so
    // this value can never accidentally drift out of sync with the Rigidbody's own real Linear Damping
    // setting the way an Inspector field could.
    private const float normalLinearDamping = 9f;
    private const float knockbackStunThreshold = 1f;

    // Per-instance, not static — each player's dash events are its own, not a shared global channel.
    // Surge events live on CharacterStatsPD instead, since StartSurge()/TryEndSurge() are defined there.
    private PredictedEvent _onDashStarted;
    // Fires only on a NATURAL dash end (timer ran out) — NOT on Interrupt(), see _onDashInterrupted below.
    // "Reward on completion" listeners (DashExplosionAugment/DashHealAugment) rely on this distinction: an
    // interrupted dash shouldn't still pay out its on-end effect. PhantomBladeOnDashAugment moved to
    // _onDashStarted instead (2026-09-14, was a dash-end listener).
    private PredictedEvent _onDashEnded;
    // Fires INSTEAD of _onDashEnded when Interrupt() cuts a dash short — for listeners that care specifically
    // whether the dash was cut short vs. completed naturally. Listeners that just need to know the movement
    // WINDOW closed (for whatever reason) want _onDashMovementEnded below instead.
    private PredictedEvent _onDashInterrupted;
    // Fired by Interrupt() only (never the natural/death end path) — see that method's own doc comment.
    private PredictedEvent _onSpecialMovementInterrupted;
    // Reward-agnostic movement-window pair — fires alongside Started/Ended (and, unlike Ended, also on an
    // interrupted close) for ANY dash-like movement (real dash, RogueSpinAbilityPD's leap, BWBlinkAbilityPD's
    // teleport). Exists separately from Started/Ended so a "trigger this dash's effects again" augment
    // (DashEffectDoubleTriggerAugment) can retrigger the REWARD pair without also re-opening a stateful
    // movement-window listener's window (DashThroughInterruptAugment's sweep — see its own doc comment).
    private PredictedEvent _onDashMovementStarted;
    // Carries whether this specific end was an interrupt (2026-10-10, replaces the old separate
    // _onRealDashStarted/_onRealDashEnded pair) — DashEffectDoubleTriggerAugment was the ONLY listener of
    // that pair anywhere in this codebase, and it only ever needed it to distinguish "a genuine real dash"
    // from its own retrigger AND to skip retriggering an interrupted close. Both are solvable off THIS event
    // directly: DashMovementStarted never had a self-loop risk in the first place (DoubleTrigger's own
    // retrigger never fires it, only the plain Started/Ended pair), and this payload lets DoubleTrigger skip
    // on interrupted == true instead of needing a whole separate narrower event to get that distinction.
    // Every other listener (CharacterStatsPD.OnDashMovementEnded, DashThroughInterruptAugment/
    // DashThroughHPScaledDamageAugment/PhantomBladeOnMovementAugment's own sweep-window closers) still just
    // wants to know the window closed regardless of why, so they simply ignore the new parameter.
    private PredictedEvent<bool> _onDashMovementEnded;
    // Fires once per charge actually gained — natural regen ticking over, the immediate top-up when
    // dashCharges' own max increases mid-run, RefundDashCharge (unless its own notify param suppresses it),
    // and each charge granted inside ReduceDashCooldown's overflow loop. DashChargeHighrollAugment is the
    // only listener (2026-10-14) — its own bonus-granted charge deliberately does NOT re-fire this (see its
    // own doc comment), so there's no chain risk despite listening to the same event it itself raises.
    private PredictedEvent _onDashChargeGained;

    public void AddDashChargeGainedListener(System.Action listener) => _onDashChargeGained.AddListener(listener);
    public void RemoveDashChargeGainedListener(System.Action listener) => _onDashChargeGained.RemoveListener(listener);

    public void AddDashStartedListener(System.Action listener) => _onDashStarted.AddListener(listener);
    public void RemoveDashStartedListener(System.Action listener) => _onDashStarted.RemoveListener(listener);
    public void AddDashEndedListener(System.Action listener) => _onDashEnded.AddListener(listener);
    public void RemoveDashEndedListener(System.Action listener) => _onDashEnded.RemoveListener(listener);
    public void AddDashInterruptedListener(System.Action listener) => _onDashInterrupted.AddListener(listener);
    public void RemoveDashInterruptedListener(System.Action listener) => _onDashInterrupted.RemoveListener(listener);
    public void AddSpecialMovementInterruptedListener(System.Action listener) => _onSpecialMovementInterrupted.AddListener(listener);
    public void RemoveSpecialMovementInterruptedListener(System.Action listener) => _onSpecialMovementInterrupted.RemoveListener(listener);
    public void AddDashMovementStartedListener(System.Action listener) => _onDashMovementStarted.AddListener(listener);
    public void RemoveDashMovementStartedListener(System.Action listener) => _onDashMovementStarted.RemoveListener(listener);
    public void AddDashMovementEndedListener(System.Action<bool> listener) => _onDashMovementEnded.AddListener(listener);
    public void RemoveDashMovementEndedListener(System.Action<bool> listener) => _onDashMovementEnded.RemoveListener(listener);

    // Lets an ability that functions like a dash (e.g. RogueSpinAbilityPD's leap) hook into the same
    // OnDashStarted/OnDashEnded listeners a real dash already fires (DashExplosionAugment, DashHealAugment,
    // etc.), without StartSpecialMovement itself needing to know or assume anything about dash semantics —
    // it stays fully generic (see its own doc comment), so a future non-dash-like special movement can still
    // use it without silently also counting as a dash. The caller decides, by calling these explicitly
    // alongside its own Activate()/end-of-active logic.
    public void NotifyDashStarted() => _onDashStarted.Invoke();
    public void NotifyDashEnded() => _onDashEnded.Invoke();
    // Same "caller decides" contract as NotifyDashStarted/NotifyDashEnded above — every call site that fires
    // those for an ability-driven movement (RogueSpinAbilityPD, BWBlinkAbilityPD) fires these alongside them,
    // matching what the real dash's own StartDash/EndDash do automatically (see their own call sites).
    public void NotifyDashMovementStarted() => _onDashMovementStarted.Invoke();
    // interrupted: see _onDashMovementEnded's own field comment. Every ability-driven teleport (BWBlink/
    // RogueVanish) passes false — nothing mid-flight to interrupt, it's instant. The death-forced-halt path
    // passes true (2026-10-10, explicit design) — not a real Interrupt() call, but it behaves like one for
    // this purpose, and DoubleTrigger shouldn't retrigger a dash's reward off a caster that's already dead.
    public void NotifyDashMovementEnded(bool interrupted) => _onDashMovementEnded.Invoke(interrupted);

    // Grants back one dash charge, clamped to the character's own current max — e.g.
    // DashThroughInterruptAugment refunding a charge on a successful interrupt. Doesn't touch
    // chargeRegenTimer — the existing passive regen (see Simulate()'s own regen block) keeps counting toward
    // whatever charge it was already counting toward; this only adds one on top, up to the cap.
    // notifyChargeGained defaults true (every other caller wants the normal "a charge was gained" signal);
    // DashChargeHighrollAugment's own bonus grant passes false so its payout doesn't re-trigger its own
    // listener — see that augment's own doc comment for the no-chaining design.
    public void RefundDashCharge(bool notifyChargeGained = true)
    {
        if (_stats == null) return;
        if (currentState.currentCharges >= _stats.dashCharges) return;
        currentState.currentCharges = Mathf.Min(currentState.currentCharges + 1, _stats.dashCharges);
        if (notifyChargeGained) _onDashChargeGained.Invoke();
    }

    // Shaves time off the currently-regenerating dash charge's chargeRegenTimer — e.g. an on-event
    // dash-cooldown-reduction augment (the last four of this shape were removed 2026-10-14, infinite-dash
    // balance pass — this method itself is kept as a reusable primitive for a future, better-balanced one).
    // Unlike RefundDashCharge above (a flat +1, no timer interaction),
    // this can OVERFLOW: if `seconds` exceeds what's left on the current charge's timer, that charge is
    // granted immediately and the remainder carries over to reduce the NEXT charge's own timer (freshly set
    // to DashCooldown, same value Simulate()'s own regen tick resets it to) — repeating until `seconds` is
    // exhausted or every charge is already full. Bounded by _stats.dashCharges (always small), so this
    // terminates even if DashCooldown is ever 0 or negative (each iteration still grants a charge and moves
    // the loop closer to the currentCharges == dashCharges exit condition, regardless of how much `seconds`
    // itself decreases that iteration). A no-op whenever already at max charges — matches every other
    // dash-charge mutator's own "harmless when there's nothing to do" convention.
    public void ReduceDashCooldown(float seconds)
    {
        if (_stats == null || seconds <= 0f) return;

        while (seconds > 0f && currentState.currentCharges < _stats.dashCharges)
        {
            if (currentState.chargeRegenTimer <= 0f) currentState.chargeRegenTimer = _stats.DashCooldown;

            if (seconds < currentState.chargeRegenTimer)
            {
                currentState.chargeRegenTimer -= seconds;
                seconds = 0f;
            }
            else
            {
                seconds -= currentState.chargeRegenTimer;
                currentState.currentCharges++;
                currentState.chargeRegenTimer = currentState.currentCharges < _stats.dashCharges ? _stats.DashCooldown : 0f;
                _onDashChargeGained.Invoke();
            }
        }
    }

    // State-backed (2026-09-22 fix — moved off a plain field). Set once, permanently, by
    // ConstantMovementAugment.OnApply() (called from AugmentEffectPD.UpdateView(), off verified state, same
    // safe cross-identity currentState-write convention CharacterStatsPD.ApplyProgressionBonuses/
    // ApplyStatAugmentPick already use). This used to cite baseMoveSpeed as its precedent for why a plain
    // field was safe here — that precedent turned out to be wrong (see CharacterStatsPD.ApplyProgressionBonuses'
    // own doc comment): PlayerControllerPD is a sibling component on the same player character GameObject as
    // CharacterStatsPD, spawned via the same hierarchy.Create() call, so it's equally exposed to a
    // PredictedHierarchy reconciliation resetting a plain field back to its C# default with nothing left to
    // re-grant it (ConstantMovementAugment's own OnApply() only ever fires once, ever, for a given pick).
    // Forces the Movement block below to fall back to state.lastMoveDir whenever there's no current input,
    // instead of standing still — "you can't stand still."
    public void SetForceMovement(bool force) => currentState.forceMovement = force;

    private bool _wasDeadLastFrame;
    private bool _pendingReadyPressed;
    private bool _pendingPausePressed;
    private bool _pendingPickPressed;
    private int  _pendingPickIndex;
    private float _pendingPickLuckMultiplierA = 1f;
    private float _pendingPickLuckMultiplierB = 1f;
    private bool _pendingEndScreenReadyPressed;
    private bool _pendingRerollPressed;
    private InputSystem_Actions _input;

    // Predicted, not verified — this only drives the local owner's own button label, and instant
    // feedback on your own click matters more than the extra tick of latency verifiedState would add.
    public bool IsReady => currentState.isReady;
    public bool IsDashing => currentState.isDashing;
    // Exposed (2026-09-03) so CharacterStatsPD.IFriendlyPD.IsDashingOrLeaping can OR this in alongside
    // IsDashing itself — enemy dash-tracking (EnemyBasePD.HandleOffsetChase) needs to freeze on a player
    // during RogueSpin's leap the same as it does during a real dash, since both are equally "moving too
    // fast to chase live" from an enemy's perspective, even though they're deliberately separate flags
    // here (see this class's own isDashing doc comment for why a real dash never sets isSpecialMoving).
    public bool IsSpecialMoving => currentState.isSpecialMoving;
    // True while either ability slot is currently forcing a special action on this player — isSpecialMoving,
    // or an ability whose IAbilityPD.ForcesSpecialAction is true and IsActive (e.g. RogueSpinAbilityPD's
    // leap). A non-locking ability (ForcesSpecialAction false, e.g. RogueVanishAbilityPD — it only flips
    // invisibility/movespeed, never touches movement) does NOT set this while it's the one active.
    //
    // Deliberately does NOT include isDashing/currentState.isDashing (unlike its old formula) — dashing no
    // longer hard-locks abilities the way a true lockout does. It follows the same lenient rule as a
    // Berserk-Surge-style forced Surge instead: only IAbilityPD.IsActionAbility abilities get blocked while
    // dashing, buff abilities stay castable through it. See Simulate()'s own dash/ability gating block for
    // the actual (isDashing || IsForcedSurging) && IsActionAbility formula — _ability1/_ability2's own UI
    // (LocalUIManagerPD/PlayerUIWorldSpace) reads IsDashing directly alongside IsLockedOut/IsForcedSurging to
    // mirror that same formula for the on-cooldown BG.
    public bool IsLockedOut => currentState.isSpecialMoving || currentState.stunTimer > 0f
        || (_ability1 != null && _ability1.IsActive && _ability1.ForcesSpecialAction)
        || (_ability2 != null && _ability2.IsActive && _ability2.ForcesSpecialAction);
    // Mirrors EnemyBasePD.IsStunned/IsTrueStunned — see State.stunTimer/trueStunTimer's own doc comments for
    // the distinction.
    public bool IsStunned => currentState.stunTimer > 0f;
    public bool IsTrueStunned => currentState.trueStunTimer > 0f;

    // ── Structs ────────────────────────────────────────────────────────────
    public struct Input : IPredictedData
    {
        public Vector2 direction;
        public bool dashPressed;
        public bool ability1Pressed;
        public bool ability2Pressed;
        public bool surgeHeld;
        public bool pausePressed;
        public bool readyPressed;
        public bool pickPressed;
        public int pickedCardIndex;
        // Reroll the whole set of presented augment options at the cost of one CharacterStatsPD.RerollTokens
        // — same buffer-and-drain edge-triggered shape as pickPressed above, see RequestReroll's own doc
        // comment.
        public bool rerollPressed;
        public bool endScreenReadyPressed;
        // The picking player's own meta-progression multiplier — captured here (owning client only,
        // via GetFinalInput) rather than recomputed by AugmentManagerPD, since a remote machine's own
        // local ProgressionManager save data would give a different (wrong) answer for this player's pick.
        public float pickMultiplier;
        // LuckRolledLevelUpStatsAugment's per-stat roll for THIS pick — unlike pickMultiplier, this is
        // genuinely random and can't be safely recomputed every tick, so it's captured once (at card-display
        // time, by AugmentCardUI, via a local UnityEngine.Random roll — never GameRandom) and buffered
        // through RequestPick alongside pickedCardIndex, inside the same _pendingPickPressed block, rather
        // than recomputed fresh in GetFinalInput the way pickMultiplier is.
        public float pickLuckMultiplierA;
        public float pickLuckMultiplierB;
        // Direction from this player's own position toward wherever the mouse cursor currently points,
        // projected onto the ground plane — see ComputeCursorWorldDir's own doc comment. Captured here
        // (owning client only, via GetFinalInput) same as direction/surgeHeld above, so every machine
        // resimulates identically off the one captured value rather than each reading its own local mouse
        // state. Vector3.zero when nothing valid to aim at (no camera, cursor off any ground-plane hit).
        public Vector3 cursorWorldDir;
        // Ground-plane distance from this player to the raw cursor hit point (before cursorWorldDir gets
        // normalized) — 0 whenever cursorWorldDir is Vector3.zero (nothing valid to aim at that tick).
        // Captured alongside cursorWorldDir for the same reason (owning client only, via GetFinalInput) —
        // an ability that casts AT a target point (e.g. ArcherArrowRainAbilityPD), not just toward a
        // direction, needs the actual distance to reconstruct that point, clamped to its own max range.
        public float cursorDistance;
        public void Dispose() { }
    }

    public struct State : IPredictedData<State>
    {
        public Vector3 lastMoveDir;
        // Direction from this player toward the mouse cursor, refreshed every tick from input.cursorWorldDir
        // whenever that's non-zero (so a momentarily-invalid raycast just holds the last good direction
        // instead of snapping to zero) — abilities that want to aim toward the cursor instead of the held
        // movement direction (e.g. RogueSpinAbilityPD's leap) read this the same way they'd read lastMoveDir.
        public Vector3 cursorDir;
        // Refreshed in lockstep with cursorDir below (see its own doc comment for the "hold last good
        // value" reasoning) — the ground-plane distance to wherever the cursor is currently pointing,
        // unclamped by any particular ability's own max range (each ability clamps this itself against its
        // own cast range when reconstructing a target point).
        public float cursorDistance;
        public bool isDashing;
        public float dashTimer;
        public Vector3 dashVelocity;
        // Captured in StartDash(), read back in EndDash() — see that method's own doc comment. The one
        // position in the whole dash window guaranteed clear of obstacles, since the player was already
        // standing there before the dash began; used as TeleportSafetyPD's total-failure fallback.
        public Vector3 dashStartPosition;
        // Same shape as isDashing/dashTimer/dashVelocity above, driven externally by StartSpecialMovement()
        // (called from an ability component's own Activate(), e.g. RogueSpinAbilityPD) rather than input
        // read directly here — PlayerControllerPD doesn't know or care which ability is running, just that
        // something asked for movement.
        public bool isSpecialMoving;
        public float specialMoveTimer;
        public Vector3 specialVelocity;
        // Same shape/purpose as dashStartPosition above, just for the special-movement window instead —
        // captured in StartSpecialMovement(), read back in EndSpecialMovement()'s own TeleportSafetyPD
        // resolution (2026-09-19, added once RogueSpinAbilityPD's leap started phasing through Obstacles the
        // same layer-collision change that let real dashes do the same — see ResolveTeleportSafeLanding's own
        // doc comment).
        public Vector3 specialMoveStartPosition;
        // Set by Stun()/Knockback() — while > 0, blocks starting a dash/ability (see lockedOut/IsLockedOut)
        // and suppresses the normal movement AddForce (see Simulate()'s own Movement section), letting
        // whatever velocity Knockback() applied decay on its own via the Rigidbody's own linear drag instead
        // of fighting player input. Never touches isDashing/isSpecialMoving itself — Knockback() calls
        // Interrupt() first to end those cleanly before applying its own velocity.
        public float stunTimer;
        // Separate from stunTimer above — stunTimer is the shared BEHAVIORAL gate (set by both a real Stun()
        // call and Knockback()'s own derived micro-stun), trueStunTimer is set ONLY by a real Stun() call.
        // Same distinction as EnemyBaseState.trueStunTimer (see its own doc comment) — kept here purely as a
        // futureproofing hook (e.g. a future stun visual, or a "genuinely stunned vs just staggered by a hit"
        // check) even though nothing currently reads it. Always <= stunTimer by construction.
        public float trueStunTimer;
        public int currentCharges;
        public float chargeRegenTimer;
        public float orbSweepTimer;
        public bool isReady;
        // State-backed (2026-09-22 fix — moved off a plain field mutated directly inside Simulate(), the
        // exact "plain field with a real runtime mutator" bug class this whole session has been hunting:
        // a plain field doesn't roll back on resimulation, so the speculative pass of a tick that detects
        // _stats.dashCharges changing would permanently consume the detection (writing the new value into
        // the plain field), and a later corrected replay of that SAME tick would then see no change at all
        // (the plain field already matches), silently skipping the state.currentCharges top-up that tick was
        // actually supposed to grant. See Simulate()'s own "Grant the extra charge immediately" comment.
        public int lastKnownMaxCharges;
        // State-backed (2026-09-22 fix) — see SetForceMovement's own doc comment for the full reasoning.
        public bool forceMovement;
        public void Dispose() { }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
        foreach (var a in GetComponents<IAbilityPD>())
        {
            switch (a.Slot)
            {
                case AbilitySlot.Ability1: _ability1 = a; break;
                case AbilitySlot.Ability2: _ability2 = a; break;
            }
        }
        var physicsChild = transform.Find("PhysicsCollider");
        if (physicsChild != null) _physicsCollider = physicsChild.GetComponent<Collider>();
        if (_rigidbody != null) _predictedTransform = _rigidbody.GetComponent<PredictedTransform>();
        _orbLayer = LayerMask.GetMask("XPOrb");
    }

    protected override State GetInitialState() => new()
    {
        lastMoveDir  = Vector3.forward,
        cursorDir    = Vector3.forward,
        currentCharges = _stats != null ? _stats.dashCharges : 1,
        lastKnownMaxCharges = _stats != null ? _stats.dashCharges : 1,
    };

    public Transform CameraTarget => _cameraTarget != null ? _cameraTarget : transform;

    protected override void LateAwake()
    {
        _onDashStarted = new PredictedEvent(predictionManager, this);
        _onDashEnded   = new PredictedEvent(predictionManager, this);
        _onDashInterrupted = new PredictedEvent(predictionManager, this);
        _onSpecialMovementInterrupted = new PredictedEvent(predictionManager, this);
        _onDashMovementStarted = new PredictedEvent(predictionManager, this);
        _onDashMovementEnded   = new PredictedEvent<bool>(predictionManager, this);
        _onDashChargeGained    = new PredictedEvent(predictionManager, this);

        if (!isOwner) return;
        _input = new InputSystem_Actions();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        _input?.Disable();
        _input?.Dispose();
    }

    public void EnableInput() => _input?.Enable();

    // Called from the UI button's OnClick. Only buffers a pending flag — the actual toggle happens
    // deterministically in Simulate() once this is drained into Input, same as every other action here.
    // Never mutate state directly from a UI callback: that only happens on the clicking machine and the
    // server never finds out, so nothing gets replicated (this was the multiplayer ready-up bug).
    public void RequestReadyToggle() => _pendingReadyPressed = true;

    // Resume (single-player) is just "press Pause again while already paused" from the UI instead of
    // the keyboard — Simulate()'s existing pausePressed branch already handles that case correctly.
    public void RequestPausePressed() => _pendingPausePressed = true;

    // Called from the level-up card UI's OnClick. Same buffer-and-drain pattern as ready/pause.
    public void RequestPick(int cardIndex, float luckMultiplierA = 1f, float luckMultiplierB = 1f)
    {
        _pendingPickPressed = true;
        _pendingPickIndex = cardIndex;
        _pendingPickLuckMultiplierA = luckMultiplierA;
        _pendingPickLuckMultiplierB = luckMultiplierB;
    }

    // Called from whatever reroll UI ends up wired up (button, hotkey, etc.). Same buffer-and-drain
    // edge-triggered pattern as ready/pause/pick above — the actual validation (isPresenting, real-augment
    // round, not already picked, RerollTokens > 0) and the token spend + option regeneration all happen
    // deterministically in Simulate() via AugmentManagerPD.RequestReroll, never here.
    public void RequestReroll() => _pendingRerollPressed = true;

    // Called from the win/game-over screen's ready button. Same buffer-and-drain pattern as ready/pause.
    public void RequestEndScreenReady() => _pendingEndScreenReadyPressed = true;

    // ── Input ──────────────────────────────────────────────────────────────
    protected override void UpdateInput(ref Input input)
    {
        if (_input != null)
        {
            input.dashPressed  |= _input.Player.Dash.WasPressedThisFrame();
            input.ability1Pressed |= _input.Player.Ability1.WasPressedThisFrame();
            input.ability2Pressed |= _input.Player.Ability2.WasPressedThisFrame();

            // If the in-game Settings panel is open, Escape closes ONLY that (a purely local UI action, same
            // "never mutate state directly from a UI callback" reasoning as RequestPausePressed/
            // RequestReadyToggle above) and is entirely consumed here — it never also reaches
            // input.pausePressed below. Without this, a single Escape while paused with Settings open would
            // both close Settings AND (via the same press reaching Simulate()'s pause/ready-toggle branch)
            // unpause the game — or submit an unwanted ready-toggle in multiplayer — leaving Settings
            // visibly stuck open over top of whatever's underneath.
            bool pausePressedThisFrame = _input.Player.Pause.WasPressedThisFrame();
            if (pausePressedThisFrame && LocalUIManagerPD.instance != null && LocalUIManagerPD.instance.IsSettingsPanelOpen)
                LocalUIManagerPD.instance.OnSettingsBackClicked();
            else
                input.pausePressed |= pausePressedThisFrame;

            // Purely local UI toggle, not part of the networked/predicted Input struct — matches old
            // PlayerController's identical direct (un-networked) LocalUIManager calls, since this only
            // ever affects this machine's own UI display, never simulation state. Holding Tab shows the
            // stats panel (inactive by default — statsUIExpanded retired 2026-08-21, this is now the only
            // stats panel) and the augment list; releasing hides both.
            if (_input.Player.Tab.WasPressedThisFrame())
            {
                LocalUIManagerPD.instance?.SetStatsVisible(true);
                LocalUIManagerPD.instance?.SetAugmentListVisible(true);
            }
            else if (_input.Player.Tab.WasReleasedThisFrame())
            {
                LocalUIManagerPD.instance?.SetStatsVisible(false);
                LocalUIManagerPD.instance?.SetAugmentListVisible(false);
            }
        }

        input.readyPressed |= _pendingReadyPressed;
        _pendingReadyPressed = false;

        input.pausePressed |= _pendingPausePressed;
        _pendingPausePressed = false;

        if (_pendingPickPressed)
        {
            input.pickPressed = true;
            input.pickedCardIndex = _pendingPickIndex;
            input.pickLuckMultiplierA = _pendingPickLuckMultiplierA;
            input.pickLuckMultiplierB = _pendingPickLuckMultiplierB;
        }
        _pendingPickPressed = false;

        input.endScreenReadyPressed |= _pendingEndScreenReadyPressed;
        _pendingEndScreenReadyPressed = false;

        input.rerollPressed |= _pendingRerollPressed;
        _pendingRerollPressed = false;
    }

    protected override void GetFinalInput(ref Input input)
    {
        input.pickMultiplier = ProgressionManager.GetStatMultiplier(StatType.LevelUpStats);
        if (_input == null) return;
        input.direction = _input.Player.Move.ReadValue<Vector2>();
        input.surgeHeld = _input.Player.Surge.IsPressed();
        input.cursorWorldDir = ComputeCursorWorldDir(out input.cursorDistance);
    }

    // Owner-only — direction from this player's own current position toward wherever the mouse cursor
    // currently points, projected onto the ground plane at the player's own height. Reads the Player/
    // LookDirection action (Vector2, bound to Mouse/position) rather than the OS mouse device directly, so
    // every input source in this project goes through the InputSystem_Actions asset the same way. Vector3.
    // zero (a valid "nothing new to report" sentinel — State.cursorDir only overwrites on a non-zero value)
    // whenever there's no camera or the ray doesn't cross the ground plane at all (camera looking straight
    // along it, effectively never in practice).
    private Vector3 ComputeCursorWorldDir(out float worldDistance)
    {
        worldDistance = 0f;
        if (Camera.main == null) return Vector3.zero;
        Ray ray = Camera.main.ScreenPointToRay(_input.Player.LookDirection.ReadValue<Vector2>());
        Plane groundPlane = new(Vector3.up, transform.position);
        if (!groundPlane.Raycast(ray, out float distance)) return Vector3.zero;
        Vector3 dir = ray.GetPoint(distance) - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude <= 0.0001f) return Vector3.zero;
        worldDistance = dir.magnitude;
        return dir.normalized;
    }

    // One-shot, edge-triggered "just pressed this tick" flags must not persist when the same real input
    // gets copied forward across several extrapolated ticks (missing/delayed real data) — left uncleared,
    // every extrapolated tick would re-see e.g. dashPressed=true and could re-trigger that action once
    // per extrapolated tick from a single real button press. Don't rely on an incidental state guard
    // elsewhere (like dash's !state.isDashing) to paper over this — that only holds as long as the
    // guarded state outlasts the extrapolation gap, which isn't guaranteed. direction/surgeHeld are
    // deliberately left alone: those reflect ongoing held state, so repeating the last known value is
    // the correct guess while waiting for fresher real input, not something to clear.
    protected override void ModifyExtrapolatedInput(ref Input input)
    {
        input.dashPressed = false;
        input.ability1Pressed = false;
        input.ability2Pressed = false;
        input.pausePressed = false;
        input.readyPressed = false;
        input.pickPressed = false;
        input.endScreenReadyPressed = false;
        input.rerollPressed = false;
    }

    // ── Simulation ─────────────────────────────────────────────────────────
    protected override void Simulate(Input input, ref State state, float delta)
    {
        // LoadingScreenRevealed, not GameStarted (2026-08-18) — see its own doc comment on GameManagerPD.
        // Movement/ability/dash input used to start processing the instant GameStarted flipped true, well
        // before the loading screen actually cleared — harmless on its own (nobody could see it happening),
        // but kept in lockstep with everything else that gates on this now for consistency.
        if (GameManagerPD.instance == null || !GameManagerPD.instance.LoadingScreenRevealed) return;

        bool isSinglePlayer = predictionManager == null || predictionManager.players == null || predictionManager.players.players.Count <= 1;
        bool isPresenting = AugmentManagerPD.instance != null && AugmentManagerPD.instance.IsPresenting;

        // Pause/ready-up shouldn't fire while a level-up card screen is up — don't let it open the
        // pause menu on top of that screen.
        if (!isPresenting)
        {
            if (input.pausePressed && PausePD.instance != null)
            {
                if (PausePD.instance.IsPaused && !isSinglePlayer && owner.HasValue)
                    ToggleReadyState(ref state);
                else
                    PausePD.instance.RequestToggle();
            }
            else if (input.readyPressed && PausePD.instance != null && PausePD.instance.IsPaused && !isSinglePlayer && owner.HasValue)
            {
                ToggleReadyState(ref state);
            }
        }

        if (input.pickPressed && AugmentManagerPD.instance != null && owner.HasValue)
            AugmentManagerPD.instance.SubmitPick(owner.Value, input.pickedCardIndex, input.pickMultiplier, input.pickLuckMultiplierA, input.pickLuckMultiplierB);

        if (input.rerollPressed && AugmentManagerPD.instance != null && owner.HasValue)
            AugmentManagerPD.instance.RequestReroll(owner.Value);

        // Not gated by isPresenting/IsPaused — the end screen only ever shows once the game has already
        // won/ended, and readying up from it should work regardless of whatever pause state happens to be
        // latched at that point.
        if (input.endScreenReadyPressed && GameManagerPD.instance != null
            && (GameManagerPD.instance.HasWon || GameManagerPD.instance.IsGameOver) && owner.HasValue)
            GameManagerPD.instance.SetEndScreenReady(owner.Value);

        // Presenting timer expired and this player still hasn't picked — AutoPick re-checks both of
        // those itself, so it's safe to just keep calling this every tick rather than needing a separate
        // one-shot guard here.
        if (AugmentManagerPD.instance != null && AugmentManagerPD.instance.HasPresentingTimerExpired && owner.HasValue)
            AugmentManagerPD.instance.AutoPick(owner.Value, input.pickMultiplier);

        if (PausePD.instance != null && PausePD.instance.IsPaused)
        {
            // DoPhysicsPass() isn't gated by IsPaused — it keeps integrating whatever velocity the
            // Rigidbody already has every tick regardless of the visual pause. Without this, mid-dash
            // residual velocity kept sliding the player from momentum while "paused," which the weapon's
            // frozen orbit (correctly locked to the player's position at the instant pause hit) didn't
            // track, showing up as a growing gap. isDashing/dashTimer stay untouched here on purpose —
            // Simulate() just never reaches the movement code while paused, so the dash resumes exactly
            // where it left off (still isDashing, same dashVelocity re-applied) once unpaused.
            if (_rigidbody != null)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            return;
        }

        state.isReady = false;

        // Grant the extra charge immediately when max charges increases, instead of only refilling via regen over time.
        if (_stats != null && _stats.dashCharges != state.lastKnownMaxCharges)
        {
            // Fire once per charge actually gained here (a single mid-run stat jump can grant more than
            // one at once), same per-charge semantic every other _onDashChargeGained call site uses — see
            // that field's own doc comment. A max DECREASE (negative delta) correctly fires zero times.
            int gained = _stats.dashCharges - state.lastKnownMaxCharges;
            state.currentCharges += gained;
            state.lastKnownMaxCharges = _stats.dashCharges;
            for (int i = 0; i < gained; i++) _onDashChargeGained.Invoke();
        }

        // Charge regen runs even while dead so the player has dashes on revive. Paused while actually
        // isDashing (2026-08-19) — a real dash only, NOT isSpecialMoving (RogueSpinAbilityPD's leap and
        // other ability-driven movement use that flag instead, see its own doc comment, and should keep
        // ticking normally). Plays nice with Interrupt() for free: EndDash(interrupted: true) sets isDashing
        // false immediately just like a natural end does, so regen resumes from wherever it was frozen
        // either way, no separate handling needed. ReduceDashCooldown() (other effects shaving time off the
        // current charge) is untouched by this gate — it mutates chargeRegenTimer directly, independent of
        // this natural per-tick decrement, so it still applies normally even mid-dash.
        if (_stats != null && state.currentCharges < _stats.dashCharges && !state.isDashing)
        {
            // Surge dash-cooldown haste (2026-10-12) — 0f/no-op until SurgeDashCDRateAugment sets
            // dashCooldownRateBonus — see that field's own doc comment.
            float dashCooldownRate = 1f;
            if (dashCooldownRateBonus != 0f && _stats.IsSurging)
                dashCooldownRate += dashCooldownRateBonus;

            state.chargeRegenTimer -= delta * dashCooldownRate;
            if (state.chargeRegenTimer <= 0f)
            {
                state.currentCharges++;
                state.chargeRegenTimer = state.currentCharges < _stats.dashCharges ? _stats.DashCooldown : 0f;
                _onDashChargeGained.Invoke();
            }
        }

        if (_stats == null || _stats.IsDead)
        {
            // Halt whatever was in progress (e.g. a mid-dash death) so the corpse doesn't keep sliding.
            // Fires _onDashMovementEnded alongside _onDashEnded (2026-08-31 fix) — every _onDashMovementStarted
            // needs a matching Ended or a movement-window listener (DashThroughInterruptAugment's own sweep)
            // gets stuck open forever; this path used to only fire the reward pair, silently skipping the
            // movement pair since it doesn't route through EndDash().
            //
            // ResolveTeleportSafeLanding calls added 2026-09-19 — dying mid-dash/mid-leap is a THIRD way
            // (alongside a natural end and an Interrupt()) this window can close, and since dashes/leaps can
            // now phase through Obstacles, a death landing inside one would otherwise leave the corpse stuck
            // there forever (this path deliberately bypasses EndDash()/EndSpecialMovement() themselves to
            // preserve its own distinct, already-correct event-firing shape below, so the position fix has to
            // be called directly here too rather than picked up for free). Called BEFORE the velocity zeroing
            // right after, matching EndDash/EndSpecialMovement's own "resolve position first, everything else
            // after" ordering — per explicit design, the position correction must land before any paired
            // effect (e.g. a lethal hit's own Knockback(), which calls Interrupt() — and therefore this exact
            // dash/special-movement end — before applying its own impulse) rather than after.
            // linearDamping restored here too — this early-return skips the Movement section entirely (where
            // that's normally reasserted every tick, see its own comment there for why it has to be per-tick
            // rather than a one-shot StartDash/EndDash toggle), so this needs its own copy of the same
            // state-driven reassertion. Still safe for the identical reason: driven by state.isDashing/
            // isSpecialMoving (properly rolled-back predicted State), not "did some other method happen to
            // run," and this block itself runs every tick the player is dead, not just once on the transition
            // into death — so it self-corrects on any resimulation the same way the Movement section's own
            // copy does.
            if ((state.isDashing || state.isSpecialMoving) && _rigidbody != null)
                _rigidbody.rb.linearDamping = normalLinearDamping;

            if (state.isDashing)
            {
                ResolveTeleportSafeLanding(state.dashStartPosition, state.dashVelocity);
                state.isDashing = false;
                _onDashEnded.Invoke();
                // true (2026-10-10, explicit design) — not a real Interrupt() call, but a dash cut short by
                // the caster's own death behaves like one for DoubleTrigger's purposes: it shouldn't schedule
                // a retrigger off a corpse's dash. See _onDashMovementEnded's own field comment.
                _onDashMovementEnded.Invoke(true);
            }
            if (state.isSpecialMoving)
                ResolveTeleportSafeLanding(state.specialMoveStartPosition, state.specialVelocity);
            state.isSpecialMoving = false;
            if (_rigidbody != null)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            return;
        }

        // Refreshed before ability input below so Activate() (e.g. RogueSpinAbilityPD reading
        // currentState.cursorDir) sees this tick's fresh direction, not a stale one from before this same
        // tick updated it. Only overwrites on a non-zero value (see ComputeCursorWorldDir's own doc comment)
        // so a momentarily-invalid raycast just holds the last good direction instead of snapping to zero.
        if (input.cursorWorldDir != Vector3.zero)
        {
            state.cursorDir = input.cursorWorldDir;
            state.cursorDistance = input.cursorDistance;
        }

        // "Locked out" — a HARD lock: isSpecialMoving or either ability slot are the only things left that
        // can force a special action on this player, disrupting normal movement, and only ever one should
        // run at a time. isSpecialMoving covers any special-movement window (belt-and-suspenders alongside
        // the IAbilityPD check below — today only RogueSpinAbilityPD ever sets isSpecialMoving, always in
        // lockstep with ForcesSpecialAction, but this stays correct even if that ever changes). A LOCKING
        // ability (ForcesSpecialAction true, e.g. RogueSpinAbilityPD) being active locks everything else out
        // — dash, the other slot, and itself. A NON-locking one (ForcesSpecialAction false, e.g.
        // RogueVanishAbilityPD — it only flips invisibility/movespeed, never touches movement) does NOT set
        // this while it's the one active, so it never blocks anything else. Same formula as the public
        // IsLockedOut property (that one reads currentState instead of this tick's own `state` ref, otherwise
        // identical) — see its own doc comment for the exact rule spec.
        //
        // isDashing is deliberately NOT part of this hard lock — every ability is castable while dashing now
        // (2026-08-31 rework, see class doc history) — a ForcesSpecialAction ability activated mid-dash
        // instead cleanly ends the dash first (see the ability-activation block below), rather than being
        // blocked by it.
        bool lockedOut = state.isSpecialMoving || state.stunTimer > 0f
            || (_ability1 != null && _ability1.IsActive && _ability1.ForcesSpecialAction)
            || (_ability2 != null && _ability2.IsActive && _ability2.ForcesSpecialAction);

        // Starting a new dash still requires NOT already dashing (state.isDashing is no longer folded into
        // lockedOut above, so this needs its own explicit check) and not hard-locked-out.
        if (input.dashPressed && state.currentCharges > 0 && !lockedOut && !state.isDashing)
            StartDash(ref state);

        // Ability1 input — PlayerControllerPD only gates/forwards; the ability component itself owns its
        // own cooldown state and decides what Activate()/NotifyActivationBlocked() actually do.
        //
        // Blocked only while a Berserk-Surge-style forced Surge (CharacterStatsPD.IsForcedSurging) is active
        // AND this ability's own ActiveSurgeBlock is true (2026-08-31, replaces the old IsActionAbility check
        // — that flag is retired entirely now that dash no longer blocks any ability and Surge-blocking is
        // driven by the same flag abilities already use to block Surge in the other direction). Every other
        // ability stays castable through a forced Surge. See CharacterStatsPD.StartBerserkSurge's own
        // CancelSurgeBlockingAbility call for the reverse direction — Surge activating cancels whichever
        // ActiveSurgeBlock ability is currently active, rather than merely blocking new casts.
        if (input.ability1Pressed && _ability1 != null)
        {
            bool actionBlocked1 = _stats.IsForcedSurging && _ability1.ActiveSurgeBlock;
            bool blocked1 = lockedOut || actionBlocked1;
            if (_ability1.IsReady && !blocked1)
            {
                // A ForcesSpecialAction ability (RogueSpin/BWBlink) activated mid-dash cleanly ends the dash
                // first — CLEAN end (default interrupted: false), deliberately never Interrupt()/
                // interrupted: true: this is the player choosing to act, not being interrupted by anything
                // external, so dash-completion-reward augments (DashExplosionAugment/DashHealAugment) still
                // fire normally, and the Interrupted status popup must never show for a player's own choice.
                if (state.isDashing && _ability1.ForcesSpecialAction) EndDash(ref state);
                _ability1.Activate();
            }
            else _ability1.NotifyActivationBlocked();
        }

        // Ability2 input — same gate/forward split as Ability1 above.
        if (input.ability2Pressed && _ability2 != null)
        {
            bool actionBlocked2 = _stats.IsForcedSurging && _ability2.ActiveSurgeBlock;
            bool blocked2 = lockedOut || actionBlocked2;
            if (_ability2.IsReady && !blocked2)
            {
                if (state.isDashing && _ability2.ForcesSpecialAction) EndDash(ref state);
                _ability2.Activate();
            }
            else _ability2.NotifyActivationBlocked();
        }

        // Dash timer — deferred-by-one-tick end (2026-09-21 fix, confirmed live via ResolveSafeTravelVelocity's
        // own diagnostic logging): ending the INSTANT dashTimer first dropped to <= 0 meant EndDash() ran (and
        // zeroed the rigidbody's velocity) in the SAME tick the Movement section below was about to apply
        // THIS tick's own dashVelocity — since velocity is a plain value integrated once per tick using
        // whatever it's set to by the end of Simulate(), EndDash's own zero-out was always the last write,
        // silently dropping that entire tick's worth of movement. Every dash was landing short of its own
        // intended distance by almost exactly one tick, invisible before only because the old post-hoc
        // TeleportSafetyPD correction was generous enough to paper over it. Fix: let the tick where the timer
        // FIRST goes non-positive still count as a full dashing tick (isDashing stays true, Movement below
        // still applies dashVelocity normally) — only call EndDash() at the START of the NEXT tick, once the
        // timer is ALREADY <= 0 from a previous tick's own decrement. Errs toward a small overshoot (at most
        // one tick's worth of extra travel) rather than the old systematic shortfall — a much safer direction
        // for TeleportSafetyPD's own step-size/margin tolerances to absorb.
        if (state.isDashing)
        {
            if (state.dashTimer <= 0f)
                EndDash(ref state);
            else
                state.dashTimer -= delta;
        }

        // Special movement timer — same shape/same fix as the dash timer above.
        if (state.isSpecialMoving)
        {
            if (state.specialMoveTimer <= 0f)
                EndSpecialMovement(ref state);
            else
                state.specialMoveTimer -= delta;
        }

        // Stun timer — see State.stunTimer/trueStunTimer's own doc comments. trueStunTimer nested inside,
        // same "always <= stunTimer" shape as EnemyBasePD's own tick-down. The Movement section below and
        // lockedOut above just re-check state.stunTimer > 0f fresh every tick — no falling-edge action needed
        // here at all (2026-09-22): _physicsCollider is left enabled throughout a knockback slide now (see
        // Knockback()'s own doc comment), and linearDamping's own per-tick reassertion in Movement already
        // covers the falling edge for that.
        if (state.stunTimer > 0f)
        {
            state.stunTimer -= delta;
            if (state.trueStunTimer > 0f) state.trueStunTimer -= delta;
        }

        // Surge — blocked from STARTING while weapons are disabled (e.g. a special action) or while any
        // active ability holds an explicit Surge block (IAbilityPD.ActiveSurgeBlock — see
        // CharacterStatsPD.IsSurgeBlocked's own doc comment for why this is a separate, orthogonal check
        // from IsWeaponsDisabled rather than folded into it); ending is still always allowed.
        if (input.surgeHeld && !_stats.IsSurging && !_stats.IsWeaponsDisabled && !_stats.IsSurgeBlocked && _stats.CurrentEnergy >= _stats.SurgeActivationThreshhold)
            _stats.StartSurge();
        else if (!input.surgeHeld && _stats.IsSurging)
            _stats.TryEndSurge();

        // Movement
        Vector3 moveDir = new Vector3(input.direction.x, 0, input.direction.y).normalized;
        if (moveDir != Vector3.zero)
            state.lastMoveDir = moveDir;
        // ConstantMovementAugment: no current input, but forced to always be moving — fall back to the last
        // real direction instead of standing still. state.lastMoveDir is already normalized (only ever
        // assigned from an already-.normalized moveDir above), so this stays full-speed, same as every real
        // move input — see SetForceMovement's own doc comment.
        else if (state.forceMovement)
            moveDir = state.lastMoveDir;

        // Reasserted every tick from State, same reasoning as the velocity writes right below (and the same
        // lesson as DummyTargetPD's own per-tick kinematic-lock pattern) — 2026-09-21 fix, was a one-shot
        // toggle in StartDash/EndDash/StartSpecialMovement/EndSpecialMovement (plus a separate copy in the
        // death path below) instead. That was a genuine simulation-safety gap: linearDamping isn't part of
        // PurrDiction's own tracked/rolled-back UnityRigidbodyState (confirmed by reading that struct — it
        // only tracks linearVelocity/angularVelocity/isKinematic/isSleeping/useGravity), so a TRANSITION-based
        // toggle can desync across a resimulation — a speculative StartDash() could zero it, but if the
        // eventual corrected replay of that same tick never actually calls StartDash() (the dash input turned
        // out to be mispredicted), nothing in the corrected timeline would ever call EndDash() to restore it
        // either, leaving the real Rigidbody stuck at 0 permanently. Driving it from state.isDashing/
        // isSpecialMoving directly here instead means every replay — speculative or corrected — always ends
        // this exact tick with the right value, regardless of what any other, possibly-discarded pass did.
        _rigidbody.rb.linearDamping = (state.isDashing || state.isSpecialMoving) ? 0f : normalLinearDamping;

        if (state.isDashing)
            _rigidbody.velocity = state.dashVelocity;
        else if (state.isSpecialMoving)
            _rigidbody.velocity = state.specialVelocity;
        else if (state.stunTimer > 0f)
        {
            // Deliberately no AddForce/velocity write here — let whatever velocity Knockback() applied decay
            // on its own via the Rigidbody's own configured linear drag, same as the enemy-side equivalent.
        }
        else
            _rigidbody.AddForce(moveDir * (_stats.MoveSpeed * delta * 5f), ForceMode.VelocityChange);

        // XP orb collection
        state.orbSweepTimer -= delta;
        if (state.orbSweepTimer <= 0f)
        {
            state.orbSweepTimer = SweepInterval;
            SweepForOrbs();
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (_stats != null)
        {
            // Death is state-critical — only act on server-confirmed dead, and only on the transition.
            bool isDead = _stats.verifiedState?.isDead ?? false;
            if (isDead != _wasDeadLastFrame)
            {
                if (_physicsCollider != null) _physicsCollider.enabled = !isDead;
                _wasDeadLastFrame = isDead;
            }
        }
    }

    // ── XP orbs ────────────────────────────────────────────────────────────
    private const float SweepInterval = 0.2f;
    private static int _orbLayer;
    private static readonly Collider[] _orbHits = new Collider[256];

    private void SweepForOrbs()
    {
        int count = Physics.OverlapSphereNonAlloc(transform.position, _stats.XPRadius, _orbHits, _orbLayer);
        for (int i = 0; i < count; i++)
        {
            var orb = _orbHits[i].GetComponent<XPOrbPD>();
            if (orb != null)
                orb.TryStartAttracting(owner ?? default);
        }
    }

    private void ToggleReadyState(ref State state)
    {
        state.isReady = !state.isReady;
        if (owner.HasValue && PausePD.instance != null)
            PausePD.instance.SetPlayerReady(owner.Value, state.isReady);
    }

    // ── Dash helpers ───────────────────────────────────────────────────────
    private void StartDash(ref State state)
    {
        state.isDashing    = true;
        state.currentCharges--;
        state.dashTimer    = DashDuration;
        // See its own doc comment — captured here as the one guaranteed-clear position for EndDash()'s own
        // TeleportSafetyPD fallback.
        state.dashStartPosition = _rigidbody.position;
        // Pre-adjusted (2026-09-20) so a natural, uninterrupted dash already lands on a safe spot instead of
        // relying purely on EndDash()'s own post-hoc correction — see ResolveSafeTravelVelocity's own doc
        // comment for the full reasoning.
        state.dashVelocity = ResolveSafeTravelVelocity(state.dashStartPosition, state.lastMoveDir, DashSpeed, DashDuration);

        if (state.chargeRegenTimer <= 0f)
            state.chargeRegenTimer = _stats.DashCooldown;

        // Committed channel, not the general GrantInvincibility pool — see CharacterStatsPD.
        // GrantCommittedInvincibility's own doc comment. Lets Interrupt() revoke exactly this grant without
        // touching any other overlapping invincibility source.
        _stats.GrantCommittedInvincibility(state.dashTimer);
        if (_physicsCollider != null) _physicsCollider.enabled = false;
        // linearDamping itself is handled every tick in the Movement section below, not here — see that
        // block's own comment for why a one-shot toggle here would be simulation-unsafe. (Confirmed live why
        // zeroing it matters at all: even though dashVelocity is force-reassigned to _rigidbody.velocity every
        // single tick, Unity's own physics step applies linear drag damping as part of integrating THAT SAME
        // tick, using whatever velocity was just set — so a non-zero drag, 9 on the player's own Rigidbody,
        // silently ate a consistent ~15% off the real distance traveled every tick, regardless of how
        // carefully ResolveSafeTravelVelocity computed the intended distance.)
        _rigidbody.velocity = Vector3.zero;
        _onDashStarted.Invoke();
        _onDashMovementStarted.Invoke();

        // Owner-only, same convention as EnemyBasePD.TriggerHitFeedback's own attacker.isOwner-gated hit SFX
        // — every machine runs this same Simulate() code, but only the dashing player's own client should
        // hear it. isReplaying-guarded too (same as every other one-shot cosmetic side effect fired directly
        // from Simulate() in this codebase, e.g. EnemyBasePD's pause-entry ResetInterpolation()) — StartDash()
        // itself only ever runs once per real dash, but a corrected replay of that same tick would otherwise
        // call this a second time.
        if (isOwner && !predictionManager.isReplaying && SFXManager.instance != null)
            SFXManager.instance.Play(dashSfx, transform.position);
    }

    // interrupted: false for the natural end (Simulate()'s own dashTimer<=0 check) — fires _onDashEnded as
    // always, invincibility left to expire on its own tiny remaining tail. true only from Interrupt(): fires
    // _onDashInterrupted INSTEAD (never both — see that event's own doc comment for why "reward on
    // completion" listeners need this distinction) and cancels the committed invincibility grant immediately
    // rather than leaving it to run out.
    // Speed-up cap for ResolveSafeTravelVelocity below (2026-09-20) — a correction that would need to travel
    // faster than this multiple of the caller's own base speed is discarded entirely in favor of "dash in
    // place" (zero velocity) instead of actually applying it. Only caps SPEEDING UP: slowing down (an obstacle
    // sat close to the start of the path) has no equivalent cap and never gets discarded — coming to a stop
    // right at an obstacle's edge is exactly the intended behavior.
    //
    // Deliberately intended as THE multiplier for every dash-style movement going forward, not a per-ability
    // tunable — this is a QOL BUFF, not a safety cap first and foremost: with a 1.0 multiplier, a dash either
    // cleanly clears an obstacle or it doesn't, with nothing in between. At 1.25, a dash that was aimed just
    // slightly short of actually clearing an obstacle gets a "grace distance" boost to travel a bit farther
    // than its own normal speed would allow, so a near-miss still carries the player all the way through
    // instead of leaving them short. It's deliberately "cheating" the player's own dash distance upward within
    // a small margin specifically to make dashing over/through obstacles feel more forgiving — the fallback to
    // "dash in place" only exists for the case where even that boosted distance still isn't enough (a
    // sufficiently deep/wide obstacle), not as the main point of this value. Private, not [SerializeField]
    // (2026-09-21) — see normalLinearDamping's own comment for why these particular fields are entirely
    // script-controlled rather than Inspector-editable.
    private const float maxDashSpeedUpMultiplier = 1.25f;

    // Recomputes a dash-style movement's own velocity so, if uninterrupted, it lands exactly on a safe
    // (non-obstructed, per TeleportSafetyPD) spot by the time its own duration runs out — shared by StartDash()
    // and StartSpecialMovement() (2026-09-20), the same START-side pairing ResolveTeleportSafeLanding below
    // already is for the END of these same two movement kinds. Rather than always moving at a flat speed and
    // relying on the END-of-window correction (EndDash/EndSpecialMovement's own ResolveTeleportSafeLanding
    // call, left completely UNCHANGED — still the backstop for an INTERRUPTED window or a mid-window death,
    // either of which can land at an arbitrary point along the path this pre-check never sees) to visibly snap
    // the player out of an obstacle at the very last moment, this computes the movement's own NATURAL
    // (uncorrected) endpoint up front and searches for a corrected one.
    //
    // Searches the FORWARD (clear-the-obstacle) and BACKWARD (stop-short) directions SEPARATELY via
    // TeleportSafetyPD.FindSafeTeleportPositionOneDirection (2026-09-21 fix — was one bidirectional
    // FindSafeTeleportPosition call, "whichever direction finds a clear spot first wins") and explicitly
    // prefers forward when it's within the speed-up grace cap, falling back to backward otherwise. The
    // bidirectional version was picking the far side almost every time the obstacle sat well inside the
    // movement's own full range: the natural endpoint lands past the WHOLE obstacle in that case, so
    // "keep going" only needs a tiny step while "back up" has to cross the obstacle's entire width first —
    // forward reliably won that race, got rejected by the speed cap, and fell back to "dash in place" far
    // more often than intended, discarding a perfectly good near-side stop that was never actually
    // considered. Searching each direction independently and choosing deliberately (not by which one
    // happens to resolve first) fixes that — see maxDashSpeedUpMultiplier's own doc comment for why forward
    // is still preferred when it's actually reachable within the grace margin.
    //
    // The forward search's own maxDistance is bounded tightly to the grace margin itself
    // (naturalDistance * (multiplier - 1)) rather than some larger cap — anything beyond that distance would
    // only get rejected by the speed check anyway, so there's no reason to search (or accept) further out.
    // The backward search's maxDistance is bounded to the movement's own natural travel distance (plus one
    // step's worth of margin), same reasoning as before: it walks straight back toward startPosition, which
    // is ALWAYS already clear (the player was already standing there), so that bound is mathematically
    // guaranteed to reach a clear spot before running out of room — genuine total failure (the
    // !nearEndpoint.HasValue branch below) should be unreachable in ordinary play.
    //
    // Total failure on BOTH searches falls back to "dash in place" (Vector3.zero) — deliberately NOT the
    // original uncorrected speed: falling back to full speed would just recreate the exact obstacle-landing
    // problem this whole system exists to avoid.
    //
    // DOES constrain this to the playable area's own boundary now (2026-09-22 fix, same isValidPosition
    // convention as BWBlinkAbilityPD) — the original reasoning here ("real physical Rigidbody motion is still
    // genuinely blocked by the solid Bounds wall collider, so no precomputed velocity could ever carry the
    // player past it") turned out not to hold: BoundsCollision lives on _physicsCollider, which StartDash()
    // disables for the ENTIRE dash window (needed so dashes can pass through decorative Obstacles/Bounds
    // geometry at all) — so there's no physical collision to rely on for the whole duration of a dash, not just
    // a tunneling edge case. Passing isValidPosition here means the CORRECTED endpoint this method computes can
    // never be one that lands outside PlayableAreaPD's own polygon in the first place, same belt-and-suspenders
    // reasoning BWBlink's own isValidPosition already relies on.
    private Vector3 ResolveSafeTravelVelocity(Vector3 startPosition, Vector3 direction, float speed, float duration)
    {
        if (duration <= 0f) return direction * speed;

        float naturalDistance = speed * duration;
        Vector3 naturalEndpoint = startPosition + direction * naturalDistance;
        Vector2 direction2D = new Vector2(direction.x, direction.z);
        Vector2 backwardDirection2D = -direction2D;
        float physicsRadius = PhysicsColliderRadius;
        float stepSize = physicsRadius * 0.5f;
        // physicsRadius + stepSize, NOT the -0.05f convention ResolveTeleportSafeLanding's own post-hoc
        // correction still uses (unchanged there) — that -0.05 deliberately makes the query MORE permissive
        // than the player's true size, accepting a spot before the player's real collider has actually
        // cleared the wall by a hair, relying on Unity's own physics to gently resolve the last sliver of
        // overlap. That's invisible for a stationary post-hoc correction, but confirmed live here to produce
        // a visible "bump" outward on the clear-the-obstacle path specifically — the player is still carrying
        // dash momentum FORWARD into that residual overlap when it resolves, instead of resting into it from
        // a stop. A flat +0.05f margin (tried first) wasn't generous enough to reliably absorb it either —
        // this path is inherently the TIGHTEST case (the accepted point is, by construction, the very first
        // sample that just barely clears, unlike the near-side stop-short path, which never gets anywhere
        // near this tight and has never shown the bump), so it needs real headroom, not a token buffer. Tied
        // to stepSize itself (a full extra half-radius) rather than another flat guess, so the margin scales
        // sensibly with the player's own size instead of needing its own separate tuning.
        float checkRadius = physicsRadius + stepSize;
        int obstacleMask = LayerMask.GetMask("Bounds", "Obstacle");

        // Try clearing the obstacle first — bounded to exactly the grace margin, so anything this finds is
        // automatically within the speed cap already (no separate check needed).
        float graceDistance = Mathf.Max(0f, naturalDistance * (maxDashSpeedUpMultiplier - 1f));
        if (graceDistance > 0f)
        {
            Vector3? clearedEndpoint = TeleportSafetyPD.FindSafeTeleportPositionOneDirection(
                naturalEndpoint,
                direction2D,
                stepSize,
                checkRadius,
                obstacleMask,
                maxDistance: graceDistance,
                isValidPosition: PlayableAreaPD.instance != null ? PlayableAreaPD.instance.IsInsidePlayableArea : (System.Func<Vector3, bool>)null);

            if (clearedEndpoint.HasValue)
            {
                float clearedDistance = Vector3.Distance(startPosition, clearedEndpoint.Value);
                return direction * (clearedDistance / duration);
            }
        }

        // Couldn't clear it within the grace margin — get as close as possible instead.
        Vector3? nearEndpoint = TeleportSafetyPD.FindSafeTeleportPositionOneDirection(
            naturalEndpoint,
            backwardDirection2D,
            stepSize,
            checkRadius,
            obstacleMask,
            maxDistance: naturalDistance + stepSize,
            isValidPosition: PlayableAreaPD.instance != null ? PlayableAreaPD.instance.IsInsidePlayableArea : (System.Func<Vector3, bool>)null);

        if (!nearEndpoint.HasValue) return Vector3.zero;

        float nearDistance = Vector3.Distance(startPosition, nearEndpoint.Value);
        return direction * (nearDistance / duration);
    }

    // Shared by EndDash() and EndSpecialMovement() (2026-09-19) — any dash-STYLE movement window (a real
    // dash, RogueSpinAbilityPD's leap, and any future ability built on StartSpecialMovement) ends up needing
    // the exact same "did this window's own end point land inside something it phased through, and if so
    // where's the nearest safe spot" resolution, so it lives here once rather than being copy-pasted per
    // caller. See TeleportSafetyPD's own doc comment for the full algorithm.
    //
    // startPosition is the one position in the whole window guaranteed clear of obstacles (the player was
    // already standing there before the window began — StartDash()/StartSpecialMovement() each capture their
    // own), used both as FindOutwardDirection's known-clear raycast anchor AND as FindSafeTeleportPosition's
    // total-failure fallback (same "always have a known-safe fallback" shape BWBlinkAbilityPD's own
    // pre-teleport-position fallback uses).
    //
    // fallbackTravelVelocity (dashVelocity/specialVelocity, already XZ-plane-only in practice) is used ONLY
    // if FindOutwardDirection itself finds nothing to raycast against (e.g. the end position turned out not
    // to actually be blocked) — the normal case instead resolves the wall's own true outward surface normal,
    // not the raw travel direction, since backing straight along the original travel vector doesn't
    // correspond to moving away from the wall's own actual surface once it's hit at an angle (confirmed live:
    // using the travel direction directly used to "spit the player out" at a diagonal).
    //
    // FindSafeTeleportPositionMultiDirectional, not the plain two-direction FindSafeTeleportPosition
    // (2026-09-21) — same reasoning as BWBlinkAbilityPD's own identical swap: an interrupted/dead dash is
    // resolving a single STATIC embedded position, same as a teleport, not a moving-through-space search
    // along one fixed axis, so it can benefit from the same 8-direction sideways consideration (e.g. an
    // interrupt landing well past the midpoint of a long, narrow obstacle). The wall's own true outward
    // normal (searchDirection2D above) is still passed in as the "forward" reference, so it still wins ties
    // exactly as before — this only adds the sideways options that direction-only search never had.
    //
    // ResetInterpolation() only fires when a correction actually happened, so the overwhelmingly common
    // uneventful case pays zero extra cost here beyond the queries this always makes regardless.
    // Resolves a PROSPECTIVE teleport target to a safe nearby spot if it would otherwise land inside/past
    // Bounds/Obstacle geometry or outside the playable area — extracted (2026-10-04) from BWBlinkAbilityPD's
    // own inline copy of this exact block (left untouched there — this is a pure addition, not a refactor of
    // working code) so a second teleport-to-a-point user (RogueVanishBlinkAugment) doesn't need to duplicate
    // the same safety math a third time. Different shape from ResolveTeleportSafeLanding below: that one
    // corrects the CURRENT position after a dash/special-movement window already happened (searching
    // outward from a known-clear start); this one resolves a TARGET before ever moving there at all
    // (FindSafeTeleportPositionMultiDirectional, not a two-direction search, since a teleport is free to
    // resolve to any direction around the target — it never physically travels through the space in
    // between). See TeleportSafetyPD's own doc comment for the full algorithm. Falls back to fromPosition
    // itself (i.e. "don't move at all") on total failure — a caller-level choice, not something
    // TeleportSafetyPD bakes in generically.
    public Vector3 ResolveSafeTeleportTarget(Vector3 rawTarget, Vector3 fromPosition)
    {
        Vector2 direction2D = new Vector2(rawTarget.x - fromPosition.x, rawTarget.z - fromPosition.z);
        float physicsRadius = PhysicsColliderRadius;
        float stepSize = physicsRadius * 0.5f;
        return TeleportSafetyPD.FindSafeTeleportPositionMultiDirectional(
            rawTarget,
            direction2D,
            stepSize,
            physicsRadius + stepSize,
            LayerMask.GetMask("Bounds", "Obstacle"),
            isValidPosition: PlayableAreaPD.instance != null ? PlayableAreaPD.instance.IsInsidePlayableArea : (System.Func<Vector3, bool>)null) ?? fromPosition;
    }

    private void ResolveTeleportSafeLanding(Vector3 startPosition, Vector3 fallbackTravelVelocity)
    {
        Vector3 currentPosition = _rigidbody.position;
        int obstacleMask = LayerMask.GetMask("Bounds", "Obstacle");
        Vector2 searchDirection2D = TeleportSafetyPD.FindOutwardDirection(startPosition, currentPosition, obstacleMask)
            ?? new Vector2(fallbackTravelVelocity.x, fallbackTravelVelocity.z);
        float physicsRadius = PhysicsColliderRadius;
        float stepSize = physicsRadius * 0.5f;
        // physicsRadius + stepSize, NOT the old -0.05f (2026-09-21 fix, same margin fix as
        // ResolveSafeTravelVelocity/BWBlinkAbilityPD — see ResolveSafeTravelVelocity's own doc comment for
        // the full derivation of why a MORE permissive margin causes a visible "boop" and why a
        // step-size-proportional buffer is the fix, not another flat guess).
        // isValidPosition (2026-09-22 fix, same convention as ResolveSafeTravelVelocity/BWBlinkAbilityPD) —
        // this is the backstop for an INTERRUPTED window or mid-window death: _physicsCollider (which
        // BoundsCollision lives on) is disabled for the entire dash/special-movement window, so the
        // currentPosition it's correcting FROM can genuinely be anywhere outside the playable area at the
        // moment of interrupt/death, not just near an obstacle — without this the correction search itself
        // could walk further out into the open space past the wall instead of back toward the map.
        Vector3 safePosition = TeleportSafetyPD.FindSafeTeleportPositionMultiDirectional(
            currentPosition,
            searchDirection2D,
            stepSize,
            physicsRadius + stepSize,
            obstacleMask,
            isValidPosition: PlayableAreaPD.instance != null ? PlayableAreaPD.instance.IsInsidePlayableArea : (System.Func<Vector3, bool>)null) ?? startPosition;
        if (safePosition != currentPosition)
        {
            _rigidbody.position = safePosition;
            if (_stats != null) _stats.transform.position = safePosition;
            _predictedTransform?.ResetInterpolation();
        }
    }

    private void EndDash(ref State state, bool interrupted = false)
    {
        state.isDashing = false;
        // Position resolved FIRST, before re-enabling the physics collider or firing any event (2026-09-22
        // reorder — was collider-enable, THEN position resolve, the wrong order) — StartDash() disables
        // _physicsCollider for the whole dash window (the BoundsCollision check lives on that collider), so an
        // interrupted/cut-short dash can genuinely be resting outside the playable area at this exact instant.
        // Same "resolve position first, everything else after" convention the death-path early-return in
        // Simulate() already documents and follows — e.g. a lethal hit's own Knockback() calls Interrupt()
        // (and therefore this exact method) before applying its own impulse, so the corrected position has to
        // already be in place before that impulse, or before _onDashInterrupted/_onDashEnded's own listeners
        // can read a still-out-of-bounds transform.
        ResolveTeleportSafeLanding(state.dashStartPosition, state.dashVelocity);
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        // linearDamping restoration handled every tick in the Movement section, not here — see that block's
        // own comment.

        _rigidbody.velocity = Vector3.zero;
        if (interrupted)
        {
            _stats?.CancelCommittedInvincibility();
            _onDashInterrupted.Invoke();
        }
        else
        {
            _onDashEnded.Invoke();
        }
        // Movement window closes regardless of why — see _onDashMovementEnded's own field comment.
        _onDashMovementEnded.Invoke(interrupted);
    }

    // ── Special movement helpers ─────────────────────────────────────────────
    // Called directly by an ability component's own Activate() (e.g. RogueSpinAbilityPD) — same
    // cross-identity currentState mutation pattern as CharacterStatsPD.StartSurge()/GrantInvincibility(),
    // safe here too since Activate() is only ever invoked from this same identity's own Simulate() (via the
    // ability1Pressed/ability2Pressed gates above), so `currentState` and the `ref state` that call is
    // already inside are the same storage. duration/speed are owned by the caller (the ability's own
    // tunable fields), not local constants — this method only knows HOW to move the player (driving the
    // rigidbody), never the actual tuned values, which stay owned by whichever ability is calling in.
    // Generic/reusable by design — any future ability that needs a leap/dash-style movement window can call
    // this too, not just RogueSpinAbilityPD. direction is likewise the caller's choice (e.g. RogueSpinAbilityPD
    // passes currentState.cursorDir, not lastMoveDir) — this method doesn't assume any particular direction
    // source, just applies whatever it's given. speed is a flat units/sec value, same "not MoveSpeed-scaled"
    // reasoning as DashSpeed above — this method never reads _stats.MoveSpeed/surge state at all, so a
    // caller that DOES want MoveSpeed-derived movement is free to compute that itself before calling in.
    public void StartSpecialMovement(float duration, float speed, Vector3 direction)
    {
        if (_stats == null || _rigidbody == null) return;

        currentState.isSpecialMoving = true;
        // See its own doc comment — captured here as ResolveTeleportSafeLanding's known-clear anchor/fallback,
        // and now also as ResolveSafeTravelVelocity's own start position.
        currentState.specialMoveStartPosition = _rigidbody.position;
        // Pre-adjusted (2026-09-20) so a natural, uninterrupted window already lands on a safe spot — see
        // ResolveSafeTravelVelocity's own doc comment.
        currentState.specialVelocity = ResolveSafeTravelVelocity(currentState.specialMoveStartPosition, direction, speed, duration);
        currentState.specialMoveTimer = duration;

        if (_physicsCollider != null) _physicsCollider.enabled = false;
        // linearDamping handled every tick in the Movement section — see StartDash's own comment.
        _rigidbody.velocity = Vector3.zero;
    }

    private void EndSpecialMovement(ref State state)
    {
        state.isSpecialMoving = false;
        // Position resolved FIRST — same reorder/reasoning as EndDash's own comment.
        ResolveTeleportSafeLanding(state.specialMoveStartPosition, state.specialVelocity);
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        // linearDamping restoration handled every tick in the Movement section — see EndDash's own comment.

        _rigidbody.velocity = Vector3.zero;
    }

    // Player-side counterpart to IEnemyPD.Interrupt() — cancels whatever committed action (dash, or an
    // ability using StartSpecialMovement, e.g. RogueSpinAbilityPD's leap) this player is currently mid-way
    // through, reverting to normal movement. BWBlinkAbilityPD needs no exclusion here — its teleport is
    // instantaneous and never sets isDashing/isSpecialMoving in the first place, so there's nothing for this
    // to catch.
    //
    // Dash's own invincibility (GrantCommittedInvincibility, see StartDash) is cancelled directly here, since
    // PlayerControllerPD owns that grant itself. An interrupted ABILITY's own invincibility (e.g.
    // RogueSpinAbilityPD's leap) is instead the responsibility of that ability's own
    // _onSpecialMovementInterrupted listener — same "small composable primitive, whoever owns the grant
    // reverts it" convention as the enemy-side SetX(bool) primitives — since not every ForcesSpecialAction
    // ability necessarily grants committed invincibility.
    //
    // Neither path fires its own "completed" event (_onDashEnded / IAbilityPD's NotifyAbilityEnd) — an
    // interrupted dash fires _onDashInterrupted instead (see its own doc comment), and an interrupted ability
    // is expected to skip its own NotifyDashEnded/NotifyAbilityEnd calls the same way RogueSpinAbilityPD's own
    // EndActiveWindow does. Being interrupted is a punishment, not a "the action still counts" completion —
    // "reward on completion" listeners (DashExplosionAugment, DashHealAugment, PhantomBladeOnDashAugment)
    // must not still pay out for an action that got cut short. Listeners that only care about the WINDOW
    // closing regardless of why (e.g. DashThroughInterruptAugment's own sweep) subscribe to both the normal
    // end event AND the interrupted one.
    //
    // Returns whether this call actually cancelled something, matching IEnemyPD.Interrupt()'s own
    // bool-return contract (so a caller — e.g. an upcoming enemy type — can tell a real interrupt from a
    // no-op).
    public bool Interrupt()
    {
        bool cancelledSomething = false;

        if (currentState.isDashing)
        {
            EndDash(ref currentState, interrupted: true);
            cancelledSomething = true;
        }

        if (currentState.isSpecialMoving)
        {
            EndSpecialMovement(ref currentState);
            _onSpecialMovementInterrupted.Invoke();
            cancelledSomething = true;
        }

        // A Berserk Surge (2026-08-23) is interruptible — unlike a normal Surge (blocked outright by
        // forcedSurge via TryEndSurge()), a hard interrupt landing here can cut one short via ForceEndSurge()
        // (see its own doc comment for the forcedSurge reset + BerserkSurgeEndGrace hand-off this correctly
        // triggers, same as a natural energy-depletion end). This is what makes KnightBerserkAbilityPD's own
        // buff (which only ever ends via the resulting _onSurgeEnded, see its own class doc comment) end
        // early on an interrupt instead of running until Berserk naturally drains dry.
        if (_stats != null && _stats.IsForcedSurging)
        {
            _stats.ForceEndSurge();
            cancelledSomething = true;
        }

        // Status popup only for an actual committed-action cancel (dash, a CA ability's StartSpecialMovement
        // window, or a Berserk Surge cut short) — never for a no-op call (nothing currently mid-action).
        if (cancelledSomething) _stats?.NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Interrupted);

        return cancelledSomething;
    }

    // Called by CharacterStatsPD.StartBerserkSurge() the instant a Berserk Surge genuinely starts (2026-08-31)
    // — the reverse direction of ActiveSurgeBlock: an ability whose own ActiveSurgeBlock is true already
    // force-ends Surge when IT activates (see IAbilityPD.ActiveSurgeBlock's own doc comment); this is what
    // makes the same relationship hold when Surge activates FIRST instead, cutting off whichever ability slot
    // is currently active with ActiveSurgeBlock true (today, only RogueSpinAbilityPD can ever be in this
    // state). Deliberately calls the ability's own IAbilityPD.CancelActiveWindow() rather than reaching into
    // PlayerControllerPD's own isSpecialMoving/Interrupt() machinery — CancelActiveWindow is scoped to
    // ActiveSurgeBlock specifically, not ForcesSpecialAction, so this stays correct even for a future
    // ActiveSurgeBlock ability that doesn't lock movement at all.
    public void CancelSurgeBlockingAbility()
    {
        if (_ability1 != null && _ability1.IsActive && _ability1.ActiveSurgeBlock) _ability1.CancelActiveWindow();
        if (_ability2 != null && _ability2.IsActive && _ability2.ActiveSurgeBlock) _ability2.CancelActiveWindow();
    }

    // Ends whatever special-movement window is currently open (isSpecialMoving, physics collider,
    // rigidbody velocity) WITHOUT firing _onSpecialMovementInterrupted (2026-08-31) — for a self-cancel
    // path where the ability itself already knows it's ending and handles its own teardown directly (e.g.
    // RogueSpinAbilityPD.CancelActiveWindow(), called when Berserk Surge cuts its leap short), so
    // re-invoking the full interrupted event would just be a redundant second call into the same ability.
    // Same underlying EndSpecialMovement() primitive Interrupt() itself uses for this half of its own
    // teardown, exposed here so a caller that ISN'T going through Interrupt() can still keep this
    // controller-owned state from drifting out of sync with the ability's own window closing — without
    // this, isSpecialMoving/the disabled collider would otherwise just sit there until specialMoveTimer
    // (a SEPARATE timer from the ability's own activeTimer, started together but never force-synced) happens
    // to run out on its own, exactly the "two parallel timers hoping to agree" shape already fixed for King
    // Orbiter's pull-in — this closes it directly instead of hoping the two stay aligned. Idempotent — a
    // no-op if isSpecialMoving is already false, so it's always safe to call speculatively.
    public void EndSpecialMovementWindow()
    {
        if (currentState.isSpecialMoving) EndSpecialMovement(ref currentState);
    }

    // Public entry point for a real, direct stun (not knockback-derived) — sets trueStunTimer as well as the
    // shared stunTimer gate, same isTrueStun split as EnemyBasePD.Stun()/ApplyStun(). No crowd-control-resist
    // scaling here (unlike the enemy-side Stun()) — players don't have a resist stat, not because they're
    // deliberately meant to be immune-proof; add one here first if that's ever needed. Also fires the "Stun"
    // status popup — rising edge only (see the popup gate below), same convention as the enemy side. No
    // in-game source calls this yet, but the popup is wired ahead of one existing.
    public void Stun(float duration)
    {
        bool wasTrueStunned = currentState.trueStunTimer > 0f;
        ApplyStun(duration, isTrueStun: true);
        if (!wasTrueStunned && currentState.trueStunTimer > 0f) _stats?.NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Stun);
    }

    // Player-side counterpart to EnemyBasePD.InterruptTrueStun — for a FUTURE caller that wants to grant
    // bonus true-stun duration on the SAME hit as a paired Interrupt() call, without ALSO popping "Stun" —
    // the "Interrupted" popup already conveys that visually, same reasoning EnemyBasePD.InterruptTrueStun's
    // own doc comment gives. No live caller yet (same "wired ahead of one existing" scoping as Stun() itself)
    // — exists so that whenever an enemy attack DOES interrupt-and-stun a player on one hit, it has a path
    // that can't accidentally double-pop, instead of composing Interrupt() + Stun() together (which would).
    public void InterruptTrueStun(float duration) => ApplyStun(duration, isTrueStun: true);

    // Shared MECHANICAL gate for Stun()/InterruptTrueStun() above and Knockback() below — Mathf.Max-based so
    // it never shortens a longer stun already in progress, same convention as the enemy-side version. Popup
    // logic deliberately lives in Stun() itself, not here (see InterruptTrueStun's own doc comment for why) —
    // same split EnemyBasePD.Stun()/InterruptTrueStun()/ApplyStun() already establishes. Doesn't check
    // anything like the enemy-side isStunnable/stunResistant gate — not because players are deliberately
    // meant to be immune-proof, just that nothing currently needs stun immunity/resistance; add that check
    // here first if it's ever required. Doesn't touch isDashing/isSpecialMoving or call Interrupt() itself —
    // Knockback() below composes that; a future standalone caller that also needs a committed action
    // cancelled first is responsible for calling Interrupt() itself, same "small composable primitive,
    // caller composes" convention as the SetX(bool) family.
    private void ApplyStun(float duration, bool isTrueStun = false)
    {
        if (duration <= 0f || (_stats != null && _stats.IsDead)) return;
        currentState.stunTimer = Mathf.Max(currentState.stunTimer, duration);
        if (isTrueStun) currentState.trueStunTimer = Mathf.Max(currentState.trueStunTimer, duration);
    }

    // Player-side counterpart to EnemyBasePD.AddKnockbackVelocity — same Vector3 impulse shape (magnitude =
    // force, direction = normalized) rather than separate force/direction params, for direct parity/easy
    // diffing against that method. One-time direct rigidbody velocity assignment (not a stored/re-applied
    // State field), left to decay on its own via the Rigidbody's own configured linear drag. No crowd-
    // control-resist scaling (see ApplyStun's own doc comment) — impulse is applied at full value, unlike the
    // enemy side's `impulse.normalized * effectiveSpeed` (which reduces to plain `impulse` once resist is
    // dropped, so this assigns it directly).
    //
    // Calls Interrupt() first (per design — anything that knocks a player back needs to safely end whatever
    // committed action they were mid-way through first, same reason Interrupt() itself exists), so by the
    // time velocity is applied isDashing/isSpecialMoving are already false and won't fight this tick's write.
    // Unlike the enemy side (where Interrupt() is a separate step the attacker composes explicitly — see
    // IEnemyPD.Interrupt()'s own doc comment), it's baked in directly here rather than left for the caller to
    // remember, since every current/planned player-knockback source wants it called first anyway.
    //
    // Derives an approximate stun duration from the impulse's magnitude via the same
    // ln(magnitude/threshold)/drag formula as the enemy side and applies it as a knockback (non-true) stun.
    //
    // Deliberately leaves _physicsCollider enabled throughout (2026-09-22, was disabled for the duration same
    // as StartDash/StartSpecialMovement) — a knockback should never shove a player through real Obstacle/
    // Bounds geometry the way a dash's own predicted-safe-landing math is now allowed to (see
    // ResolveSafeTravelVelocity's own doc comment); colliding with solid geometry mid-slide and stopping there
    // is the intended behavior, same as any other physical object being launched. linearDamping is left
    // untouched (still normalLinearDamping throughout, unlike dashing/special-moving) — the stunDuration
    // formula right below is already derived from that real decay rate, so this was correct as-is.
    public void Knockback(Vector3 impulse)
    {
        if (_rigidbody == null || _stats == null || _stats.IsDead) return;

        Interrupt();

        // Guarded by !isKinematic — no player script sets this rigidbody kinematic today, but
        // PredictedRigidbody's velocity setter throws ("not supported") if ever attempted on a kinematic
        // body, same reason every enemy-side velocity write (AddKnockbackVelocity included) guards itself
        // the same way. Cheap defensive parity in case a future ability/effect ever kinematic-freezes a
        // player (e.g. a grab-style enemy attack).
        if (!_rigidbody.isKinematic)
            _rigidbody.velocity = impulse;

        float speed = impulse.magnitude;
        float stunDuration = speed > knockbackStunThreshold
            ? Mathf.Log(speed / knockbackStunThreshold) / normalLinearDamping
            : 0f;
        ApplyStun(stunDuration);
    }
}
