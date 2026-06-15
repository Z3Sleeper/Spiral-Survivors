using PurrNet;
using UnityEngine;

public class LeaperGloom : EnemyBase
{
    private static readonly int WindupHash = Animator.StringToHash("LeaperWindup");
    private static readonly int LeapHash = Animator.StringToHash("LeaperLeap");
    private static readonly int WindupSpeedHash = Animator.StringToHash("WindupSpeed");
    private static readonly int LeapSpeedHash = Animator.StringToHash("LeapSpeed");
    private Animator _animator;
    [Header("Leap Settings")]
    [SerializeField] private float windupDuration = 0.8f;
    [SerializeField] private float leapDuration = 0.5f;
    private const float leapHeight = 3f;
    [SerializeField] private float postLeapPause = 0.5f;
    [SerializeField] private float attackCooldown = 4f;
    [SerializeField] private Transform model;
    [SerializeField] private Collider aoeHitbox;
    [SerializeField] private GameObject dangerIndicator;
    [SerializeField] private float landingDuration = 0.1f;
    [SerializeField] private int leapsPerAttack = 1;

    public enum State { Moving, Windup, Leaping, Landing, PostLeap }
    private float _stateTimer;
    private float _attackCooldownTimer;
    private Vector3 _leapStart;
    private Vector3 _leapTarget;
    private int _leapsRemaining;
    private float _clientLeapElapsed;

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
        TryGetComponent(out _animator);
        foreach (var clip in _animator.runtimeAnimatorController.animationClips)
        {
            if (clip.name == "Leaper Gloom Windup")
                _animator.SetFloat(WindupSpeedHash, clip.length / windupDuration);
            else if (clip.name == "Leaper Gloom Leap")
                _animator.SetFloat(LeapSpeedHash, clip.length / leapDuration);
        }
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, attackCooldown);
        if (!isServer) return;
        _leapsRemaining = leapsPerAttack;
    }

    private void OnStateChanged(State state)
    {
        aoeHitbox.enabled = (state == State.Landing);
        dangerIndicator.SetActive(state == State.Windup || state == State.Leaping);
        if (state == State.Windup)
        {
            _animator.Play(WindupHash, 0, 0f);
            dangerIndicator.transform.SetParent(null);
            dangerIndicator.transform.position = new Vector3(_syncLeapTarget.value.x, 0.05f, _syncLeapTarget.value.z);
        }
        else if (state == State.Leaping)
        {
            _animator.Play(LeapHash, 0, 0f);
            dangerIndicator.transform.position = new Vector3(_syncLeapTarget.value.x, 0.05f, _syncLeapTarget.value.z);
            _clientLeapElapsed = 0f;
        }
        else
        {
            dangerIndicator.transform.SetParent(transform);
        }
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

            case State.Landing:
                if (_stateTimer <= 0f)
                    EnterPostLand();
                break;

            case State.PostLeap:
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

        if (_state == State.Leaping)
        {
            if (isServer)
            {
                float t = 1f - (_stateTimer / leapDuration);
                Vector3 flatPos = Vector3.Lerp(_leapStart, _leapTarget, t);
                _rb.MovePosition(flatPos);
                float arcY = Mathf.Sin(t * Mathf.PI) * leapHeight;
                model.localPosition = new Vector3(0f, 0.5f + arcY, 0f);
            }
            else
            {
                _clientLeapElapsed += Time.fixedDeltaTime;
                float t = Mathf.Clamp01(_clientLeapElapsed / leapDuration);
                float arcY = Mathf.Sin(t * Mathf.PI) * leapHeight;
                model.localPosition = new Vector3(0f, 0.5f + arcY, 0f);
            }
        }
    }

    private void EnterWindup()
    {
        if (_target == null) return;
        _leapTarget = _target.position;
        _syncLeapTarget.value = _leapTarget;

        SnapFaceTarget();
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

    }

    private void Land()
    {
        _rb.isKinematic = false;
        _physicsCollider.enabled = true;
        _hurtbox.enabled = true;
        transform.position = new Vector3(_leapTarget.x, 0f, _leapTarget.z);
        _rb.linearVelocity = Vector3.zero;
        model.localPosition = new Vector3(0f, 0.5f, 0f);

        _state = State.Landing;
        _stateTimer = landingDuration;

        dangerIndicator.SetActive(false);
    }

    private void EnterPostLand()
    {
        _leapsRemaining--;
        if (_leapsRemaining > 0)
            EnterWindup();
        else
        {
            _state = State.PostLeap;
            _stateTimer = postLeapPause;
            _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, 0.2f * attackCooldown);
        }
    }

    private void EnterMoving()
    {
        _state = State.Moving;
        _leapsRemaining = leapsPerAttack;
    }

    protected override void Die()
    {
        DestroyDangerIndicatorRpc();
        base.Die();
    }

    [ObserversRpc]
    private void DestroyDangerIndicatorRpc()
    {
        if (dangerIndicator != null)
            Destroy(dangerIndicator);
    }
}