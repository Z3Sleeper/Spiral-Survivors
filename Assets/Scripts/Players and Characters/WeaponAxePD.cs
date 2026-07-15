using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Orbits its owner exactly like WeaponMeleePD, but sweeps two zones per tick instead of one: a narrow
// inner zone (the handle) and a much wider outer zone (the axehead). Only the axehead can crit and deals
// full damage; the handle deals reduced damage and never crits. If an enemy overlaps both zones in the
// same tick, it counts as an outer hit (outer wins the tie-break) — ported from the old WeaponAxe.cs.
public class WeaponAxePD : WeaponBasePD<WeaponAxePD.State>
{
    public struct State : IPredictedData<State>
    {
        public float angle;
        public bool  prevSet;
        public void Dispose() { }
    }

    [Header("Zone Half-Extents (normalized, multiplied by weapon scale)")]
    [SerializeField] private float innerXExtent = 0.1f;
    [SerializeField] private float outerXExtent = 0.375f;
    [SerializeField] private float yExtent = 0.1f;

    [SerializeField] [Range(0f, 1f)] private float handleRatio = 0.6f;
    [SerializeField] private float outerXRangeScaleRatio = 0.75f;
    [SerializeField] private Transform outerBladeVisual;
    // Rescales outerBladeVisual's local width to visually match the outer zone's actual extent as
    // WeaponRange changes — purely cosmetic, weapon-specific, not a character stat (unlike the old
    // system, where this lived on CharacterStats).
    [SerializeField] private bool outerXScalesWithRange = false;

    [Header("Handle Damage")]
    [SerializeField] private float innerDamageMultiplier = 0.5f;

    private bool _initialized;
    private float _baseOuterBladeLocalScaleX;

    private readonly RaycastHit[] _innerSweep   = new RaycastHit[16];
    private readonly RaycastHit[] _outerSweep   = new RaycastHit[16];
    private readonly Collider[]   _innerOverlap = new Collider[16];
    private readonly Collider[]   _outerOverlap = new Collider[16];

    private readonly Dictionary<IEnemyPD, float> _hitCooldowns     = new();
    private readonly List<IEnemyPD>               _cooldownBuffer   = new();
    private readonly HashSet<IEnemyPD>            _touchedThisTick  = new();
    private readonly HashSet<IEnemyPD>            _outerHitsThisTick = new();
    private readonly List<(IEnemyPD enemy, float dmg, bool isCrit)> _hitFeedbackQueue = new();

