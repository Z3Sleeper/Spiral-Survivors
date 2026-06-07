using PurrNet;
using UnityEngine;

public class LeaperEnemy : EnemyBase
{
    [Header("Leap Settings")]
    [SerializeField] private float windupDuration = 0.8f;
    [SerializeField] private float leapDuration = 0.5f;
    [SerializeField] private float leapHeight = 3f;
    [SerializeField] private float postLeapPause = 0.5f;
    [SerializeField] private float attackCooldown = 4f;
    [SerializeField] private Transform model;
    [SerializeField] private Collider aoeHitbox;
    [SerializeField] private GameObject dangerIndicator;
    [SerializeField] private float aoeActiveDuration = 0.15f;
    [SerializeField] private int leapsPerAttack = 1;

    public enum State { Moving, Windup, Leaping, PostLeap }
    private float _stateTimer;
    private float _attackCooldownTimer = 2f;
    private Vector3 _leapStart;
    private Vector3 _leapTarget;
    private int _leapsRemaining;

    [HideInInspector] public SyncVar<State> _syncState = new(State.Moving);
    private State _state
    {
        get => _syncState.value;
        set => _syncState.value = value;
    }

    [HideInInspector] public SyncVar<Vector3> _syncLeapTarget = new(Vector3.zero);

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _syncState.onChanged += OnStateChanged;
        _syncLeapTarget.onChanged += OnLeapTargetChanged;
        OnStateChanged(_syncState.value);
        if (!isServer) return;
        aoeHitbox.enabled = false;
        _leapsRemaining = leapsPerAttack;
    }

    private void OnStateChanged(State state)
    {
        dangerIndicator.SetActive(state == State.Windup || state == State.Leaping);
    }

    private void OnLeapTargetChanged(Vector3 target)
    {
        dangerIndicator.transform.position = new Vector3(target.x, 0.05f, target.z);
    }

    protected override void Update()
    {
        base.Update();
        if (!isServer) return;
        if (_isDead) return;

        _stateTimer -= Time.deltaTime;
        _attackCooldownTimer -= Time.deltaTime;

        switch (_state)
        {
            case State.Moving:
                if (TargetInRange() && _attackCooldownTimer <= 0f)
                    EnterWindup();
                break;

            case State.Windup:
                if (_stateTimer <= 0f)
                    EnterLeap();
                break;

            case State.Leaping:
                if (_stateTimer <= 0f)
                    Land();
                break;

            case State.PostLeap:
                if (_stateTimer <= 0f)
                    EnterMoving();
                break;
        }
    }

    protected override void FixedUpdate()
    {
        if (_state == State.Moving)
            base.FixedUpdate();

        if (_state == State.Leaping)
        {
            float t = 1f - (_stateTimer / leapDuration);
            Vector3 flatPos = Vector3.Lerp(_leapStart, _leapTarget, t);
            _rb.MovePosition(flatPos);
            float arcY = Mathf.Sin(t * Mathf.PI) * leapHeight;
            model.localPosition = new Vector3(0f, 0.5f + arcY, 0f);
        }
    }

    private void EnterWindup()
    {
        if (_target == null) return;
        _leapTarget = _target.position;
        _syncLeapTarget.value = _leapTarget;

        _state = State.Windup;
        _stateTimer = windupDuration;
    }

    private void EnterLeap()
    {
        _leapStart = transform.position;
        _state = State.Leaping;
        _stateTimer = leapDuration;
        _rb.linearVelocity = Vector3.zero;
        _rb.isKinematic = true;
        _physicsCollider.enabled = false;
        _hurtbox.enabled = false;

        dangerIndicator.transform.SetParent(null);
    }

    private void Land()
    {
        _rb.isKinematic = false;
        _physicsCollider.enabled = true;
        _hurtbox.enabled = true;
        transform.position = new Vector3(_leapTarget.x, 0f, _leapTarget.z);
        _rb.linearVelocity = Vector3.zero;
        model.localPosition = new Vector3(0f, 0.5f, 0f);

        aoeHitbox.enabled = true;
        Invoke(nameof(DisableAoeHitbox), aoeActiveDuration);

        _leapsRemaining--;
        if (_leapsRemaining > 0)
            EnterWindup();
        else
        {
            _state = State.PostLeap;
            _stateTimer = postLeapPause;
            _attackCooldownTimer = attackCooldown;
        }

        dangerIndicator.transform.SetParent(null);
        dangerIndicator.SetActive(false);
    }

    private void DisableAoeHitbox()
    {
        aoeHitbox.enabled = false;
    }

    private void EnterMoving()
    {
        _state = State.Moving;
        _leapsRemaining = leapsPerAttack;
    }

    protected override void Die()
    {
        if (dangerIndicator != null)
            Destroy(dangerIndicator);
        base.Die();
    }
}