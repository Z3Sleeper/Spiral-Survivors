using System;
using PurrNet;
using UnityEngine;

public class SpearEnemy : EnemyBase
{
    private static readonly int SpearStabHash = Animator.StringToHash("SpearStab");
    private Animator _animator;

    [Header("Spear Settings")]
    [SerializeField] private Collider spearHitbox;
    public float stabDamageMultiplier = 1f;
    [SerializeField] private float windupDuration = 0.5f;
    [SerializeField] private float stabDuration = 0.1f;
    [SerializeField] private float postStabPause = 0.5f;
    [SerializeField] private float attackCooldown = 3f;
    [SerializeField] private GameObject dangerIndicator;

    public enum State { Moving, Windup, Stabbing, PostStab }
    private float _stateTimer;
    private float _attackCooldownTimer;
    [HideInInspector] public SyncVar<State> _syncState = new SyncVar<State>(State.Moving);
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
        if (!isServer) return;
        spearHitbox.enabled = false;
    }

    private void OnStateChanged(State state)
    {
        dangerIndicator.SetActive(state == State.Windup);
        if (state == State.Windup)
            _animator.Play(SpearStabHash, 0, 0f);
    }

    protected override void Awake()
    {
        base.Awake();
        TryGetComponent(out _animator);
    }

    protected override void Update()
    {
        base.Update(); // handles find target timer
        if (!isServer) return;

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
                    EnterStab();
                break;

            case State.Stabbing:
                if (_stateTimer <= 0f)
                    EnterPostStab();
                break;

            case State.PostStab:
                if (_stateTimer <= 0f)
                    EnterMoving();
                break;
        }
    }

    protected override void FixedUpdate()
    {
        if (_state == State.Moving)
            base.FixedUpdate(); // only move during Moving state
    }

    private bool TargetInRange()
    {
        if (_target == null) return false;
        return Vector3.Distance(transform.position, _target.position) <= preferredRange + rangeToleranceFar;
    }

    private void EnterWindup()
    {
        _state = State.Windup;
        _stateTimer = windupDuration;
        dangerIndicator.SetActive(true);
        _animator.Play(SpearStabHash, 0, 0f);

        // Snap to face target
        if (_target != null)
        {
            Vector3 dir = (_target.position - transform.position);
            dir.y = 0;
            if (dir != Vector3.zero)
                transform.rotation = Quaternion.LookRotation(dir);
        }
    }

    private void EnterStab()
    {
        _state = State.Stabbing;
        _stateTimer = stabDuration;
        dangerIndicator.SetActive(false);
        spearHitbox.enabled = true;
    }

    private void EnterPostStab()
    {
        _state = State.PostStab;
        _stateTimer = postStabPause;
        spearHitbox.enabled = false;
        _attackCooldownTimer = attackCooldown;
    }

    private void EnterMoving()
    {
        _state = State.Moving;
    }
}