    private float HitCooldown => _stats != null ? 135f / _stats.WeaponSpeed : 0.25f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (outerBladeVisual != null) _baseOuterBladeLocalScaleX = outerBladeVisual.localScale.x;
    }

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

    protected override void LateSimulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        Vector3 origin = _stats.transform.position;
        Vector3 orbitPos = OrbitalPosition(state.angle, origin);
        Vector3 outward  = orbitPos - origin;
        if (outward == Vector3.zero) return;

        Transform hb = hitboxTarget != null ? hitboxTarget : transform;

        // Compute previous-tick zone geometry BEFORE moving — hb still reflects last tick's transform
        // here, same trick WeaponMeleePD.LateSimulate() uses for its single hitbox, just applied to all
        // three zone-defining points (no extra State needed; PredictedTransform guarantees this read is
        // accurate across rollback).
        Vector3 prevInnerCenter = default, prevOuterPoint1 = default, prevOuterPoint2 = default;
        if (state.prevSet)
        {
            var prevZones = ComputeZones(hb);
            prevInnerCenter = prevZones.innerCenter;
            prevOuterPoint1 = prevZones.outerPoint1;
            prevOuterPoint2 = prevZones.outerPoint2;
        }

        transform.SetPositionAndRotation(orbitPos, Quaternion.LookRotation(outward));

        var zones = ComputeZones(hb);

        _touchedThisTick.Clear();
        _outerHitsThisTick.Clear();

        if (state.prevSet)
        {
            Vector3 innerDelta = zones.innerCenter - prevInnerCenter;
            float   id = innerDelta.magnitude;
            if (id > 0.001f)
            {
                int c = Physics.BoxCastNonAlloc(prevInnerCenter, zones.innerExt, innerDelta / id,
                    _innerSweep, zones.rot, id, CombatLayerMasks.EnemyHurtboxMask);
                for (int i = 0; i < c; i++)
                {
                    var e = _innerSweep[i].collider.GetComponent<IEnemyPD>();
                    if (e != null) _touchedThisTick.Add(e);
                }
            }

            Vector3 p1Delta = zones.outerPoint1 - prevOuterPoint1;
            float   od = p1Delta.magnitude;
            if (od > 0.001f)
            {
                int c = Physics.CapsuleCastNonAlloc(prevOuterPoint1, prevOuterPoint2, zones.capsuleRadius,
                    p1Delta / od, _outerSweep, od, CombatLayerMasks.EnemyHurtboxMask);
                for (int i = 0; i < c; i++)
                {
                    var e = _outerSweep[i].collider.GetComponent<IEnemyPD>();
                    if (e != null) { _touchedThisTick.Add(e); _outerHitsThisTick.Add(e); }
                }
            }
        }

        int ic = Physics.OverlapBoxNonAlloc(zones.innerCenter, zones.innerExt, _innerOverlap, zones.rot, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < ic; i++)
        {
            var e = _innerOverlap[i].GetComponent<IEnemyPD>();
            if (e != null) _touchedThisTick.Add(e);
        }

        int oc = Physics.OverlapCapsuleNonAlloc(zones.outerPoint1, zones.outerPoint2, zones.capsuleRadius, _outerOverlap, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < oc; i++)
        {
            var e = _outerOverlap[i].GetComponent<IEnemyPD>();
            if (e != null) { _touchedThisTick.Add(e); _outerHitsThisTick.Add(e); }
        }

        // Same "must separate then re-contact to re-hit" gate as WeaponMeleePD — see WeaponBasePD.IsFreshHit().
        // An enemy overlapping both zones this tick counts as an outer hit (outer wins the tie-break).
        foreach (var enemy in _touchedThisTick)
        {
            if (!IsFreshHit(enemy, _hitCooldowns)) continue;
            _hitCooldowns[enemy] = HitCooldown;
            Hit(enemy, _outerHitsThisTick.Contains(enemy));
        }
        CommitOverlapFrame(_touchedThisTick);

        state.prevSet = true;
    }

    // Recomputes both zones from the hitbox transform's CURRENT state — called twice per tick (once
    // before moving, for the previous-tick points; once after, for this tick's) and once per gizmo draw.
    private (Vector3 innerCenter, Vector3 innerExt, Vector3 outerPoint1, Vector3 outerPoint2, float capsuleRadius, Quaternion rot) ComputeZones(Transform hb)
    {
        Quaternion rot = hb.rotation;
        float fullRange = hb.lossyScale.z;
        float handleHZ  = fullRange * handleRatio * 0.5f;
        float bladeHZ   = fullRange * (1f - handleRatio) * 0.5f;
        float sx = hb.lossyScale.x;
        float sy = hb.lossyScale.y;

        Vector3 innerCenter = hb.position - hb.forward * bladeHZ;
        Vector3 outerCenter = hb.position + hb.forward * handleHZ;
        float outerX = outerXScalesWithRange
            ? outerXExtent * Mathf.Lerp(sx, fullRange, outerXRangeScaleRatio)
            : outerXExtent * sx;
        float capsuleRadius     = bladeHZ;
        float capsuleHalfHeight = Mathf.Max(0f, outerX - capsuleRadius);
        Vector3 outerPoint1 = outerCenter + hb.right * capsuleHalfHeight;
        Vector3 outerPoint2 = outerCenter - hb.right * capsuleHalfHeight;
        float innerX = xRangeScaleRatio > 0f
            ? innerXExtent * Mathf.Lerp(sx, fullRange, xRangeScaleRatio)
            : innerXExtent * sx;
        Vector3 innerExt = new(innerX, yExtent * sy, handleHZ);

        return (innerCenter, innerExt, outerPoint1, outerPoint2, capsuleRadius, rot);
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

        // Purely cosmetic (outer blade visual width) — render rate, not simulation, since it has no
        // gameplay effect and doesn't need to be replay-consistent.
        if (outerXScalesWithRange && outerBladeVisual != null && _stats != null)
            UpdateOuterBladeVisual();

        // Fire hit feedback immediately at render rate — bypasses state propagation delay.
        if (_stats != null)
        {
            foreach (var (enemy, dmg, isCrit) in _hitFeedbackQueue)
                enemy.TriggerHitFeedback(dmg, isCrit, _stats);
        }
        _hitFeedbackQueue.Clear();
    }

    private void UpdateOuterBladeVisual()
    {
        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        float fullRange = _stats.WeaponRange;
        float sx = hb.lossyScale.x;
        float outerX = outerXExtent * Mathf.Lerp(sx, fullRange, outerXRangeScaleRatio);
        float baseOuter = outerXExtent * sx;
        float hitboxRatio = baseOuter > 0f ? outerX / baseOuter : 1f;
        // rangeScaleTarget's X is already multiplied by parentXRatio via ScaleModel — divide it out.
        float parentXRatio = xRangeScaleRatio > 0f
            ? Mathf.Lerp(1f, fullRange / Mathf.Max(0.001f, designOrbit * transform.localScale.z), xRangeScaleRatio)
            : 1f;
        float localRatio = parentXRatio > 0.001f ? hitboxRatio / parentXRatio : hitboxRatio;
        var vs = outerBladeVisual.localScale;
        outerBladeVisual.localScale = new Vector3(_baseOuterBladeLocalScaleX * localRatio, vs.y, vs.z);
    }

    // ── Hit ───────────────────────────────────────────────────────────────────

    private void Hit(IEnemyPD enemy, bool isOuter)
    {
        if (enemy._isDead) return;
        float baseDamage = isOuter ? _stats.WeaponDamage : _stats.WeaponDamage * innerDamageMultiplier;
        float damage = RollDamage(baseDamage);
        bool  isCrit = isOuter && GameRandom.instance.NextFloat() < _stats.CritChance;
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
        var zones = ComputeZones(hb);

        Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(zones.innerCenter, zones.rot, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, zones.innerExt * 2f);
        Gizmos.matrix = prev;

        Gizmos.color = new Color(1f, 0f, 0f, 0.4f);
        Gizmos.DrawWireSphere(zones.outerPoint1, zones.capsuleRadius);
        Gizmos.DrawWireSphere(zones.outerPoint2, zones.capsuleRadius);
        Gizmos.DrawLine(zones.outerPoint1 + hb.up * zones.capsuleRadius,      zones.outerPoint2 + hb.up * zones.capsuleRadius);
        Gizmos.DrawLine(zones.outerPoint1 - hb.up * zones.capsuleRadius,      zones.outerPoint2 - hb.up * zones.capsuleRadius);
        Gizmos.DrawLine(zones.outerPoint1 + hb.forward * zones.capsuleRadius, zones.outerPoint2 + hb.forward * zones.capsuleRadius);
        Gizmos.DrawLine(zones.outerPoint1 - hb.forward * zones.capsuleRadius, zones.outerPoint2 - hb.forward * zones.capsuleRadius);
    }
}
