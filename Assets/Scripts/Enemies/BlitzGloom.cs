using System.Collections;
using PurrNet;
using UnityEngine;

public class BlitzGloom : EnemyBase
{
    [Header("Blitz Settings")]
    [SerializeField] private float windupDuration = 0.7f;
    [SerializeField] private float postBlitzPause = 0.5f;
    [SerializeField] private float attackCooldown = 5f;
    [SerializeField] private float blitzDistance = 15f;
    [SerializeField] private float blitzSpeedMultiplier = 25f;
    [SerializeField] private int blitzesPerAttack = 1;
    [SerializeField] private GameObject windupIndicator;
    [SerializeField] private WindupFill windupFill;

    public enum State { Moving, Windup, Blitzing, PostBlitz }
    private float _stateTimer = float.MaxValue;
    private float _attackCooldownTimer = float.MaxValue;
    private Vector3 _blitzDirection;
    private Vector3 _blitzEndPoint;
    private int _blitzesRemaining = 0;

    private State _state = State.Moving;
    private void SetState(State state)
    {
        isStunnable = state == State.Moving ? !stunResistant : false;
        _state = state;
        OnStateChanged(state);
        SyncStateRpc(state, _blitzDirection);
    }

    [ObserversRpc]
    private void SyncStateRpc(State state, Vector3 blitzDir)
    {
        if (isServer) return;
        _blitzDirection = blitzDir;
        _state = state;
        OnStateChanged(state);
    }

    public override bool CanContactDamage => _state == State.Blitzing;


    protected override void OnSpawned()
    {
        base.OnSpawned();
        OnStateChanged(State.Moving);
        _blitzesRemaining = blitzesPerAttack;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, attackCooldown);
    }

    private void OnStateChanged(State state)
    {
        if (state == State.Windup)
        {
            windupIndicator.transform.SetParent(null);
            windupIndicator.transform.position = new Vector3(transform.position.x, 0.05f, transform.position.z);
            windupIndicator.transform.rotation = Quaternion.LookRotation(_blitzDirection);
            windupIndicator.SetActive(true);
            if (windupFill != null) windupFill.StartFill(windupDuration);
        }
        else if (state != State.Blitzing)
        {
            windupIndicator.SetActive(false);
            windupIndicator.transform.SetParent(transform);
            if (windupFill != null) windupFill.ResetFill();
        }
    }

    protected override void Update()
    {
        base.Update();

        if (_state == State.Windup && windupIndicator != null && windupIndicator.activeSelf)
            windupIndicator.transform.rotation = Quaternion.LookRotation(_blitzDirection);

        if (!isServer || _isDead || IsStunned) return;

        _stateTimer -= Time.deltaTime;
        _attackCooldownTimer -= Time.deltaTime;

        switch (_state)
        {
            case State.Moving:
                if (TargetInRange() && _attackCooldownTimer <= 0f && !_specialRoutineActive)
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
        if (_isDead || IsStunned) return;
        if (_state == State.Moving)
            base.FixedUpdate();

        if (_state == State.Blitzing)
        {
            Vector3 toEnd = _blitzEndPoint - transform.position;
            float speed = baseMoveSpeed * blitzSpeedMultiplier;
            _rb.linearVelocity = new Vector3(
                _blitzDirection.x * speed,
                _rb.linearVelocity.y,
                _blitzDirection.z * speed
            );
        }
        else if (_state == State.PostBlitz && isServer)
            FaceTargetSmooth();
    }

    private void EnterWindup()
    {
        _stateTimer = windupDuration;
        _rb.linearVelocity = Vector3.zero;
        _physicsCollider.enabled = false;

        var snapDir = SnapFaceTarget();
        if (snapDir != Vector3.zero)
            _blitzDirection = snapDir;

        SetState(State.Windup);
    }

    private void EnterBlitz()
    {
        SetState(State.Blitzing);
        _blitzEndPoint = transform.position + _blitzDirection * blitzDistance;
        _physicsCollider.enabled = false;
        _rb.linearVelocity = Vector3.zero;
        _blitzesRemaining--;
    }

    private void EnterPostBlitz()
    {
        SetState(State.PostBlitz);
        _stateTimer = postBlitzPause;
        _physicsCollider.enabled = true;
        _rb.linearVelocity = Vector3.zero;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, 0.2f * attackCooldown);
    }

    private void EnterMoving()
    {
        SetState(State.Moving);
        _blitzesRemaining = blitzesPerAttack;
    }

    protected override void OnHideOnDeath()
    {
        if (windupIndicator != null) Destroy(windupIndicator);
    }
}