using PurrNet.Prediction;
using UnityEngine;

[RequireComponent(typeof(CharacterStatsPD))]
public class PDCharacterController : PredictedIdentity<PDCharacterController.Input, PDCharacterController.State>
{
    [SerializeField] private PredictedRigidbody _rigidbody;
    [SerializeField] private Transform _cameraTarget;

    private CharacterStatsPD _stats;
    private Collider _physicsCollider;

    private const float DashDistance = 8f;
    private const float DashSpeed    = 40f;

    public static event System.Action<PDCharacterController> OnDashStarted;
    public static event System.Action<PDCharacterController> OnDashEnded;
    public static event System.Action<PDCharacterController> OnSurgeStarted;
    public static event System.Action<PDCharacterController> OnSurgeEnded;

    private State _previousState;
    private bool _wasSurging;
    private bool _isReady;
    private InputSystem_Actions _input;

    public bool IsReady => _isReady;

    // ── Structs ────────────────────────────────────────────────────────────
    public struct Input : IPredictedData
    {
        public Vector2 direction;
        public bool dashPressed;
        public bool surgeHeld;
        public bool pausePressed;
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
        public void Dispose() { }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────
    private void Awake()
    {
        _stats = GetComponent<CharacterStatsPD>();
        var physicsChild = transform.Find("PhysicsCollider");
        if (physicsChild != null) _physicsCollider = physicsChild.GetComponent<Collider>();
    }

    protected override State GetInitialState() => new()
    {
        lastMoveDir  = Vector3.forward,
        currentCharges = _stats != null ? _stats.dashCharges : 1,
    };

    public Transform CameraTarget => _cameraTarget != null ? _cameraTarget : transform;

    protected override void LateAwake()
    {
        if (!isOwner) return;
        _input = new InputSystem_Actions();
    }

    protected override void OnDestroy()
    {
        _input?.Disable();
    }

    public void EnableInput() => _input?.Enable();

    public void ToggleReady()
    {
        _isReady = !_isReady;
        if (PausePD.instance != null && owner.HasValue)
            PausePD.instance.AddPendingReady(owner.Value, _isReady ? 1 : -1);
    }

    // ── Input ──────────────────────────────────────────────────────────────
    protected override void UpdateInput(ref Input input)
    {
        if (_input == null) return;
        input.dashPressed  |= _input.Player.Dash.WasPressedThisFrame();
        input.pausePressed |= _input.Player.Pause.WasPressedThisFrame();
    }

    protected override void GetFinalInput(ref Input input)
    {
        if (_input == null) return;
        input.direction = _input.Player.Move.ReadValue<Vector2>();
        input.surgeHeld = _input.Player.Surge.IsPressed();
    }

    // ── Simulation ─────────────────────────────────────────────────────────
    protected override void Simulate(Input input, ref State state, float delta)
    {
        if (input.pausePressed && PausePD.instance != null)
        {
            bool isSinglePlayer = predictionManager == null || predictionManager.players == null || predictionManager.players.players.Count <= 1;
            if (PausePD.instance.IsPaused && !isSinglePlayer && owner.HasValue)
            {
                _isReady = !_isReady;
                PausePD.instance.AddPendingReady(owner.Value, _isReady ? 1 : -1);
            }
            else
            {
                PausePD.instance.RequestToggle();
            }
        }
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        _isReady = false;

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

        if (_stats == null || _stats.IsDead) return;

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
            _rigidbody.AddForce(moveDir * (_stats.MoveSpeed * delta * 50f), ForceMode.VelocityChange);

        // TODO: SweepForOrbs()
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (viewState.isDashing && !_previousState.isDashing)
            OnDashStarted?.Invoke(this);
        else if (!viewState.isDashing && _previousState.isDashing)
            OnDashEnded?.Invoke(this);

        if (_stats != null)
        {
            bool nowSurging = _stats.IsSurging;
            if (nowSurging && !_wasSurging) OnSurgeStarted?.Invoke(this);
            else if (!nowSurging && _wasSurging) OnSurgeEnded?.Invoke(this);
            _wasSurging = nowSurging;
        }

        _previousState = viewState;
    }

    // ── Dash helpers ───────────────────────────────────────────────────────
    private void StartDash(ref State state)
    {
        float moveSpeedForDash = _stats.IsSurging ? _stats.MoveSpeed / _stats.SurgeMoveSpeedMulti : _stats.MoveSpeed;
        float scaledDashSpeed  = DashSpeed * ((4f + moveSpeedForDash) / 4f);

        state.isDashing    = true;
        state.currentCharges--;
        state.dashVelocity = state.lastMoveDir * scaledDashSpeed;
        state.dashTimer    = DashDistance / DashSpeed;

        if (state.chargeRegenTimer <= 0f)
            state.chargeRegenTimer = _stats.DashCooldown;

        _stats.SetInvincible(state.dashTimer);
        if (_physicsCollider != null) _physicsCollider.enabled = false;
        _rigidbody.velocity = Vector3.zero;
    }

    private void EndDash(ref State state)
    {
        state.isDashing = false;
        _stats.SetInvincible(0f);
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        _rigidbody.velocity = Vector3.zero;
    }


}
