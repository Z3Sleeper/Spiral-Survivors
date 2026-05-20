using PurrNet;
using Unity.VisualScripting;
using UnityEngine;

public class PlayerMovement : NetworkIdentity
{
    private InputSystem_Actions _input;

    private CharacterStats _stats;

    private Rigidbody _rb;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    protected override void OnDespawned(bool asServer)
    {
        _input?.Disable();
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
    }

    private void FixedUpdate()
    {
        if (_input == null) return;
        
        Vector2 move = _input.Player.Move.ReadValue<Vector2>();
        Vector3 moveDir = new Vector3(move.x, 0, move.y).normalized;
        _rb.AddForce(moveDir * _stats.moveSpeed, ForceMode.VelocityChange);
    }
}