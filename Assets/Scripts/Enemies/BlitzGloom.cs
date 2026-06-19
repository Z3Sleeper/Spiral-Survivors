using System.Collections;
using PurrNet;
using UnityEngine;

public class BlitzGloom : EnemyBase
{
    [Header("Blitz Settings")]
    [SerializeField] private float windupDuration = 0.7f;
    [SerializeField] private float postBlitzPause = 0.5f;
    [SerializeField] private float attackCooldown = 5f;
    [SerializeField] private float blitzDistance = 10f;
    [SerializeField] private float blitzSpeed = 20f;
    [SerializeField] private int blitzesPerAttack = 1;
    [SerializeField] private GameObject windupIndicator;
    [SerializeField] private WindupFill windupFill;

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

    [HideInInspector] public SyncVar<Vector3> _syncBlitzDirection = new(Vector3.forward);

    public override bool CanContactDamage => _state == State.Blitzing;


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
        if (windupIndicator != null) windupIndicator.SetActive(state == State.Windup);
        if (windupFill != null)
        {
            if (state == State.Windup) windupFill.StartFill(windupDuration);
            else windupFill.ResetFill();
        }
    }

    protected override void Update()
    {
        base.Update();

        if (_state == State.Windup && windupIndicator != null && windupIndicator.activeSelf)
            windupIndicator.transform.rotation = Quaternion.LookRotation(_syncBlitzDirection.value);

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
        if (_isDead) return;
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

        var snapDir = SnapFaceTarget();
        if (snapDir != Vector3.zero)
        {
            _blitzDirection = snapDir;
            _syncBlitzDirection.value = snapDir;
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