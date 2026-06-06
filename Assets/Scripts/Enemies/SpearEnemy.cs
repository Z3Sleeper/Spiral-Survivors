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
    [SerializeField] private float attackCooldown = 5f;
    [SerializeField] private int attacksPerAttack = 1;
    [SerializeField] private GameObject dangerIndicator;

    public enum State { Moving, Windup, Stabbing, PostStab }
    private float _stateTimer;
    private float _attackCooldownTimer = 3f;
    private int _attacksRemaining;
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
        spearHitbox.enabled = false;
        TryGetComponent(out _animator);
        _attacksRemaining = attacksPerAttack;
    }

    private void OnStateChanged(State state)
    {
        dangerIndicator.SetActive(state == State.Windup);
        if (state == State.Windup)
            _animator.Play(SpearStabHash, 0, 0f);
    }

    protected override void Update()
    {
        base.Update(); // handles find target timer
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
                    EnterStab();
                break;

            case State.Stabbing:
                if (_stateTimer <= 0f)
                    EnterPostStab();
                break;

            case State.PostStab:
                if (_stateTimer <= 0f)
                    if (_attacksRemaining > 0)
                        EnterWindup();
                    else
                        EnterMoving();
                break;
        }
    }

    protected override void FixedUpdate()
    {
        if (_state == State.Moving)
            base.FixedUpdate(); // only move during Moving state
    }

    private void EnterWindup()
    {
        _stateTimer = windupDuration;
        spearHitbox.enabled = false;

        // Snap to face target
        if (_target != null)
        {
            Vector3 dir = (_target.position - transform.position);
            dir.y = 0;
            if (dir != Vector3.zero)
                transform.rotation = Quaternion.LookRotation(dir);
        }
        
        _state = State.Windup;
    }

    private void EnterStab()
    {
        _state = State.Stabbing;
        _stateTimer = stabDuration;
        spearHitbox.enabled = true;
        _attacksRemaining--;
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
        _attacksRemaining = attacksPerAttack;
    }
}