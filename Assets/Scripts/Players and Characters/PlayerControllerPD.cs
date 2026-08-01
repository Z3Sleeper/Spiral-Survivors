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

    // Per-instance, not static — each player's dash events are its own, not a shared global channel.
    // Surge events live on CharacterStatsPD instead, since StartSurge()/TryEndSurge() are defined there.
    private PredictedEvent _onDashStarted;
    private PredictedEvent _onDashEnded;

    public void AddDashStartedListener(System.Action listener) => _onDashStarted.AddListener(listener);
    public void RemoveDashStartedListener(System.Action listener) => _onDashStarted.RemoveListener(listener);
    public void AddDashEndedListener(System.Action listener) => _onDashEnded.AddListener(listener);
    public void RemoveDashEndedListener(System.Action listener) => _onDashEnded.RemoveListener(listener);

    // Lets an ability that functions like a dash (e.g. RogueSpinAbilityPD's leap) hook into the same
    // OnDashStarted/OnDashEnded listeners a real dash already fires (DashExplosionAugment, DashHealAugment,
    // etc.), without StartSpecialMovement itself needing to know or assume anything about dash semantics —
    // it stays fully generic (see its own doc comment), so a future non-dash-like special movement can still
    // use it without silently also counting as a dash. The caller decides, by calling these explicitly
    // alongside its own Activate()/end-of-active logic.
    public void NotifyDashStarted() => _onDashStarted.Invoke();
    public void NotifyDashEnded() => _onDashEnded.Invoke();

    private bool _wasDeadLastFrame;
    private bool _pendingReadyPressed;
    private bool _pendingPausePressed;
    private bool _pendingPickPressed;
    private int  _pendingPickIndex;
    private bool _pendingEndScreenReadyPressed;
    private InputSystem_Actions _input;

    // Predicted, not verified — this only drives the local owner's own button label, and instant
    // feedback on your own click matters more than the extra tick of latency verifiedState would add.
    public bool IsReady => currentState.isReady;
    public bool IsDashing => currentState.isDashing;
    // True while dash or either ability slot is currently forcing a special action on this player — dash,
    // isSpecialMoving, or an ability whose IAbilityPD.ForcesSpecialAction is true and IsActive (e.g.
    // RogueSpinAbilityPD's leap). A non-locking ability (ForcesSpecialAction false, e.g.
    // RogueVanishAbilityPD — it only flips invisibility/movespeed, never touches movement) does NOT set this
    // while it's the one active. Read by _ability1/_ability2's own UI (LocalUIManagerPD/PlayerUIWorldSpace)
    // to show the on-cooldown BG even when the slot itself would otherwise read as ready, and matches the
    // exact same formula Simulate() gates starting a new dash/ability against — see that block's own doc
    // comment for the full three-rule spec this implements.
    public bool IsLockedOut => currentState.isDashing || currentState.isSpecialMoving
        || (_ability1 != null && _ability1.IsActive && _ability1.ForcesSpecialAction)
        || (_ability2 != null && _ability2.IsActive && _ability2.ForcesSpecialAction);

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
    public void RequestPick(int cardIndex)
    {
        _pendingPickPressed = true;
        _pendingPickIndex = cardIndex;
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
        if (GameManagerPD.instance == null || !GameManagerPD.instance.GameStarted) return;

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
            AugmentManagerPD.instance.SubmitPick(owner.Value, input.pickedCardIndex, input.pickMultiplier);

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

        // Charge regen runs even while dead so the player has dashes on revive.
        if (_stats != null && state.currentCharges < _stats.dashCharges)
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

        // "Locked out" — dash and either ability slot are the only three things that can force a special
        // action on this player, disrupting normal movement, and only ever one should run at a time.
        // isDashing/isSpecialMoving cover dash and any special-movement window (belt-and-suspenders
        // alongside the IAbilityPD check below — today only RogueSpinAbilityPD ever sets isSpecialMoving,
        // always in lockstep with ForcesSpecialAction, but this stays correct even if that ever changes). A
        // LOCKING ability (ForcesSpecialAction true, e.g. RogueSpinAbilityPD) being active locks everything
        // else out exactly like dashing does — dash, the other slot, and itself. A NON-locking one
        // (ForcesSpecialAction false, e.g. RogueVanishAbilityPD — it only flips invisibility/movespeed,
        // never touches movement) does NOT set this while it's the one active, so it never blocks anything
        // else. Same formula as the public IsLockedOut property (that one reads currentState instead of
        // this tick's own `state` ref, otherwise identical) — see its own doc comment for the exact rule spec.
        bool lockedOut = state.isDashing || state.isSpecialMoving
            || (_ability1 != null && _ability1.IsActive && _ability1.ForcesSpecialAction)
            || (_ability2 != null && _ability2.IsActive && _ability2.ForcesSpecialAction);

        if (input.dashPressed && state.currentCharges > 0 && !lockedOut)
            StartDash(ref state);

        // Ability1 input — PlayerControllerPD only gates/forwards; the ability component itself owns its
        // own cooldown state and decides what Activate()/NotifyActivationBlocked() actually do.
        if (input.ability1Pressed && _ability1 != null)
        {
            if (_ability1.IsReady && !lockedOut) _ability1.Activate();
            else _ability1.NotifyActivationBlocked();
        }

        // Ability2 input — same gate/forward split as Ability1 above.
        if (input.ability2Pressed && _ability2 != null)
        {
            if (_ability2.IsReady && !lockedOut) _ability2.Activate();
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

        if (state.isDashing)
            _rigidbody.velocity = state.dashVelocity;
        else if (state.isSpecialMoving)
            _rigidbody.velocity = state.specialVelocity;
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

        _stats.GrantInvincibility(state.dashTimer);
        if (_physicsCollider != null) _physicsCollider.enabled = false;
        _rigidbody.velocity = Vector3.zero;
        _onDashStarted.Invoke();
    }

    private void EndDash(ref State state)
    {
        state.isDashing = false;
        // Deliberately NOT cancelling invincibility here anymore — GrantInvincibility no longer supports an
        // explicit "turn it off" call from any one source (see its own doc comment). Dash's own contribution
        // just expires naturally via the invincibleTimer countdown; if some other source (e.g.
        // ShieldBreakBurstAugment) extended it further, that time is left intact instead of being wiped out
        // by the dash ending.
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        _rigidbody.velocity = Vector3.zero;
        _onDashEnded.Invoke();
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


}
