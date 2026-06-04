using System.Collections;
using PurrNet;
using UnityEngine;

public class BlitzEnemy : EnemyBase
{
    [Header("Blitz Settings")]
    [SerializeField] private float windupDuration = 0.7f;
    [SerializeField] private float postBlitzPause = 0.5f;
    [SerializeField] private float attackCooldown = 5f;
    [SerializeField] private float blitzDistance = 10f;
    [SerializeField] private float blitzSpeed = 20f;
    [SerializeField] private int blitzesPerAttack = 1;
    [SerializeField] private GameObject dangerIndicator;

    public enum State { Moving, Windup, Blitzing, PostBlitz }
    private float _stateTimer;
    private float _attackCooldownTimer = 3f;
    private Vector3 _blitzDirection;
    private Vector3 _blitzEndPoint;
    private int _blitzesRemaining = 0;

    [HideInInspector] public SyncVar<State> _syncState = new(State.Moving);
    private State _state
    {
        get => _syncState.value;
        set => _syncState.value = value;
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _syncState.onChanged += OnStateChanged;
        OnStateChanged(_syncState.value);
        hasContactDamage = false;
        _blitzesRemaining = blitzesPerAttack;
        _attackCooldownTimer = attackCooldown;
    }

    private void OnStateChanged(State state)
    {
        dangerIndicator.SetActive(state == State.Windup);
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
                    EnterBlitz();
                break;

            case State.Blitzing:
                if (Vector3.Distance(transform.position, _blitzEndPoint) < 0.5f)
                    if (_blitzesRemaining > 0)
                        EnterWindup();
                    else
                        EnterPostBlitz();
                break;

            case State.PostBlitz:
                if (_stateTimer <= 0f)
                    EnterMoving();
                break;
        }
    }

    protected override void FixedUpdate()
    {
        if (_state == State.Moving)
            base.FixedUpdate();

        if (_state == State.Blitzing)
        {
            Vector3 toEnd = _blitzEndPoint - transform.position;
            _rb.linearVelocity = new Vector3(
                _blitzDirection.x * blitzSpeed,
                _rb.linearVelocity.y,
                _blitzDirection.z * blitzSpeed
            );
        }
    }

    private void EnterWindup()
    {
        _state = State.Windup;
        _stateTimer = windupDuration;
        _rb.linearVelocity = Vector3.zero;
        hasContactDamage = false;
        _physicsCollider.enabled = true;

        if (_target != null)
        {
            Vector3 dir = _target.position - transform.position;
            dir.y = 0;
            if (dir != Vector3.zero)
            {
                _blitzDirection = dir.normalized;
                transform.rotation = Quaternion.LookRotation(_blitzDirection);
            }
        }
    }

    private void EnterBlitz()
    {
        _state = State.Blitzing;
        _blitzEndPoint = transform.position + _blitzDirection * blitzDistance;
        hasContactDamage = true;
        _physicsCollider.enabled = false;
        _rb.linearVelocity = Vector3.zero;
        _blitzesRemaining--;
    }

    private void EnterPostBlitz()
    {
        _state = State.PostBlitz;
        _stateTimer = postBlitzPause;
        hasContactDamage = false;
        _physicsCollider.enabled = true;
        _rb.linearVelocity = Vector3.zero;
        _attackCooldownTimer = attackCooldown;
    }

    private void EnterMoving()
    {
        _state = State.Moving;
        _blitzesRemaining = blitzesPerAttack;
    }
}