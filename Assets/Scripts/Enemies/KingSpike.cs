using System.Collections;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class KingSpike : BossBase
{
    [Header("Damage")]
    [SerializeField] private float chargeDamageMultiplier = 1f;
    [SerializeField] private float aoeDamageMultiplier = 1.5f;
    [SerializeField] private float bombDamageMultiplier = 0.75f;

    [Header("Charge")]
    [SerializeField] private float chargeWindupDuration = 0.7f;
    [SerializeField] private float chargeSpeed = 18f;
    [SerializeField] private float chargeDistance = 15f;
    [SerializeField] private float attackRange = 14f;
    [SerializeField] private float attackCooldown = 8f;
    [SerializeField] private int chargesPerAttack = 2;
    [SerializeField] private float betweenChargePause = 0.5f;
    [SerializeField] private GameObject chargeWindupIndicator;
    [SerializeField] private WindupFill chargeWindupFill;

    [Header("AoE")]
    [SerializeField] private float aoeWindupDuration = 1.5f;
    [SerializeField] private float aoeRadius = 8f;
    [SerializeField] private GameObject aoeWindupIndicator;
    [SerializeField] private WindupFill aoeWindupFill;

    [Header("Bombs")]
    [SerializeField] private GameObject bombIndicatorPrefab;
    [SerializeField] private float bombOffsetRadius = 1f;
    [SerializeField] private float bombWarningDuration = 0.5f;
    [SerializeField] private float bombRadius = 3f;
    [SerializeField] private int bombWaves = 5;
    [SerializeField] private float bombInterval = 1f;
    [SerializeField] private float bombInitialDelay = 1f;

    public enum State { Moving, ChargeWindup, Charging, BetweenCharges, AoeWindup }

    private State _state = State.Moving;
    private void SetState(State state)
    {
        isStunnable = state == State.Moving ? !stunResistant : false;
        _state = state;
        OnStateChanged(state);
        SyncStateRpc(state, _chargeDirection);
    }

    [ObserversRpc]
    private void SyncStateRpc(State state, Vector3 chargeDir)
    {
        if (isServer) return;
        _chargeDirection = chargeDir;
        _state = state;
        OnStateChanged(state);
    }

    public override float GetDamage() => _state == State.Charging ? damage * chargeDamageMultiplier : damage;

    private float _stateTimer = float.MaxValue;
    private float _attackCooldownTimer = float.MaxValue;
    private Vector3 _chargeDirection;
    private Vector3 _chargeEndPoint;
    private int _chargesRemaining;
    private float _preSurgeMoveSpeed;
    private bool _isSurging;
    private readonly List<GameObject> _activeBombIndicators = new();

    protected override void OnSpawned()
    {
        base.OnSpawned();
        OnStateChanged(State.Moving);
        _attackCooldownTimer = attackCooldown * 0.5f;

        if (aoeWindupIndicator != null) aoeWindupIndicator.SetActive(false);
        if (chargeWindupIndicator != null) chargeWindupIndicator.SetActive(false);
    }

    private void OnStateChanged(State state)
    {
        if (chargeWindupIndicator != null) chargeWindupIndicator.SetActive(state == State.ChargeWindup);
        if (chargeWindupFill != null)
        {
            if (state == State.ChargeWindup) chargeWindupFill.StartFill(chargeWindupDuration);
            else chargeWindupFill.ResetFill();
        }

        if (aoeWindupIndicator != null) aoeWindupIndicator.SetActive(state == State.AoeWindup);
        if (aoeWindupFill != null)
        {
            if (state == State.AoeWindup) aoeWindupFill.StartFill(aoeWindupDuration);
            else aoeWindupFill.ResetFill();
        }
    }

    protected override void Update()
    {
        base.Update();

        if (_state == State.ChargeWindup && chargeWindupIndicator != null && chargeWindupIndicator.activeSelf)
            chargeWindupIndicator.transform.rotation = Quaternion.LookRotation(_chargeDirection);

        if (!isServer || _isDead || IsStunned) return;

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
        if (_isDead || IsStunned) return;
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
            _chargeDirection = snapDir;
        _stateTimer = chargeWindupDuration;
        SetState(State.ChargeWindup);
    }

    private void EnterCharging()
    {
        _chargeEndPoint = transform.position + _chargeDirection * chargeDistance;
        _physicsCollider.enabled = false;
        _rb.linearVelocity = Vector3.zero;
        SetState(State.Charging);
    }

    private void EnterBetweenCharges()
    {
        _physicsCollider.enabled = true;
        _rb.linearVelocity = Vector3.zero;
        _stateTimer = betweenChargePause;
        SetState(State.BetweenCharges);
    }

    private void EnterAoeWindup()
    {
        _physicsCollider.enabled = true;
        _rb.linearVelocity = Vector3.zero;
        _stateTimer = aoeWindupDuration;
        SetState(State.AoeWindup);
    }

    private void FireAoe()
    {
        TriggerAoeRpc(transform.position, aoeRadius, damage * aoeDamageMultiplier);

        _attackCooldownTimer = attackCooldown;
        SetState(State.Moving);

        StartCoroutine(BombCoroutine());
    }

    [ObserversRpc]
    private void TriggerAoeRpc(Vector3 position, float radius, float dmg)
    {
        foreach (var stats in CharacterStats.All.Values)
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

            foreach (var player in CharacterStats.All.Values)
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

        if (bombIndicatorPrefab != null)
        {
            var indicator = Instantiate(bombIndicatorPrefab, pos, Quaternion.identity);
            _activeBombIndicators.Add(indicator);
            var fill = indicator.GetComponentInChildren<WindupFill>();
            if (fill != null) fill.StartFill(bombWarningDuration);
            StartCoroutine(DestroyIndicatorAfterDelay(indicator, bombWarningDuration + 0.1f));
        }

        StartCoroutine(BombDamageCoroutine(position));
    }

    private IEnumerator DestroyIndicatorAfterDelay(GameObject indicator, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (indicator != null)
        {
            _activeBombIndicators.Remove(indicator);
            Destroy(indicator);
        }
    }

    private IEnumerator BombDamageCoroutine(Vector3 position)
    {
        yield return new WaitForSeconds(bombWarningDuration);

        foreach (var stats in CharacterStats.All.Values)
        {
            if (!stats.isOwner || stats.IsDead || stats.IsInvincible) continue;
            if (Vector3.Distance(stats.transform.position, position) <= bombRadius)
                stats.TakeDamageRpc(damage * bombDamageMultiplier);
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
        attackCooldown /= 2f;
        moveSpeed *= 1.5f;
        _attackCooldownTimer = Mathf.Min(_attackCooldownTimer, attackCooldown);
    }

    protected override void OnHideOnDeath()
    {
        if (chargeWindupIndicator != null) chargeWindupIndicator.SetActive(false);
        if (aoeWindupIndicator != null) aoeWindupIndicator.SetActive(false);
    }
}
