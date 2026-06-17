using PurrNet;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class PlayerController : NetworkIdentity
{
    private readonly Collider[] _orbHits = new Collider[50];
    private static int _orbLayer;
    private InputSystem_Actions _input;
    private CharacterStats _stats;
    private Rigidbody _rb;

    private Vector3 _lastMoveDir = Vector3.forward;
    [HideInInspector] public int _currentCharges;
    [HideInInspector] public float _chargeRegenTimer;
    private bool _isDashing;
    [HideInInspector] public float _dashTimer;
    private Collider _physicsCollider;

    public static event System.Action<PlayerController> OnDashStarted;

    public bool isSurging { get; private set; }
    private float _surgeTickTimer;
    private const float SurgeTickInterval = 0.1f;
    private float _surgeMoveSpeedBonus;
    private float _surgeWeaponSpeedBonus;
    private const float DashDistance = 8f;
    private const float DashSpeed = 80f;
    private float _sweepTimer;
    private const float SweepInterval = 0.2f;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _physicsCollider = transform.Find("PhysicsCollider").GetComponent<Collider>();
        _orbLayer = LayerMask.GetMask("XPOrb");
    }

    protected override void OnDespawned(bool asServer)
    {
        _input?.Disable();
        GameManager.OnPauseChanged -= OnPauseChanged;
    }

    private void OnEnable()
    {
        _input?.Enable();
    }

    private void OnDisable()
    {
        _input?.Disable();
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        enabled = isOwner;
        if (!isOwner) return;
        InitializeForOwner();
    }

    public void InitializeForOwner()
    {
        if (_input != null) return;
        _input = new InputSystem_Actions();
        _input.Enable();
        _stats = GetComponent<CharacterStats>();
        _currentCharges = _stats.dashCharges;
        GameManager.OnPauseChanged += OnPauseChanged;
        enabled = true;
    }

    private void Update()
    {
        if (_input == null) return;

        _sweepTimer -= Time.deltaTime;
        if (_sweepTimer <= 0f)
        {
            _sweepTimer = SweepInterval;
            SweepForOrbs();
        }

        // Pause input
        if (_input.Player.Pause.WasPressedThisFrame())
            GameManager.instance.RequestPauseRpc();

        // Dash input
        if (_input.Player.Dash.WasPressedThisFrame() && _currentCharges > 0 && !_isDashing)
            StartDash();

        // Dash timer
        if (_isDashing)
        {
            _dashTimer -= Time.deltaTime;
            if (_dashTimer <= 0f)
                EndDash();
        }

        // Charge regen
        if (_currentCharges < _stats.dashCharges)
        {
            _chargeRegenTimer -= Time.deltaTime;
            if (_chargeRegenTimer <= 0f)
            {
                _currentCharges++;
                _chargeRegenTimer = _currentCharges < _stats.dashCharges ? _stats.DashCooldown : 0f;
            }
        }

        // Surge input
        if (_input.Player.Surge.IsPressed() && !isSurging && _stats.CurrentEnergy >= _stats.SurgeActivationThreshhold)
            StartSurge();
        else if (!_input.Player.Surge.IsPressed() && isSurging)
            EndSurge();

        // Surge tick
        if (isSurging)
        {
            _surgeTickTimer -= Time.deltaTime;
            if (_surgeTickTimer <= 0f)
            {
                _surgeTickTimer = SurgeTickInterval;
                _stats.CurrentEnergy -= _stats.SurgeCost;
                if (_stats.CurrentEnergy <= 0f)
                {
                    _stats.CurrentEnergy = 0f;
                    EndSurge();
                }
            }
        }
        else
        {
            // Passive energy regen
            _stats.CurrentEnergy += _stats.EnergyRegen * Time.deltaTime;
            _stats.CurrentEnergy = Mathf.Min(_stats.CurrentEnergy, _stats.MaxEnergy);
        }
    }
    
    private void SweepForOrbs()
    {
        int count = Physics.OverlapSphereNonAlloc(transform.position, _stats.XPRadius, _orbHits, _orbLayer);
        for (int i = 0; i < count; i++)
        {
            var orb = _orbHits[i].GetComponent<XPOrb>();
            if (orb != null)
                orb.StartAttractingRPC(transform, owner ?? default);
        }
    }

    private void FixedUpdate()
    {
        if (_input == null) return;

        Vector2 move = _input.Player.Move.ReadValue<Vector2>();
        Vector3 moveDir = new Vector3(move.x, 0, move.y).normalized;

        if (moveDir != Vector3.zero)
            _lastMoveDir = moveDir;

        if (!_isDashing)
            _rb.AddForce(moveDir * _stats.MoveSpeed, ForceMode.VelocityChange);
    }

    private void OnPauseChanged(bool paused)
    {
        if (paused) _input.Disable();
        else _input.Enable();
    }

    private void StartDash()
    {
        OnDashStarted?.Invoke(this);
        _isDashing = true;
        _currentCharges--;
        if (_chargeRegenTimer <= 0f)
            _chargeRegenTimer = _stats.DashCooldown;
        _dashTimer = DashDistance / DashSpeed;
        _stats.invincible = true;
        _physicsCollider.enabled = false;

        _rb.linearVelocity = Vector3.zero;
        _rb.AddForce(_lastMoveDir * DashSpeed, ForceMode.VelocityChange);
    }

    private void EndDash()
    {
        _isDashing = false;
        _stats.invincible = false;
        _physicsCollider.enabled = true;
        _rb.linearVelocity = Vector3.zero;
    }

    private void StartSurge()
    {
        isSurging = true;
        _stats.CurrentEnergy -= _stats.SurgeCost;
        _surgeTickTimer = SurgeTickInterval;

        float effectiveness = _stats.SurgeEffectiveness;
        _surgeMoveSpeedBonus = _stats.SurgeMoveSpeedMulti * effectiveness;
        _surgeWeaponSpeedBonus = _stats.SurgeWeaponSpeedMultiplier * effectiveness;

        _stats.bonusMoveSpeedPercent *= _surgeMoveSpeedBonus;
        _stats.bonusWeaponSpeedPercent *= _surgeWeaponSpeedBonus;
    }

    private void EndSurge()
    {
        isSurging = false;
        _stats.bonusMoveSpeedPercent /= _surgeMoveSpeedBonus;
        _stats.bonusWeaponSpeedPercent /= _surgeWeaponSpeedBonus;
    }
}