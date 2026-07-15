using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

public class WeaponMeleePD : WeaponBasePD<WeaponMeleePD.State>
{
    public struct State : IPredictedData<State>
    {
        public float angle;
        public bool  prevSet;
        public void Dispose() { }
    }

    [SerializeField] private Vector3 meleeHalfExtents = new(0.1f, 0.1f, 0.5f);

    private int  _enemyHurtboxMask;
    private bool _initialized;

    private readonly RaycastHit[]                _sweepResults  = new RaycastHit[32];
    private readonly Collider[]                  _overlapResults = new Collider[32];
    private readonly Dictionary<IEnemyPD, float> _hitCooldowns  = new();
    private readonly List<IEnemyPD>                                  _cooldownBuffer   = new();
    private readonly List<(IEnemyPD enemy, float dmg, bool isCrit)> _hitFeedbackQueue = new();

    private float HitCooldown => _stats != null ? 135f / _stats.WeaponSpeed : 0.25f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void LateAwake()
    {
        TryInit();
    }

    private void TryInit()
    {
        if (!owner.HasValue) return;
        if (!CharacterStatsPD.All.TryGetValue(owner.Value, out var stats)) return;
        var mgr = stats.GetComponent<WeaponManagerPD>();
        if (mgr == null) return;
        _manager = mgr;
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        _initialized = true;
        mgr.RegisterWeapon(this);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_manager != null) _manager.UnregisterWeapon(this);
    }

    protected override State GetInitialState() => default;

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void Simulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        state.angle -= _stats.WeaponSpeed * delta;
    }

    // Runs after the framework's DoPhysicsPass() for this tick (Simulate() on every identity happens
    // first, then physics integrates, then LateSimulate() on every identity) — so _stats.transform.position
    // here already reflects the player's post-physics, current-tick position. Reading it in Simulate()
    // instead (which runs BEFORE the physics pass) is always one tick stale: invisible at normal speed,
    // but glaring during a dash or high move speed, where a single tick covers real distance.
    protected override void LateSimulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Compute orbital position and apply to transform.
        // PredictedTransform records these tick-positions and interpolates the Visuals child for rendering.
        Vector3 origin = _stats.transform.position;
        Vector3 orbitPos = OrbitalPosition(state.angle, origin);
        Vector3 outward  = orbitPos - origin;
        if (outward == Vector3.zero) return;

        Transform hb = hitboxTarget != null ? hitboxTarget : transform;

        // Record prev hitbox position BEFORE moving.
        // PredictedTransform keeps transform.position at the correct simulation-tick value across rollbacks,
        // so hb.position here is always the accurate previous-tick hitbox world position.
        Vector3 prevCenter = state.prevSet ? hb.position : orbitPos;

        transform.SetPositionAndRotation(orbitPos, Quaternion.LookRotation(outward));

        // After setting transform, hb.position is now the current hitbox world center.
        hb.GetPositionAndRotation(out Vector3 center, out Quaternion hitboxRot);

        float xRatio = xRangeScaleRatio > 0f
            ? Mathf.Lerp(1f, hb.lossyScale.z / Mathf.Max(0.001f, designOrbit * transform.localScale.z), xRangeScaleRatio)
            : 1f;
        Vector3 half = new(
            meleeHalfExtents.x * hb.lossyScale.x * xRatio,
            meleeHalfExtents.y * hb.lossyScale.y,
            meleeHalfExtents.z * hb.lossyScale.z);

        // Box sweep from previous tick position (catches enemies the weapon swept through)
        if (state.prevSet)
        {
            Vector3 sweep = center - prevCenter;
            float   dist  = sweep.magnitude;
            if (dist > 0.001f)
            {
                int n = Physics.BoxCastNonAlloc(prevCenter, half, sweep / dist,
                    _sweepResults, hitboxRot, dist, _enemyHurtboxMask);
                for (int i = 0; i < n; i++)
                {
                    var enemy = _sweepResults[i].collider.GetComponentInParent<IEnemyPD>();
                    if (enemy != null && !_hitCooldowns.ContainsKey(enemy))
                    {
                        _hitCooldowns[enemy] = HitCooldown;
                        Hit(enemy);
                    }
                }
            }
        }

        // Overlap at current position
        int oc = Physics.OverlapBoxNonAlloc(center, half, _overlapResults, hitboxRot, _enemyHurtboxMask);
        for (int i = 0; i < oc; i++)
        {
            var enemy = _overlapResults[i].GetComponentInParent<IEnemyPD>();
            if (enemy != null && !_hitCooldowns.ContainsKey(enemy))
            {
                _hitCooldowns[enemy] = HitCooldown;
                Hit(enemy);
            }
        }

        state.prevSet = true;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!_initialized) TryInit();

        // Tick cooldowns at render rate — not in Simulate, so resims can't expire them early.
        _cooldownBuffer.Clear();
        _cooldownBuffer.AddRange(_hitCooldowns.Keys);
        foreach (var e in _cooldownBuffer)
        {
            _hitCooldowns[e] -= Time.deltaTime;
            if (_hitCooldowns[e] <= 0f) _hitCooldowns.Remove(e);
        }

        // Fire hit feedback immediately at render rate — bypasses state propagation delay.
        if (_stats != null)
        {
            foreach (var (enemy, dmg, isCrit) in _hitFeedbackQueue)
                enemy.TriggerHitFeedback(dmg, isCrit, _stats);
        }
        _hitFeedbackQueue.Clear();
    }

    // ── Hit ───────────────────────────────────────────────────────────────────

    private void Hit(IEnemyPD enemy)
    {
        if (enemy._isDead) return;
        float damage = RollDamage(_stats.WeaponDamage);
        bool  isCrit = GameRandom.instance.NextFloat() < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage;
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
        Vector3 knockDir = (enemy.transform.position - _stats.transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback);
        _hitFeedbackQueue.Add((enemy, damage, isCrit));
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        float xRatio = (xRangeScaleRatio > 0f && designOrbit > 0f)
            ? Mathf.Lerp(1f, hb.lossyScale.z / Mathf.Max(0.001f, designOrbit * transform.localScale.z), xRangeScaleRatio)
            : 1f;
        Vector3 half = new(
            meleeHalfExtents.x * hb.lossyScale.x * xRatio,
            meleeHalfExtents.y * hb.lossyScale.y,
            meleeHalfExtents.z * hb.lossyScale.z);
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(hb.position, hb.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, half * 2f);
        Gizmos.matrix = prev;
    }
}
