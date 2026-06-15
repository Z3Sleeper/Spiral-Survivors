using System;
using PurrNet;
using UnityEngine;

public class SpearEnemy : EnemyBase
{
    private static readonly int SpearWindupHash = Animator.StringToHash("SpearWindup");
    private static readonly int SpearStabHash = Animator.StringToHash("SpearStab");
    private static readonly int SpearPostStabHash = Animator.StringToHash("SpearPostStab");
    private static readonly int WindupSpeedHash = Animator.StringToHash("WindupSpeed");
    private static readonly int StabSpeedHash = Animator.StringToHash("StabSpeed");
    private static readonly int PostStabSpeedHash = Animator.StringToHash("PostStabSpeed");
    private Animator _animator;
    private float _postStabClipLength;

    [Header("Spear Settings")]
    [SerializeField] private Collider spearHitbox;
    public float stabDamageMultiplier = 1f;
    [SerializeField] private float windupDuration = 0.5f;
    [SerializeField] private float stabDuration = 0.1f;
    [SerializeField] private float betweenStabPause = 0.2f;
    [SerializeField] private float postStabPause = 0.5f;
    [SerializeField] private float attackCooldown = 5f;
    [SerializeField] private int attacksPerAttack = 1;
    [SerializeField] private GameObject dangerIndicator;

    public enum State { Moving, Windup, Stabbing, BetweenStabs, PostStab }
    private float _stateTimer;
    private float _attackCooldownTimer;
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
        TryGetComponent(out _animator);
        foreach (var clip in _animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == "Spear Enemy Windup")
                _animator.SetFloat(WindupSpeedHash, clip.length / windupDuration);
            else if (clip.name == "Spear Enemy Stab")
                _animator.SetFloat(StabSpeedHash, clip.length / stabDuration);
            else if (clip.name == "Spear Enemy PostStab")
            {
                _postStabClipLength = clip.length;
                _animator.SetFloat(PostStabSpeedHash, clip.length / postStabPause);
            }
        }
        _attacksRemaining = attacksPerAttack;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, attackCooldown);
        _syncState.onChanged += OnStateChanged;
        OnStateChanged(_syncState.value);
    }

    private void OnStateChanged(State state)
    {
        spearHitbox.enabled = (state == State.Stabbing);
        dangerIndicator.SetActive(state == State.Windup);
        if (state == State.Windup)
            _animator.Play(SpearWindupHash, 0, 0f);
        else if (state == State.Stabbing)
            _animator.Play(SpearStabHash, 0, 0f);
        else if (state == State.BetweenStabs)
        {
            _animator.SetFloat(PostStabSpeedHash, _postStabClipLength / betweenStabPause);
            _animator.Play(SpearPostStabHash, 0, 0f);
        }
        else if (state == State.PostStab)
        {
            _animator.SetFloat(PostStabSpeedHash, _postStabClipLength / postStabPause);
            _animator.Play(SpearPostStabHash, 0, 0f);
        }
    }

    protected override void Update()
    {
        if (_state == State.Moving)
            base.Update(); // only find targets while moving
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
                {
                    if (_attacksRemaining > 0)
                        EnterBetweenStabs();
                    else
                        EnterPostStab();
                }
                break;

            case State.BetweenStabs:
                if (_stateTimer <= 0f)
                    EnterWindup();
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

    private void EnterWindup()
    {
        _stateTimer = windupDuration;
        SnapFaceTarget();
        _state = State.Windup;
    }

    private void EnterStab()
    {
        _state = State.Stabbing;
        _stateTimer = stabDuration;
        _attacksRemaining--;
    }

    private void EnterBetweenStabs()
    {
        _state = State.BetweenStabs;
        _stateTimer = betweenStabPause;
    }

    private void EnterPostStab()
    {
        _state = State.PostStab;
        _stateTimer = postStabPause;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, 0.2f * attackCooldown);
    }

    private void EnterMoving()
    {
        _state = State.Moving;
        _attacksRemaining = attacksPerAttack;
    }
}