using System.Collections;
using PurrNet;
using UnityEngine;

public class BlitzEnemy : EnemyBase, IEnemyAttack
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
    private float _attackCooldownTimer;
    private Vector3 _blitzDirection;
    private Vector3 _blitzEndPoint;
    private int _blitzesRemaining = 0;

    [HideInInspector] public SyncVar<State> _syncState = new(State.Moving);
    private State _state
    {
        get => _syncState.value;
        set => _syncState.value = value;
    }

    public float GetDamage() => _state == State.Blitzing ? damage : 0f;

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _syncState.onChanged += OnStateChanged;
        OnStateChanged(_syncState.value);
        _blitzesRemaining = blitzesPerAttack;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, attackCooldown);
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
                Vector3 toEnd = _blitzEndPoint - transform.position;
                if (toEnd.magnitude < 0.5f || Vector3.Dot(toEnd, _blitzDirection) <= 0f)
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
        _stateTimer = windupDuration;
        _rb.linearVelocity = Vector3.zero;
        _physicsCollider.enabled = true;

        // Snap to face target
        if (_target != null)
        {
            Vector3 dir = _target.position - transform.position;
            dir.y = 0;
            if (dir != Vector3.zero)
            {
                _blitzDirection = dir.normalized;
                _rb.MoveRotation(Quaternion.LookRotation(_blitzDirection));
            }
        }
        
        _state = State.Windup;
    }

    private void EnterBlitz()
    {
        _state = State.Blitzing;
        _blitzEndPoint = transform.position + _blitzDirection * blitzDistance;
        _physicsCollider.enabled = false;
        _rb.linearVelocity = Vector3.zero;
        _blitzesRemaining--;
    }

    private void EnterPostBlitz()
    {
        _state = State.PostBlitz;
        _stateTimer = postBlitzPause;
        _physicsCollider.enabled = true;
        _rb.linearVelocity = Vector3.zero;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, 0.2f * attackCooldown);
    }

    private void EnterMoving()
    {
        _state = State.Moving;
        _blitzesRemaining = blitzesPerAttack;
    }
}