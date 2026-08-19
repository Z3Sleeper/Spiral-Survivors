using PurrNet.Prediction;
using UnityEngine;

[RequireComponent(typeof(CharacterStatsPD))]
public class PlayerControllerPD : PredictedIdentity<PlayerControllerPD.Input, PlayerControllerPD.State>
{
    [SerializeField] private PredictedRigidbody _rigidbody;
    [SerializeField] private Transform _cameraTarget;

    private CharacterStatsPD _stats;
    private Collider _physicsCollider;
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
    private const float DashDuration = 0.2f;
    private const float DashSpeed    = 50f;

    // Knockback() derives an approximate stun duration from the force it's given, same shape as
    // EnemyBasePD.AddKnockbackVelocity's own formula (stunDuration = ln(force/threshold)/drag when force
    // exceeds threshold, else 0) — these two SerializeFields exist purely to tune that estimate, not to
    // actually apply drag themselves (that's the Rigidbody's own configured linear drag doing the real
    // decay). Defaults match the enemy-side constants; retune here if the player's own Rigidbody drag is
    // configured differently and the stun window ends up feeling too long/short relative to how long the
    // knockback slide actually looks.
    [SerializeField] private float knockbackStunDrag = 9f;
    [SerializeField] private float knockbackStunThreshold = 1f;

    // Per-instance, not static — each player's dash events are its own, not a shared global channel.
    // Surge events live on CharacterStatsPD instead, since StartSurge()/TryEndSurge() are defined there.
    private PredictedEvent _onDashStarted;
    // Fires only on a NATURAL dash end (timer ran out) — NOT on Interrupt(), see _onDashInterrupted below.
    // "Reward on completion" listeners (DashExplosionAugment/DashHealAugment/PhantomBladeOnDashAugment) rely
    // on this distinction: an interrupted dash shouldn't still pay out its on-end effect.
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
    private PredictedEvent _onDashMovementEnded;

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
    public void AddDashMovementEndedListener(System.Action listener) => _onDashMovementEnded.AddListener(listener);
    public void RemoveDashMovementEndedListener(System.Action listener) => _onDashMovementEnded.RemoveListener(listener);

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
    public void NotifyDashMovementEnded() => _onDashMovementEnded.Invoke();

    // Grants back one dash charge, clamped to the character's own current max — e.g.
    // DashThroughInterruptAugment refunding a charge on a successful interrupt. Doesn't touch
    // chargeRegenTimer — the existing passive regen (see Simulate()'s own regen block) keeps counting toward
    // whatever charge it was already counting toward; this only adds one on top, up to the cap.
    public void RefundDashCharge()
    {
        if (_stats == null) return;
        currentState.currentCharges = Mathf.Min(currentState.currentCharges + 1, _stats.dashCharges);
    }

