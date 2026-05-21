using PurrNet;
using UnityEngine;

public class PlayerController : NetworkIdentity
{
    private InputSystem_Actions _input;
    private CharacterStats _stats;
    private Rigidbody _rb;

    private Vector3 _lastMoveDir = Vector3.forward;
    private int _currentCharges;
    private float _chargeRegenTimer;
    private bool _isDashing;
    private float _dashTimer;
    private Collider _hurtbox;

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
        _dashTimer = _stats.dashDistance / _stats.dashSpeed;
        _hurtbox.enabled = false;

        _rb.linearVelocity = Vector3.zero;
        _rb.AddForce(_lastMoveDir * _stats.dashSpeed, ForceMode.VelocityChange);
    }

    private void EndDash()
    {
        _isDashing = false;
        _hurtbox.enabled = true;
        _rb.linearVelocity = Vector3.zero;
    }
}