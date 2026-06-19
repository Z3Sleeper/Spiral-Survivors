using PurrNet;
using UnityEngine;

public class GloomShield : NetworkIdentity, IStrikeable
{
    [Header("Shield Settings")]
    [SerializeField] private float shieldOffset = 1.5f;
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

    private Color _originalColor;

    public enum State { Idle, Windup, Exploding, Cooldown }

    [HideInInspector] public SyncVar<State> _syncState = new(State.Idle);
    [HideInInspector] public SyncVar<Vector3> _syncFacingDir = new(Vector3.forward);

    private State _state { get => _syncState.value; set => _syncState.value = value; }
    private float _stateTimer;
    private EnemyBase _ownerEnemy;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _ownerEnemy = GetComponentInParent<EnemyBase>();
        if (shieldRenderers != null && shieldRenderers.Length > 0) _originalColor = shieldRenderers[0].material.color;
        _syncState.onChanged += OnStateChanged;
        OnStateChanged(_syncState.value);
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
            Color color = (state == State.Idle || state == State.Windup) ? Color.red : _originalColor;
            foreach (var r in shieldRenderers)
                if (r != null) r.material.color = color;
        }
    }

    private void Update()
    {
        if (_ownerEnemy == null) return;

        if (_syncFacingDir.value != Vector3.zero)
        {
            transform.position = _ownerEnemy.transform.position + _syncFacingDir.value * shieldOffset;
            transform.rotation = Quaternion.LookRotation(_syncFacingDir.value);
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
        _syncFacingDir.value = Vector3.RotateTowards(
            _syncFacingDir.value, dir.normalized,
            rotationSpeed * Mathf.Deg2Rad * Time.deltaTime, 0f);
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

    private void EnterWindup()
    {
        _stateTimer = windupDuration;
        _state = State.Windup;
    }

    private void EnterExploding()
    {
        _stateTimer = explosionDuration;
        _state = State.Exploding;
    }

    private void EnterCooldown()
    {
        _stateTimer = reactionCooldown;
        _state = State.Cooldown;
    }

    private void EnterIdle()
    {
        _state = State.Idle;
    }
}
