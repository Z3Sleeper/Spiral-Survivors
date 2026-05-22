using PurrNet;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class PlayerController : NetworkIdentity
{
    private InputSystem_Actions _input;
    private CharacterStats _stats;
    private Rigidbody _rb;

    private Vector3 _lastMoveDir = Vector3.forward;
    [HideInInspector] public int _currentCharges;
    [HideInInspector] public float _chargeRegenTimer;
    private bool _isDashing;
    [HideInInspector] public float _dashTimer;
    private Collider _hurtbox;

    public bool isSurging { get; private set; }
    private float _surgeTickTimer;
    private const float SurgeTickInterval = 0.5f;
    private float _surgeMoveSpeedBonus;
    private float _surgeWeaponSpeedBonus;
    private float _surgeAttackSpeedBonus;
    private const float DashDistance = 8f;
    private const float DashSpeed = 80f;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _hurtbox = transform.Find("Hurtbox").GetComponent<Collider>();
    }

    protected override void OnDespawned(bool asServer)
    {
        _input?.Disable();
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

        _input = new InputSystem_Actions();
        _input.Enable();
        _stats = GetComponent<CharacterStats>();
        _currentCharges = _stats.dashCharges;
    }

    private void Update()
    {
        if (_input == null) return;

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
                _chargeRegenTimer = _currentCharges < _stats.dashCharges ? _stats.dashCooldown : 0f;
            }
        }

        // Surge input
        if (_input.Player.Surge.IsPressed() && !isSurging && _stats.currentEnergy >= _stats.surgeCost)
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
                _stats.currentEnergy -= _stats.surgeCost;
                if (_stats.currentEnergy <= 0f)
                {
                    _stats.currentEnergy = 0f;
                    EndSurge();
                }
            }
        }
        else
        {
            // Passive energy regen
            _stats.currentEnergy += _stats.energyRegen * Time.deltaTime;
            _stats.currentEnergy = Mathf.Min(_stats.currentEnergy, _stats.maxEnergy);
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
            _rb.AddForce(moveDir * _stats.moveSpeed, ForceMode.VelocityChange);
    }

    private void StartDash()
    {
        _isDashing = true;
        _currentCharges--;
        if (_chargeRegenTimer <= 0f)
            _chargeRegenTimer = _stats.dashCooldown;
        _dashTimer = DashDistance / DashSpeed;
        _hurtbox.enabled = false;

        _rb.linearVelocity = Vector3.zero;
        _rb.AddForce(_lastMoveDir * DashSpeed, ForceMode.VelocityChange);
    }

    private void EndDash()
    {
        _isDashing = false;
        _hurtbox.enabled = true;
        _rb.linearVelocity = Vector3.zero;
    }

    private void StartSurge()
    {
        isSurging = true;
        _stats.currentEnergy -= _stats.surgeCost;
        _surgeTickTimer = SurgeTickInterval;

        float effectiveness = _stats.surgeEffectiveness;
        _surgeMoveSpeedBonus = _stats.baseMoveSpeed * (_stats.surgeMoveSpeedMultiplier * effectiveness - 1f);
        _surgeWeaponSpeedBonus = _stats.baseWeaponSpeed * (_stats.surgeWeaponSpeedMultiplier * effectiveness - 1f);
        _surgeAttackSpeedBonus = _stats.baseAttackSpeed * (_stats.surgeAttackSpeedMultiplier * effectiveness - 1f);

        _stats.bonusMoveSpeed += _surgeMoveSpeedBonus;
        _stats.bonusWeaponSpeed += _surgeWeaponSpeedBonus;
        _stats.bonusAttackSpeed += _surgeAttackSpeedBonus;
    }

    private void EndSurge()
    {
        isSurging = false;
        _stats.bonusMoveSpeed -= _surgeMoveSpeedBonus;
        _stats.bonusWeaponSpeed -= _surgeWeaponSpeedBonus;
        _stats.bonusAttackSpeed -= _surgeAttackSpeedBonus;
    }
}