    // Shaves time off the currently-regenerating dash charge's chargeRegenTimer — e.g.
    // OnAbilityStartReduceDashCDAugment. Unlike RefundDashCharge above (a flat +1, no timer interaction),
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
            }
        }
    }

    // Not predicted state — set once (ConstantMovementAugment.OnApply(), never reverted, same "permanent for
    // the whole match" lifecycle every other simple stat-granting augment already uses, e.g. MoveSpeedAugment
    // never overrides OnDestroy() either) and read every Simulate() tick, same category as any other
    // effectively-constant-for-the-match field (baseMoveSpeed, etc.) rather than a per-tick predicted value.
    // Forces the Movement block below to fall back to state.lastMoveDir whenever there's no current input,
    // instead of standing still — "you can't stand still."
    private bool _forceMovement;
    public void SetForceMovement(bool force) => _forceMovement = force;

    private bool _wasDeadLastFrame;
    private bool _pendingReadyPressed;
    private bool _pendingPausePressed;
    private bool _pendingPickPressed;
    private int  _pendingPickIndex;
    private float _pendingPickLuckMultiplierA = 1f;
    private float _pendingPickLuckMultiplierB = 1f;
    private bool _pendingEndScreenReadyPressed;
    private InputSystem_Actions _input;

    // Predicted, not verified — this only drives the local owner's own button label, and instant
    // feedback on your own click matters more than the extra tick of latency verifiedState would add.
    public bool IsReady => currentState.isReady;
    public bool IsDashing => currentState.isDashing;
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
        // Same shape as isDashing/dashTimer/dashVelocity above, driven externally by StartSpecialMovement()
        // (called from an ability component's own Activate(), e.g. RogueSpinAbilityPD) rather than input
        // read directly here — PlayerControllerPD doesn't know or care which ability is running, just that
        // something asked for movement.
        public bool isSpecialMoving;
        public float specialMoveTimer;
        public Vector3 specialVelocity;
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
        public void Dispose() { }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────
    private int _lastKnownMaxCharges;

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
        _orbLayer = LayerMask.GetMask("XPOrb");
        _lastKnownMaxCharges = _stats != null ? _stats.dashCharges : 1;
    }

    protected override State GetInitialState() => new()
    {
        lastMoveDir  = Vector3.forward,
        cursorDir    = Vector3.forward,
        currentCharges = _stats != null ? _stats.dashCharges : 1,
    };

    public Transform CameraTarget => _cameraTarget != null ? _cameraTarget : transform;

    protected override void LateAwake()
    {
        _onDashStarted = new PredictedEvent(predictionManager, this);
        _onDashEnded   = new PredictedEvent(predictionManager, this);
        _onDashInterrupted = new PredictedEvent(predictionManager, this);
        _onSpecialMovementInterrupted = new PredictedEvent(predictionManager, this);
        _onDashMovementStarted = new PredictedEvent(predictionManager, this);
        _onDashMovementEnded   = new PredictedEvent(predictionManager, this);

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
            input.pausePressed |= _input.Player.Pause.WasPressedThisFrame();

            // Purely local UI toggle, not part of the networked/predicted Input struct — matches old
            // PlayerController's identical direct (un-networked) LocalUIManager calls, since this only
            // ever affects this machine's own UI display, never simulation state. Holding Tab shows the
            // expanded stats panel (hiding the compact one) and the augment list; releasing reverses both.
            if (_input.Player.Tab.WasPressedThisFrame())
            {
                LocalUIManagerPD.instance?.SetStatsExpanded(true);
                LocalUIManagerPD.instance?.SetAugmentListVisible(true);
            }
            else if (_input.Player.Tab.WasReleasedThisFrame())
            {
                LocalUIManagerPD.instance?.SetStatsExpanded(false);
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
        if (_stats != null && _stats.dashCharges != _lastKnownMaxCharges)
        {
            state.currentCharges += _stats.dashCharges - _lastKnownMaxCharges;
            _lastKnownMaxCharges = _stats.dashCharges;
        }

        // Charge regen runs even while dead so the player has dashes on revive. Paused while actually
        // isDashing (2026-08-19) — a real dash only, NOT isSpecialMoving (RogueSpinAbilityPD's leap and
        // other ability-driven movement use that flag instead, see its own doc comment, and should keep
        // ticking normally). Plays nice with Interrupt() for free: EndDash(interrupted: true) sets isDashing
        // false immediately just like a natural end does, so regen resumes from wherever it was frozen
        // either way, no separate handling needed. ReduceDashCooldown() (other effects shaving time off the
        // current charge, e.g. OnAbilityStartReduceDashCDAugment) is untouched by this gate — it mutates
        // chargeRegenTimer directly, independent of this natural per-tick decrement, so it still applies
        // normally even mid-dash.
        if (_stats != null && state.currentCharges < _stats.dashCharges && !state.isDashing)
        {
            state.chargeRegenTimer -= delta;
            if (state.chargeRegenTimer <= 0f)
            {
                state.currentCharges++;
                state.chargeRegenTimer = state.currentCharges < _stats.dashCharges ? _stats.DashCooldown : 0f;
            }
        }

        if (_stats == null || _stats.IsDead)
        {
            // Halt whatever was in progress (e.g. a mid-dash death) so the corpse doesn't keep sliding.
            if (state.isDashing)
            {
                state.isDashing = false;
                _onDashEnded.Invoke();
            }
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
        // isDashing is deliberately NOT part of this hard lock (unlike its old formula) — dashing now follows
        // the same lenient rule as a Berserk-Surge-style forced Surge: only IAbilityPD.IsActionAbility
        // abilities get blocked while dashing (actionBlocked below), buff abilities stay castable through it.
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
        // Also blocked outright while actively dashing OR during a Berserk-Surge-style forced Surge
        // (CharacterStatsPD.IsForcedSurging) for any ability whose own IsActionAbility is true — buff
        // abilities (pure stat/buff-state grants) stay usable through either, only action abilities are
        // blocked. NOT ActiveSurgeBlock (a different, unrelated flag — see IAbilityPD.IsActionAbility's own
        // doc comment for why the two can't be conflated).
        if (input.ability1Pressed && _ability1 != null)
        {
            bool actionBlocked1 = (state.isDashing || _stats.IsForcedSurging) && _ability1.IsActionAbility;
            bool blocked1 = lockedOut || actionBlocked1;
            if (_ability1.IsReady && !blocked1) _ability1.Activate();
            else _ability1.NotifyActivationBlocked();
        }

        // Ability2 input — same gate/forward split as Ability1 above.
        if (input.ability2Pressed && _ability2 != null)
        {
            bool actionBlocked2 = (state.isDashing || _stats.IsForcedSurging) && _ability2.IsActionAbility;
            bool blocked2 = lockedOut || actionBlocked2;
            if (_ability2.IsReady && !blocked2) _ability2.Activate();
            else _ability2.NotifyActivationBlocked();
        }

        // Dash timer
        if (state.isDashing)
        {
            state.dashTimer -= delta;
            if (state.dashTimer <= 0f)
                EndDash(ref state);
        }

        // Special movement timer — same shape as the dash timer above.
        if (state.isSpecialMoving)
        {
            state.specialMoveTimer -= delta;
            if (state.specialMoveTimer <= 0f)
                EndSpecialMovement(ref state);
        }

        // Stun timer — see State.stunTimer/trueStunTimer's own doc comments. trueStunTimer nested inside,
        // same "always <= stunTimer" shape as EnemyBasePD's own tick-down. The Movement section below and
        // lockedOut above just re-check state.stunTimer > 0f fresh every tick — the one falling-edge action
        // needed is re-enabling _physicsCollider (see Knockback()'s own doc comment for why it's disabled in
        // the first place), same immediate-revert-on-the-exiting-edge shape as EndDash/EndSpecialMovement.
        if (state.stunTimer > 0f)
        {
            state.stunTimer -= delta;
            if (state.trueStunTimer > 0f) state.trueStunTimer -= delta;
            if (state.stunTimer <= 0f && _physicsCollider != null) _physicsCollider.enabled = true;
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
        else if (_forceMovement)
            moveDir = state.lastMoveDir;

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
        state.dashVelocity = state.lastMoveDir * DashSpeed;
        state.dashTimer    = DashDuration;

        if (state.chargeRegenTimer <= 0f)
            state.chargeRegenTimer = _stats.DashCooldown;

        // Committed channel, not the general GrantInvincibility pool — see CharacterStatsPD.
        // GrantCommittedInvincibility's own doc comment. Lets Interrupt() revoke exactly this grant without
        // touching any other overlapping invincibility source.
        _stats.GrantCommittedInvincibility(state.dashTimer);
        if (_physicsCollider != null) _physicsCollider.enabled = false;
        _rigidbody.velocity = Vector3.zero;
        _onDashStarted.Invoke();
        _onDashMovementStarted.Invoke();
    }

    // interrupted: false for the natural end (Simulate()'s own dashTimer<=0 check) — fires _onDashEnded as
    // always, invincibility left to expire on its own tiny remaining tail. true only from Interrupt(): fires
    // _onDashInterrupted INSTEAD (never both — see that event's own doc comment for why "reward on
    // completion" listeners need this distinction) and cancels the committed invincibility grant immediately
    // rather than leaving it to run out.
    private void EndDash(ref State state, bool interrupted = false)
    {
        state.isDashing = false;
        if (_physicsCollider != null) _physicsCollider.enabled = true;
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
        _onDashMovementEnded.Invoke();
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
        currentState.specialVelocity = direction * speed;
        currentState.specialMoveTimer = duration;

        if (_physicsCollider != null) _physicsCollider.enabled = false;
        _rigidbody.velocity = Vector3.zero;
    }

    private void EndSpecialMovement(ref State state)
    {
        state.isSpecialMoving = false;
        if (_physicsCollider != null) _physicsCollider.enabled = true;
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

        // Status popup only for an actual committed-action cancel (dash, or a CA ability's
        // StartSpecialMovement window) — never for a no-op call (nothing currently mid-action).
        if (cancelledSomething) _stats?.NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Interrupted);

        return cancelledSomething;
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
    // Also disables _physicsCollider for the duration — same push-collider-off convention as
    // StartDash/StartSpecialMovement (re-enabled on the stun's own falling edge, see Simulate()'s own stun
    // tick-down block), NOT the separate bounds collider that keeps players in the level, which is untouched
    // and stays active throughout so a knocked-back player still can't get shoved out of bounds. Without
    // this, the push collider could snag on terrain/other players mid-slide (cutting the knockback short
    // unpredictably) or shove other players/enemies around involuntarily while sliding through them.
    public void Knockback(Vector3 impulse)
    {
        if (_rigidbody == null || _stats == null || _stats.IsDead) return;

        Interrupt();

        if (_physicsCollider != null) _physicsCollider.enabled = false;

        // Guarded by !isKinematic — no player script sets this rigidbody kinematic today, but
        // PredictedRigidbody's velocity setter throws ("not supported") if ever attempted on a kinematic
        // body, same reason every enemy-side velocity write (AddKnockbackVelocity included) guards itself
        // the same way. Cheap defensive parity in case a future ability/effect ever kinematic-freezes a
        // player (e.g. a grab-style enemy attack).
        if (!_rigidbody.isKinematic)
            _rigidbody.velocity = impulse;

        float speed = impulse.magnitude;
        float stunDuration = speed > knockbackStunThreshold
            ? Mathf.Log(speed / knockbackStunThreshold) / knockbackStunDrag
            : 0f;
        ApplyStun(stunDuration);
    }
}
