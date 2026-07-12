using System;
using PurrNet;
using UnityEngine;

public class DaggerGloom : EnemyBase
{
    private static readonly int DaggerWindupHash = Animator.StringToHash("DaggerWindup");
    private static readonly int DaggerSpinHash = Animator.StringToHash("DaggerSpin");
    private static readonly int DaggerPostSpinHash = Animator.StringToHash("DaggerPostSpin");
    private static readonly int WindupSpeedHash = Animator.StringToHash("WindupSpeed");
    private static readonly int SpinSpeedHash = Animator.StringToHash("SpinSpeed");
    private static readonly int PostSpinSpeedHash = Animator.StringToHash("PostSpinSpeed");
    private float _postSpinClipLength;

    [Header("Dagger Settings")]
    [SerializeField] private Collider spinHitbox;
    public float spinDamageMultiplier = 1f;
    [SerializeField] private float windupDuration = 0.5f;
    [SerializeField] private float spinDuration = 0.1f;
    [SerializeField] private float betweenSpinPause = 0.3f;
    [SerializeField] private float postSpinPause = 0.6f;
    [SerializeField] private float attackCooldown = 3f;
    [SerializeField] private int attacksPerAttack = 1;
    [SerializeField] private float dashSpeedMultiplier = 15f;
    [SerializeField] private GameObject windupIndicator;
    [SerializeField] private WindupFill windupFill;

    public enum State { Moving, Windup, Spinning, BetweenSpins, PostSpin, DashingToTarget }
    private float _stateTimer = float.MaxValue;
    private float _attackCooldownTimer = float.MaxValue;
    private int _attacksRemaining;
    private State _state = State.Moving;

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

    protected override void OnSpawned()
    {
        base.OnSpawned();
        foreach (var clip in _animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == "Dagger Gloom Windup")
                _animator.SetFloat(WindupSpeedHash, clip.length / windupDuration);
            else if (clip.name == "Dagger Gloom Spin")
                _animator.SetFloat(SpinSpeedHash, clip.length / spinDuration);
            else if (clip.name == "Dagger Gloom PostSpin")
            {
                _postSpinClipLength = clip.length;
                _animator.SetFloat(PostSpinSpeedHash, clip.length / postSpinPause);
            }
        }
        _attacksRemaining = attacksPerAttack;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, attackCooldown);
        OnStateChanged(State.Moving);
    }

    private void OnStateChanged(State state)
    {
        spinHitbox.enabled = (state == State.Spinning);
        _physicsCollider.enabled = (state == State.Moving || state == State.PostSpin || state == State.DashingToTarget);
        windupIndicator.SetActive(state == State.Windup);
        if (windupFill != null)
        {
            if (state == State.Windup) windupFill.StartFill(windupDuration);
            else windupFill.ResetFill();
        }
        if (state == State.Windup)
            _animator.Play(DaggerWindupHash, 0, 0f);
        else if (state == State.Spinning)
            _animator.Play(DaggerSpinHash, 0, 0f);
        else if (state == State.BetweenSpins)
        {
            _animator.SetFloat(PostSpinSpeedHash, _postSpinClipLength / betweenSpinPause);
            _animator.Play(DaggerPostSpinHash, 0, 0f);
        }
        else if (state == State.PostSpin)
        {
            _animator.SetFloat(PostSpinSpeedHash, _postSpinClipLength / postSpinPause);
            _animator.Play(DaggerPostSpinHash, 0, 0f);
        }
    }

    protected override void Update()
    {
        if (_state == State.Moving)
            base.Update();
        if (!isServer || _isDead || IsStunned) return;

        _stateTimer -= Time.deltaTime;
        _attackCooldownTimer -= Time.deltaTime;

        switch (_state)
        {
            case State.Moving:
                if (_attackCooldownTimer <= 0f && _target != null)
                {
                    float dist = Vector3.Distance(transform.position, _target.position);
                    if (dist <= preferredRange * 2f)
                    {
                        isStunnable = false;
                        if (dist > preferredRange / 2f)
                            EnterDashToTarget();
                        else
                            EnterWindup();
                    }
                }
                break;

            case State.Windup:
                if (_stateTimer <= 0f)
                    EnterSpin();
                break;

            case State.Spinning:
                if (_stateTimer <= 0f)
                {
                    if (_attacksRemaining > 0)
                        EnterBetweenSpins();
                    else
                        EnterPostSpin();
                }
                break;

            case State.BetweenSpins:
                if (_stateTimer <= 0f)
                {
                    if (_target != null && Vector3.Distance(transform.position, _target.position) > preferredRange / 2f)
                        EnterDashToTarget();
                    else
                        EnterWindup();
                }
                break;

            case State.DashingToTarget:
                break;

            case State.PostSpin:
                if (_stateTimer <= 0f)
                    EnterMoving();
                break;
        }
    }

    protected override void FixedUpdate()
    {
        if (_state == State.Moving)
            base.FixedUpdate();
        else if (_state == State.PostSpin && isServer)
        {
            if (_stateTimer <= postSpinPause * 0.5f)
                FaceTargetSmooth();
        }
        else if (_state == State.BetweenSpins && isServer)
        {
            if (_stateTimer <= betweenSpinPause * 0.5f)
                FaceTargetSmooth();
        }
    }

    private void EnterWindup()
    {
        _stateTimer = windupDuration;
        SnapFaceTarget();
        SetState(State.Windup);
    }

    private void EnterSpin()
    {
        SetState(State.Spinning);
        _stateTimer = spinDuration;
        _attacksRemaining--;
    }

    private void EnterBetweenSpins()
    {
        SetState(State.BetweenSpins);
        _stateTimer = betweenSpinPause;
    }

    private void EnterDashToTarget()
    {
        SetState(State.DashingToTarget);
        StartCoroutine(DashToTargetThenWindup());
    }

    private System.Collections.IEnumerator DashToTargetThenWindup()
    {
        float speed = baseMoveSpeed * dashSpeedMultiplier;
        yield return StartCoroutine(DashBehindTarget(speed, preferredRange / 4f));
        if (!_isDead) EnterWindup();
    }

    private void EnterPostSpin()
    {
        isStunnable = !stunResistant;
        SetState(State.PostSpin);
        _stateTimer = postSpinPause;
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, 0.2f * attackCooldown);
    }

    private void EnterMoving()
    {
        SetState(State.Moving);
        _attacksRemaining = attacksPerAttack;
    }

    protected override void OnHideOnDeath()
    {
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null) windupFill.ResetFill();
    }

    protected override void OnPoolReset()
    {
        base.OnPoolReset();
        _state = State.Moving;
        _attacksRemaining = attacksPerAttack;
        if (spinHitbox != null) spinHitbox.enabled = false;
        if (windupIndicator != null) windupIndicator.SetActive(false);
        if (windupFill != null) windupFill.ResetFill();
    }
}
