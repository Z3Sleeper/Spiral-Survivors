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

    private bool _initialized;

    private readonly RaycastHit[]                _sweepResults  = new RaycastHit[32];
    private readonly Collider[]                  _overlapResults = new Collider[32];
    private readonly Dictionary<IEnemyPD, float> _hitCooldowns  = new();
    private readonly List<IEnemyPD>                                  _cooldownBuffer   = new();
    private readonly List<(IEnemyPD enemy, float dmg, bool isCrit, bool isHighroll)> _hitFeedbackQueue = new();
    private readonly HashSet<IEnemyPD>                               _touchedThisTick  = new();

    private float HitCooldown => _stats != null ? 135f / _stats.WeaponSpeed : 0.25f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void LateAwake()
    {
        base.LateAwake();
        TryInit();
    }

    private void TryInit()
    {
        if (!owner.HasValue) return;
        if (!CharacterStatsPD.All.TryGetValue(owner.Value, out var stats)) return;
        var mgr = stats.GetComponent<WeaponManagerPD>();
        if (mgr == null) return;
        _manager = mgr;
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

        // half doesn't depend on position/rotation, only on scale (which a pure orbit move never changes),
        // so it's safe to compute once up front and reuse across every sub-arc segment below.
        float xRatio = xRangeScaleRatio > 0f
            ? Mathf.Lerp(1f, hb.lossyScale.z / Mathf.Max(0.001f, designOrbit * transform.localScale.z), xRangeScaleRatio)
            : 1f;
        Vector3 half = new(
            meleeHalfExtents.x * hb.lossyScale.x * xRatio,
            meleeHalfExtents.y * hb.lossyScale.y,
            meleeHalfExtents.z * hb.lossyScale.z);

        _touchedThisTick.Clear();
        Vector3    center;
        Quaternion hitboxRot;

        if (state.prevSet)
        {
            // A straight sweep from last tick's hitbox position to this tick's is a chord across the orbit
            // circle, not the arc itself — at high enough rotation speed that chord can cut past an enemy
            // sitting on the arc between the two endpoints entirely. Subdivide into intermediate stops
            // along the true arc when this tick's angle movement is large enough for that gap to matter
            // (see WeaponBasePD.ComputeSubArcCount()) — at normal speeds this is always exactly 1 segment,
            // identical to a plain single sweep straight to the final position.
            float angleDelta = _stats.WeaponSpeed * delta;
            float startAngle = state.angle + angleDelta; // angle before this tick's Simulate() decrement
            int   subArcs    = ComputeSubArcCount(angleDelta);

            Vector3    segStart  = prevCenter;
            Vector3    segCenter = prevCenter;
            Quaternion segRot    = hb.rotation;

            for (int step = 1; step <= subArcs; step++)
            {
                bool    isLastStep = step == subArcs;
                Vector3 stepPos = isLastStep
                    ? orbitPos
                    : OrbitalPosition(Mathf.Lerp(startAngle, state.angle, step / (float)subArcs), origin);
                Vector3 stepOutward = stepPos - origin;
                if (stepOutward == Vector3.zero) continue;

                transform.SetPositionAndRotation(stepPos, Quaternion.LookRotation(stepOutward));
                hb.GetPositionAndRotation(out segCenter, out segRot);

                Vector3 sweep = segCenter - segStart;
                float   dist  = sweep.magnitude;
                if (dist > 0.001f)
                {
                    int n = Physics.BoxCastNonAlloc(segStart, half, sweep / dist,
                        _sweepResults, segRot, dist, CombatLayerMasks.EnemyHurtboxMask);
                    for (int i = 0; i < n; i++)
                    {
                        var enemy = _sweepResults[i].collider.GetComponent<IEnemyPD>();
                        if (enemy != null) _touchedThisTick.Add(enemy);
                    }
                }

                segStart = segCenter;
            }

            center    = segCenter;
            hitboxRot = segRot;
        }
        else
        {
            transform.SetPositionAndRotation(orbitPos, Quaternion.LookRotation(outward));
            hb.GetPositionAndRotation(out center, out hitboxRot);
        }

        // Overlap at current position
        int oc = Physics.OverlapBoxNonAlloc(center, half, _overlapResults, hitboxRot, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < oc; i++)
        {
            var enemy = _overlapResults[i].GetComponent<IEnemyPD>();
            if (enemy != null) _touchedThisTick.Add(enemy);
        }

        // Only enemies that weren't already touching last tick AND aren't on cooldown register a hit —
        // an enemy that stays continuously overlapped needs to actually separate before it can be hit
        // again, cooldown expiring alone isn't enough. See WeaponBasePD.IsFreshHit().
        foreach (var enemy in _touchedThisTick)
        {
            if (!IsFreshHit(enemy, _hitCooldowns)) continue;
            _hitCooldowns[enemy] = HitCooldown;
            Hit(enemy);
        }
        CommitOverlapFrame(_touchedThisTick);

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
            foreach (var (enemy, dmg, isCrit, isHighroll) in _hitFeedbackQueue)
                enemy.TriggerHitFeedback(dmg, isCrit, isHighroll, _stats);
        }
        _hitFeedbackQueue.Clear();
    }

    // ── Hit ───────────────────────────────────────────────────────────────────

    private void Hit(IEnemyPD enemy)
    {
        if (enemy._isDead) return;
        float damage = RollDamage(_stats.WeaponDamage, out bool isHighroll);
        if (isHighroll) damage *= 1f + _stats.HighrollBonus;
        bool  isCrit = GameRandom.instance.NextFloat() < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage;
        if (isHighroll) _onHighroll.Invoke(damage);
        if (isCrit) _onCritStrike.Invoke(enemy.ObjectId);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
        Vector3 knockDir = (enemy.transform.position - _stats.transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback);
        _hitFeedbackQueue.Add((enemy, damage, isCrit, isHighroll));
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
