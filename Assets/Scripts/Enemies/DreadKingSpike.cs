using System.Collections;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class DreadKingSpike : BossBase
{
    [Header("Damage")]
    [SerializeField] private float chargeDamage = 20f;
    [SerializeField] private float aoeDamage = 30f;
    [SerializeField] private float bombDamage = 15f;

    [Header("Charge")]
    [SerializeField] private float chargeWindupDuration = 0.7f;
    [SerializeField] private float chargeSpeed = 18f;
    [SerializeField] private float chargeDistance = 15f;
    [SerializeField] private float attackRange = 14f;
    [SerializeField] private float attackCooldown = 8f;
    [SerializeField] private int chargesPerAttack = 2;
    [SerializeField] private float betweenChargePause = 0.5f;
    [SerializeField] private GameObject chargeDangerIndicator;

    [Header("AoE")]
    [SerializeField] private float aoeWindupDuration = 1.5f;
    [SerializeField] private float aoeRadius = 8f;
    [SerializeField] private GameObject aoeDangerIndicator;
    [SerializeField] private GameObject aoeWindupIndicator;

    [Header("Bombs")]
    [SerializeField] private GameObject bombDangerIndicatorPrefab;
    [SerializeField] private GameObject bombWindupIndicatorPrefab;
    [SerializeField] private float bombOffsetRadius = 1f;
    [SerializeField] private float bombWarningDuration = 0.5f;
    [SerializeField] private float bombRadius = 3f;
    [SerializeField] private int bombWaves = 5;
    [SerializeField] private float bombInterval = 1f;
    [SerializeField] private float bombInitialDelay = 1f;

    public enum State { Moving, ChargeWindup, Charging, BetweenCharges, AoeWindup }

    [HideInInspector] public SyncVar<State> _syncState = new(State.Moving);
    private State _state
    {
        get => _syncState.value;
        set => _syncState.value = value;
    }

    [HideInInspector] public SyncVar<Vector3> _syncChargeDirection = new(Vector3.forward);

    public override float GetDamage() => _state == State.Charging ? chargeDamage : damage;

    private float _stateTimer;
    private float _attackCooldownTimer = float.MaxValue;
    private Vector3 _chargeDirection;
    private Vector3 _chargeEndPoint;
    private float _aoeWindupElapsed;
    private int _chargesRemaining;
    private float _preSurgeMoveSpeed;
    private bool _isSurging;
    private readonly List<GameObject> _activeBombIndicators = new();

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _syncState.onChanged += OnStateChanged;
        OnStateChanged(_syncState.value);
        _attackCooldownTimer = attackCooldown + UnityEngine.Random.Range(0f, attackCooldown);

        if (aoeDangerIndicator != null)
            aoeDangerIndicator.SetActive(false);
        if (aoeWindupIndicator != null)
            aoeWindupIndicator.SetActive(false);
        if (chargeDangerIndicator != null)
            chargeDangerIndicator.SetActive(false);
    }

    private void OnStateChanged(State state)
    {
        if (chargeDangerIndicator != null)
            chargeDangerIndicator.SetActive(state == State.ChargeWindup);

        if (aoeDangerIndicator != null)
        {
            aoeDangerIndicator.SetActive(state == State.AoeWindup);
            if (state == State.AoeWindup)
            {
                _aoeWindupElapsed = 0f;
                var s = aoeDangerIndicator.transform.localScale;
                aoeDangerIndicator.transform.localScale = new Vector3(0f, s.y, 0f);
            }
        }
        if (aoeWindupIndicator != null)
            aoeWindupIndicator.SetActive(state == State.AoeWindup);
    }

    protected override void Update()
    {
        base.Update();

        if (_state == State.ChargeWindup && chargeDangerIndicator != null && chargeDangerIndicator.activeSelf)
            chargeDangerIndicator.transform.rotation = Quaternion.LookRotation(_syncChargeDirection.value);

        if (_state == State.AoeWindup && aoeDangerIndicator != null && aoeDangerIndicator.activeSelf)
        {
            _aoeWindupElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_aoeWindupElapsed / aoeWindupDuration);
            float diameter = Mathf.Lerp(0f, aoeRadius * 2f, t);
            var s = aoeDangerIndicator.transform.localScale;
            aoeDangerIndicator.transform.localScale = new Vector3(diameter, s.y, diameter);
        }

        if (!isServer || _isDead) return;

        _stateTimer -= Time.deltaTime;
        _attackCooldownTimer -= Time.deltaTime;

        switch (_state)
        {
            case State.Moving:
                if (_attackCooldownTimer <= 0f && !_isSurging)
                {
                    _preSurgeMoveSpeed = moveSpeed;
                    moveSpeed = _preSurgeMoveSpeed * 5f;
                    _isSurging = true;
                }
                if (_attackCooldownTimer <= 0f && _target != null &&
                    Vector3.Distance(transform.position, _target.position) <= attackRange)
                {
                    _chargesRemaining = chargesPerAttack;
                    EnterChargeWindup();
                }
                break;

            case State.ChargeWindup:
                if (_stateTimer <= 0f)
                    EnterCharging();
                break;

            case State.Charging:
                Vector3 toEnd = _chargeEndPoint - transform.position;
                if (toEnd.magnitude < 0.5f || Vector3.Dot(toEnd, _chargeDirection) <= 0f)
                {
                    _chargesRemaining--;
                    if (_chargesRemaining > 0)
                        EnterBetweenCharges();
                    else
                        EnterAoeWindup();
                }
                break;

            case State.BetweenCharges:
                if (_stateTimer <= 0f)
                    EnterChargeWindup();
                break;

            case State.AoeWindup:
                if (_stateTimer <= 0f)
                    FireAoe();
                break;
        }
    }

    protected override void FixedUpdate()
    {
        if (_isDead) return;
        if (_state == State.Moving)
            base.FixedUpdate();
        else if (_state == State.Charging)
            _rb.linearVelocity = new Vector3(_chargeDirection.x * chargeSpeed, _rb.linearVelocity.y, _chargeDirection.z * chargeSpeed);
        else if (_state == State.ChargeWindup || _state == State.BetweenCharges || _state == State.AoeWindup)
            _rb.linearVelocity = Vector3.zero;
    }

    private void EnterChargeWindup()
    {
        if (_isSurging)
        {
            moveSpeed = _preSurgeMoveSpeed;
            _isSurging = false;
        }
        _rb.linearVelocity = Vector3.zero;
        var snapDir = SnapFaceTarget();
        if (snapDir != Vector3.zero)
        {
            _chargeDirection = snapDir;
            _syncChargeDirection.value = snapDir;
        }
        _stateTimer = chargeWindupDuration;
        _state = State.ChargeWindup;
    }

    private void EnterCharging()
    {
        _chargeEndPoint = transform.position + _chargeDirection * chargeDistance;
        _physicsCollider.enabled = false;
        _rb.linearVelocity = Vector3.zero;
        _state = State.Charging;
    }

    private void EnterBetweenCharges()
    {
        _physicsCollider.enabled = true;
        _rb.linearVelocity = Vector3.zero;
        _stateTimer = betweenChargePause;
        _state = State.BetweenCharges;
    }

    private void EnterAoeWindup()
    {
        _physicsCollider.enabled = true;
        _rb.linearVelocity = Vector3.zero;
        _stateTimer = aoeWindupDuration;
        _state = State.AoeWindup;
    }

    private void FireAoe()
    {
        TriggerAoeRpc(transform.position, aoeRadius, aoeDamage);

        _attackCooldownTimer = attackCooldown;
        _state = State.Moving;

        StartCoroutine(BombCoroutine());
    }

    [ObserversRpc]
    private void TriggerAoeRpc(Vector3 position, float radius, float dmg)
    {
        foreach (var stats in CharacterStats.All)
        {
            if (!stats.isOwner || stats.IsDead || stats.IsInvincible) continue;
            if (Vector3.Distance(stats.transform.position, position) <= radius)
                stats.TakeDamageRpc(dmg);
        }
    }

    private IEnumerator BombCoroutine()
    {
        yield return new WaitForSeconds(bombInitialDelay);

        for (int i = 0; i < bombWaves; i++)
        {
            if (_isDead) yield break;

            foreach (var player in CharacterStats.All)
            {
                if (player.IsDead) continue;
                Vector2 offset2D = UnityEngine.Random.insideUnitCircle.normalized * bombOffsetRadius;
                Vector3 bombPos = player.transform.position + new Vector3(offset2D.x, 0f, offset2D.y);
                SpawnBombRpc(bombPos);
            }

            yield return new WaitForSeconds(bombInterval);
        }
    }

    [ObserversRpc]
    private void SpawnBombRpc(Vector3 position)
    {
        Vector3 pos = new Vector3(position.x, 0.05f, position.z);
        float diameter = bombRadius * 2f;

        if (bombDangerIndicatorPrefab != null)
        {
            var danger = Instantiate(bombDangerIndicatorPrefab, pos, Quaternion.identity);
            var s = danger.transform.localScale;
            danger.transform.localScale = new Vector3(0f, s.y, 0f);
            Destroy(danger, bombWarningDuration + 0.1f);
            _activeBombIndicators.Add(danger);
            StartCoroutine(GrowBombIndicator(danger, diameter));
        }

        if (bombWindupIndicatorPrefab != null)
        {
            var windup = Instantiate(bombWindupIndicatorPrefab, pos, Quaternion.identity);
            var s = windup.transform.localScale;
            windup.transform.localScale = new Vector3(diameter, s.y, diameter);
            Destroy(windup, bombWarningDuration + 0.1f);
            _activeBombIndicators.Add(windup);
        }

        StartCoroutine(BombDamageCoroutine(position));
    }

    private IEnumerator GrowBombIndicator(GameObject indicator, float targetDiameter)
    {
        float elapsed = 0f;
        while (elapsed < bombWarningDuration && indicator != null)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / bombWarningDuration);
            var s = indicator.transform.localScale;
            indicator.transform.localScale = new Vector3(Mathf.Lerp(0f, targetDiameter, t), s.y, Mathf.Lerp(0f, targetDiameter, t));
            yield return null;
        }
    }

    private IEnumerator BombDamageCoroutine(Vector3 position)
    {
        yield return new WaitForSeconds(bombWarningDuration);

        foreach (var stats in CharacterStats.All)
        {
            if (!stats.isOwner || stats.IsDead || stats.IsInvincible) continue;
            if (Vector3.Distance(stats.transform.position, position) <= bombRadius)
                stats.TakeDamageRpc(bombDamage);
        }
    }

    protected override void Die()
    {
        ClearBombIndicatorsRpc();
        base.Die();
    }

    [ObserversRpc]
    private void ClearBombIndicatorsRpc()
    {
        foreach (var indicator in _activeBombIndicators)
            if (indicator != null) Destroy(indicator);
        _activeBombIndicators.Clear();
    }

    protected override void OnEnrage()
    {
        attackCooldown = 1f;
        _attackCooldownTimer = Mathf.Min(_attackCooldownTimer, 1f);
    }
}
