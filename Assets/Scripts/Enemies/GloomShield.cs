using PurrNet;
using UnityEngine;

public class GloomShield : NetworkIdentity, IStrikeable
{
    [Header("Shield Settings")]
    [SerializeField] private float shieldOffset = 1.5f;
    [SerializeField] private float angleOffset = 0f;
    [SerializeField] private float windupDuration = 2f;
    [SerializeField] private float explosionDuration = 0.1f;
    [SerializeField] private float rotationSpeed = 90f;
    [SerializeField] private float reactionCooldown = 5f;
    public float explosionMultiplier = 3f;
    [SerializeField] private Collider hitbox;
    [SerializeField] private Collider strikeableCollider;
    [SerializeField] private Renderer[] shieldRenderers;
    [SerializeField] private GameObject windupIndicator;
    [SerializeField] private WindupFill windupFill;
    [SerializeField] private Color windupColor = Color.red;

    private Color _originalColor;

    public enum State { Idle, Windup, Exploding, Cooldown }

    private State _state = State.Idle;
    private Vector3 _facingDir = Vector3.forward;
    private void SetState(State state)
    {
        _state = state;
        OnStateChanged(state);
        SyncStateRpc(state);
    }

    [ObserversRpc]
    private void SyncStateRpc(State state)
    {
        if (isServer) return;
        _state = state;
        OnStateChanged(state);
    }
    private float _stateTimer = float.MaxValue;
    private EnemyBase _ownerEnemy;
    private ShieldParent _shieldParent;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _ownerEnemy = GetComponentInParent<EnemyBase>();
        _shieldParent = GetComponentInParent<ShieldParent>();
        if (shieldRenderers != null && shieldRenderers.Length > 0) _originalColor = shieldRenderers[0].material.color;
        OnStateChanged(State.Idle);
    }

    private void OnStateChanged(State state)
    {
        if (hitbox != null) hitbox.enabled = state == State.Exploding;
        if (windupIndicator != null) windupIndicator.SetActive(state == State.Windup);
        if (windupFill != null)
        {
            if (state == State.Windup) windupFill.StartFill(windupDuration);
            else windupFill.ResetFill();
        }
        if (shieldRenderers != null)
        {
            Color color = (state == State.Idle || state == State.Windup) ? windupColor : _originalColor;
            foreach (var r in shieldRenderers)
                if (r != null) r.material.color = color;
        }
    }

    [ObserversRpc]
    public void HideOnDeathRpc()
    {
        foreach (var col in GetComponentsInChildren<Collider>())
            col.enabled = false;
        foreach (var r in GetComponentsInChildren<Renderer>())
            r.enabled = false;
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null) windupFill.ResetFill();
    }

    private void Update()
    {
        if (_ownerEnemy == null) return;

        if (_facingDir != Vector3.zero)
        {
            Vector3 offsetDir = Quaternion.Euler(0f, angleOffset, 0f) * _facingDir;
            transform.position = _ownerEnemy.transform.position + offsetDir * shieldOffset;
            transform.rotation = Quaternion.LookRotation(offsetDir);
        }

        if (!isServer || _ownerEnemy._isDead) return;

        _stateTimer -= Time.deltaTime;

        switch (_state)
        {
            case State.Idle:
                UpdateFacingDir();
                break;
            case State.Windup:
                if (_stateTimer <= 0f) EnterExploding();
                break;
            case State.Exploding:
                if (_stateTimer <= 0f) EnterCooldown();
                break;
            case State.Cooldown:
                UpdateFacingDir();
                if (_stateTimer <= 0f) EnterIdle();
                break;
        }
    }

    private void UpdateFacingDir()
    {
        var target = _ownerEnemy.CurrentTarget;
        if (target == null) return;
        Vector3 dir = target.position - _ownerEnemy.transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude == 0f) return;
        _facingDir = Vector3.RotateTowards(
            _facingDir, dir.normalized,
            rotationSpeed * Mathf.Deg2Rad * Time.deltaTime, 0f);
        SyncFacingDirRpc(_facingDir);
    }

    [ObserversRpc]
    private void SyncFacingDirRpc(Vector3 dir)
    {
        if (isServer) return;
        _facingDir = dir;
    }

    public void OnStruck()
    {
        TriggerReactionRpc();
    }

    [ServerRpc(requireOwnership: false)]
    private void TriggerReactionRpc()
    {
        if (_state != State.Idle) return;
        EnterWindup();
    }

    public void TriggerWindup()
    {
        if (!isServer || _state != State.Idle) return;
        EnterWindup();
    }

    private void EnterWindup()
    {
        _stateTimer = windupDuration;
        SetState(State.Windup);
        _shieldParent?.OnShieldTriggered(this);
    }

    private void EnterExploding()
    {
        _stateTimer = explosionDuration;
        SetState(State.Exploding);
    }

    private void EnterCooldown()
    {
        _stateTimer = reactionCooldown;
        SetState(State.Cooldown);
    }

    private void EnterIdle()
    {
        SetState(State.Idle);
    }

    protected override void OnPoolReset()
    {
        base.OnPoolReset();
        ResetState();
    }

    public void ResetState()
    {
        _state = State.Idle;
        _stateTimer = float.MaxValue;
        _facingDir = Vector3.forward;
        if (hitbox != null) hitbox.enabled = false;
        if (strikeableCollider != null) strikeableCollider.enabled = true;
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null) windupFill.ResetFill();
        foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = true;
        foreach (var col in GetComponentsInChildren<Collider>()) col.enabled = true;
        if (shieldRenderers != null)
            foreach (var r in shieldRenderers)
                if (r != null) r.material.color = windupColor;
    }
}
