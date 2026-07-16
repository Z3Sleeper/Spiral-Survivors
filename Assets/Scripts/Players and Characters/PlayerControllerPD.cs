using PurrNet.Prediction;
using UnityEngine;

[RequireComponent(typeof(CharacterStatsPD))]
public class PlayerControllerPD : PredictedIdentity<PlayerControllerPD.Input, PlayerControllerPD.State>
{
    [SerializeField] private PredictedRigidbody _rigidbody;
    [SerializeField] private Transform _cameraTarget;

    private CharacterStatsPD _stats;
    private Collider _physicsCollider;

    // Distance is just an emergent property of speed and duration, not a separately-tuned input.
    private const float DashDuration        = 0.2f;
    private const float DashSpeedMultiplier = 5f;

    // Per-instance, not static — each player's dash events are its own, not a shared global channel.
    // Surge events live on CharacterStatsPD instead, since StartSurge()/TryEndSurge() are defined there.
    private PredictedEvent _onDashStarted;
    private PredictedEvent _onDashEnded;

    public void AddDashStartedListener(System.Action listener) => _onDashStarted.AddListener(listener);
    public void RemoveDashStartedListener(System.Action listener) => _onDashStarted.RemoveListener(listener);
    public void AddDashEndedListener(System.Action listener) => _onDashEnded.AddListener(listener);
    public void RemoveDashEndedListener(System.Action listener) => _onDashEnded.RemoveListener(listener);

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

    // ── Structs ────────────────────────────────────────────────────────────
    public struct Input : IPredictedData
    {
        public Vector2 direction;
        public bool dashPressed;
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
        public void Dispose() { }
    }

    public struct State : IPredictedData<State>
    {
        public Vector3 lastMoveDir;
        public bool isDashing;
        public float dashTimer;
        public Vector3 dashVelocity;
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
        var physicsChild = transform.Find("PhysicsCollider");
        if (physicsChild != null) _physicsCollider = physicsChild.GetComponent<Collider>();
        _orbLayer = LayerMask.GetMask("XPOrb");
        _lastKnownMaxCharges = _stats != null ? _stats.dashCharges : 1;
    }

    protected override State GetInitialState() => new()
    {
        lastMoveDir  = Vector3.forward,
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
        _input?.Disable();
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
            if (_rigidbody != null)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            return;
        }

        // Dash input
        if (input.dashPressed && state.currentCharges > 0 && !state.isDashing)
            StartDash(ref state);

        // Dash timer
        if (state.isDashing)
        {
            state.dashTimer -= delta;
            if (state.dashTimer <= 0f)
                EndDash(ref state);
        }

        // Surge
        if (input.surgeHeld && !_stats.IsSurging && _stats.CurrentEnergy >= _stats.SurgeActivationThreshhold)
            _stats.StartSurge();
        else if (!input.surgeHeld && _stats.IsSurging)
            _stats.TryEndSurge();

        // Movement
        Vector3 moveDir = new Vector3(input.direction.x, 0, input.direction.y).normalized;
        if (moveDir != Vector3.zero)
            state.lastMoveDir = moveDir;

        if (state.isDashing)
            _rigidbody.velocity = state.dashVelocity;
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
        float moveSpeedForDash = _stats.IsSurging ? _stats.MoveSpeed / _stats.SurgeMoveSpeedMulti : _stats.MoveSpeed;
        float scaledDashSpeed  = moveSpeedForDash * DashSpeedMultiplier;

        state.isDashing    = true;
        state.currentCharges--;
        state.dashVelocity = state.lastMoveDir * scaledDashSpeed;
        state.dashTimer    = DashDuration;

        if (state.chargeRegenTimer <= 0f)
            state.chargeRegenTimer = _stats.DashCooldown;

        _stats.SetInvincible(state.dashTimer);
        if (_physicsCollider != null) _physicsCollider.enabled = false;
        _rigidbody.velocity = Vector3.zero;
        _onDashStarted.Invoke();
    }

    private void EndDash(ref State state)
    {
        state.isDashing = false;
        _stats.SetInvincible(0f);
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        _rigidbody.velocity = Vector3.zero;
        _onDashEnded.Invoke();
    }


}
