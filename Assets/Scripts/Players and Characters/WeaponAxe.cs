using System.Collections.Generic;
using UnityEngine;

public class WeaponAxe : WeaponBase
{
    [Header("Zone Half-Extents (normalized, multiplied by weapon scale)")]
    [SerializeField] private float innerXExtent = 0.1f;
    [SerializeField] private float outerXExtent = 0.375f;
    [SerializeField] private float yExtent = 0.1f;

    [SerializeField] [Range(0f, 1f)] private float handleRatio = 0.6f;
    [SerializeField] private float outerXRangeScaleRatio = 0.75f;
    [SerializeField] private Transform outerBladeVisual;

    private ExecutionerHandler _executioner;
    private int _enemyHurtboxMask;
    private int _strikeableMask;
    private int _playerHurtboxMask;

    private readonly RaycastHit[] _innerSweep     = new RaycastHit[16];
    private readonly RaycastHit[] _outerSweep     = new RaycastHit[16];
    private readonly Collider[]   _innerOverlap   = new Collider[16];
    private readonly Collider[]   _outerOverlap   = new Collider[16];
    private readonly RaycastHit[] _strikeableSweep  = new RaycastHit[8];
    private readonly Collider[]   _strikeableOverlap = new Collider[8];
    private readonly RaycastHit[] _allySweep      = new RaycastHit[8];
    private readonly Collider[]   _allyOverlap    = new Collider[8];

    private HashSet<EnemyBase>     _currentOverlaps    = new();
    private HashSet<EnemyBase>     _newOverlaps        = new();
    private HashSet<EnemyBase>     _newOuterHits       = new();
    private HashSet<IStrikeable>   _currentStrikeables = new();
    private HashSet<IStrikeable>   _newStrikeables     = new();
    private HashSet<CharacterStats> _currentAllyOverlaps = new();
    private HashSet<CharacterStats> _newAllyOverlaps    = new();
    private readonly Dictionary<EnemyBase, float> _hitCooldowns = new();
    private readonly List<EnemyBase> _cooldownBuffer = new();
    private float HitCooldown => 90f / _stats.WeaponSpeed;

    private Vector3 _prevInnerCenter;
    private Vector3 _prevOuterPoint1;
    private Vector3 _prevOuterPoint2;
    private float _baseOuterBladeLocalScaleX = -1f;

    public override void Init(CharacterStats stats)
    {
        base.Init(stats);
        _executioner = stats.GetComponent<ExecutionerHandler>();
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        _enemyHurtboxMask  = LayerMask.GetMask("EnemyHurtbox");
        _strikeableMask    = LayerMask.GetMask("Strikeable");
        _playerHurtboxMask = LayerMask.GetMask("PlayerHurtbox");
        enabled = isOwner;
        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        float fullRange0 = hb.lossyScale.z;
        float handleHZ0  = fullRange0 * handleRatio * 0.5f;
        float bladeHZ0   = fullRange0 * (1f - handleRatio) * 0.5f;
        float sx0        = hb.lossyScale.x;
        float outerX0    = outerXExtent * sx0;
        float capsuleHalfHeight0 = Mathf.Max(0f, outerX0 - bladeHZ0);
        Vector3 outerCenter0 = hb.position + hb.forward * handleHZ0;
        _prevInnerCenter  = hb.position - hb.forward * bladeHZ0;
        _prevOuterPoint1  = outerCenter0 + hb.right * capsuleHalfHeight0;
        _prevOuterPoint2  = outerCenter0 - hb.right * capsuleHalfHeight0;
    }

    private void Update()
    {
        if (_stats == null || _stats.IsDead) return;

        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        float fullRange = hb.lossyScale.z;
        float handleHZ  = fullRange * handleRatio * 0.5f;
        float bladeHZ   = fullRange * (1f - handleRatio) * 0.5f;
        float sx = hb.lossyScale.x;
        float sy = hb.lossyScale.y;

        Vector3 innerCenter = hb.position - hb.forward * bladeHZ;
        Vector3 outerCenter = hb.position + hb.forward * handleHZ;
        float outerX = _stats.outerXScalesWithRange
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

        if (outerBladeVisual != null && _stats.outerXScalesWithRange)
        {
            if (_baseOuterBladeLocalScaleX < 0f)
                _baseOuterBladeLocalScaleX = outerBladeVisual.localScale.x;
            float baseOuter = outerXExtent * sx;
            float hitboxRatio = baseOuter > 0f ? outerX / baseOuter : 1f;
            // rangeScaleTarget's X is already multiplied by parentXRatio via ScaleModel — divide it out
            float parentXRatio = xRangeScaleRatio > 0f
                ? Mathf.Lerp(1f, fullRange / Mathf.Max(0.001f, designOrbit * transform.localScale.z), xRangeScaleRatio)
                : 1f;
            float localRatio = parentXRatio > 0.001f ? hitboxRatio / parentXRatio : hitboxRatio;
            var vs = outerBladeVisual.localScale;
            outerBladeVisual.localScale = new Vector3(_baseOuterBladeLocalScaleX * localRatio, vs.y, vs.z);
        }

        _newOverlaps.Clear();
        _newOuterHits.Clear();
        _newStrikeables.Clear();
        _newAllyOverlaps.Clear();

        _cooldownBuffer.Clear();
        _cooldownBuffer.AddRange(_hitCooldowns.Keys);
        foreach (var e in _cooldownBuffer)
            if (Time.time - _hitCooldowns[e] >= HitCooldown) _hitCooldowns.Remove(e);

        Vector3 innerDelta = innerCenter - _prevInnerCenter;
        Vector3 p1Delta    = outerPoint1 - _prevOuterPoint1;
        float   id = innerDelta.magnitude;
        float   od = p1Delta.magnitude;

        if (id > 0.001f)
        {
            int c = Physics.BoxCastNonAlloc(_prevInnerCenter, innerExt, innerDelta / id, _innerSweep, hb.rotation, id, _enemyHurtboxMask);
            for (int i = 0; i < c; i++)
            {
                var e = _innerSweep[i].collider.GetComponentInParent<EnemyBase>();
                if (e != null) _newOverlaps.Add(e);
            }
        }
        if (od > 0.001f)
        {
            int c = Physics.CapsuleCastNonAlloc(_prevOuterPoint1, _prevOuterPoint2, capsuleRadius, p1Delta / od, _outerSweep, od, _enemyHurtboxMask);
            for (int i = 0; i < c; i++)
            {
                var e = _outerSweep[i].collider.GetComponentInParent<EnemyBase>();
                if (e != null) { _newOverlaps.Add(e); _newOuterHits.Add(e); }
            }
        }

        int ic = Physics.OverlapBoxNonAlloc(innerCenter, innerExt, _innerOverlap, hb.rotation, _enemyHurtboxMask);
        for (int i = 0; i < ic; i++)
        {
            var e = _innerOverlap[i].GetComponentInParent<EnemyBase>();
            if (e != null) _newOverlaps.Add(e);
        }
        int oc = Physics.OverlapCapsuleNonAlloc(outerPoint1, outerPoint2, capsuleRadius, _outerOverlap, _enemyHurtboxMask);
        for (int i = 0; i < oc; i++)
        {
            var e = _outerOverlap[i].GetComponentInParent<EnemyBase>();
            if (e != null) { _newOverlaps.Add(e); _newOuterHits.Add(e); }
        }

        float outerMult = _executioner != null ? _executioner.OuterDamageMultiplier : 1.35f;
        float innerMult = _executioner != null ? _executioner.InnerDamageMultiplier : 0.5f;
        foreach (var enemy in _newOverlaps)
        {
            if (_currentOverlaps.Contains(enemy) || _hitCooldowns.ContainsKey(enemy)) continue;
            bool isOuter = _newOuterHits.Contains(enemy);
            Hit(enemy, isOuter ? outerMult : innerMult, isOuter);
        }

        // Full-weapon box for strikeables and allies
        Vector3 fullCenter     = hb.position;
        Vector3 fullExt        = new(Mathf.Max(innerXExtent * sx, outerX), yExtent * sy, hb.lossyScale.z * 0.5f);
        Vector3 prevOuterCenter = (_prevOuterPoint1 + _prevOuterPoint2) * 0.5f;
        Vector3 prevFullCenter  = (_prevInnerCenter + prevOuterCenter) * 0.5f;
        Vector3 fullDelta   = fullCenter - prevFullCenter;
        float   fd          = fullDelta.magnitude;

        if (fd > 0.001f)
        {
            int sc = Physics.BoxCastNonAlloc(prevFullCenter, fullExt, fullDelta / fd, _strikeableSweep, hb.rotation, fd, _strikeableMask);
            for (int i = 0; i < sc; i++)
            {
                var s = _strikeableSweep[i].collider.GetComponentInParent<IStrikeable>();
                if (s != null) _newStrikeables.Add(s);
            }
        }
        int soc = Physics.OverlapBoxNonAlloc(fullCenter, fullExt, _strikeableOverlap, hb.rotation, _strikeableMask);
        for (int i = 0; i < soc; i++)
        {
            var s = _strikeableOverlap[i].GetComponentInParent<IStrikeable>();
            if (s != null) _newStrikeables.Add(s);
        }

        foreach (var strikeable in _newStrikeables)
            if (!_currentStrikeables.Contains(strikeable))
            {
                strikeable.OnStruck();
                Vector3 hitPos = strikeable is Component comp ? comp.transform.position : transform.position;
                SFXManager.instance?.Play(_stats.hitSound, hitPos, 1f, 0.05f);
            }

        if (_stats.canContactAllies)
        {
            if (fd > 0.001f)
            {
                int ac = Physics.BoxCastNonAlloc(prevFullCenter, fullExt, fullDelta / fd, _allySweep, hb.rotation, fd, _playerHurtboxMask);
                for (int i = 0; i < ac; i++)
                {
                    var a = _allySweep[i].collider.GetComponentInParent<CharacterStats>();
                    if (a != null && a != _stats) _newAllyOverlaps.Add(a);
                }
            }
            int aoc = Physics.OverlapBoxNonAlloc(fullCenter, fullExt, _allyOverlap, hb.rotation, _playerHurtboxMask);
            for (int i = 0; i < aoc; i++)
            {
                var a = _allyOverlap[i].GetComponentInParent<CharacterStats>();
                if (a != null && a != _stats) _newAllyOverlaps.Add(a);
            }
            foreach (var ally in _newAllyOverlaps)
                if (!_currentAllyOverlaps.Contains(ally))
                    HitAlly(ally);
        }

        (_currentOverlaps,    _newOverlaps)        = (_newOverlaps,    _currentOverlaps);
        (_currentStrikeables, _newStrikeables)     = (_newStrikeables, _currentStrikeables);
        (_currentAllyOverlaps, _newAllyOverlaps)   = (_newAllyOverlaps, _currentAllyOverlaps);
        _prevInnerCenter = innerCenter;
        _prevOuterPoint1 = outerPoint1;
        _prevOuterPoint2 = outerPoint2;
    }

    private void OnDrawGizmos()
    {
        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        float fullRange = hb.lossyScale.z;
        float handleHZ  = fullRange * handleRatio * 0.5f;
        float bladeHZ   = fullRange * (1f - handleRatio) * 0.5f;
        float sx = hb.lossyScale.x;
        float sy = hb.lossyScale.y;

        // Inner zone — orange box
        Vector3 innerCenter = hb.position - hb.forward * bladeHZ;
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(innerCenter, hb.rotation, Vector3.one);
        float innerXg = xRangeScaleRatio > 0f
            ? innerXExtent * Mathf.Lerp(sx, fullRange, xRangeScaleRatio)
            : innerXExtent * sx;
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(innerXg * 2f, yExtent * sy * 2f, handleHZ * 2f));
        Gizmos.matrix = prev;

        // Outer zone — red capsule (two spheres + connecting lines)
        float outerX = (_stats != null && _stats.outerXScalesWithRange)
            ? outerXExtent * Mathf.Lerp(sx, fullRange, outerXRangeScaleRatio)
            : outerXExtent * sx;
        float capsuleRadius     = bladeHZ;
        float capsuleHalfHeight = Mathf.Max(0f, outerX - capsuleRadius);
        Vector3 outerCenter = hb.position + hb.forward * handleHZ;
        Vector3 p1 = outerCenter + hb.right * capsuleHalfHeight;
        Vector3 p2 = outerCenter - hb.right * capsuleHalfHeight;

        Gizmos.color = new Color(1f, 0f, 0f, 0.4f);
        Gizmos.DrawWireSphere(p1, capsuleRadius);
        Gizmos.DrawWireSphere(p2, capsuleRadius);
        Gizmos.DrawLine(p1 + hb.up * capsuleRadius,     p2 + hb.up * capsuleRadius);
        Gizmos.DrawLine(p1 - hb.up * capsuleRadius,     p2 - hb.up * capsuleRadius);
        Gizmos.DrawLine(p1 + hb.forward * capsuleRadius, p2 + hb.forward * capsuleRadius);
        Gizmos.DrawLine(p1 - hb.forward * capsuleRadius, p2 - hb.forward * capsuleRadius);
    }

    private void Hit(EnemyBase enemy, float zoneMultiplier, bool canCrit = true)
    {
        if (enemy == null || enemy._isDead) return;
        _hitCooldowns[enemy] = Time.time;
        float damage = RollDamage(_stats.WeaponDamage * zoneMultiplier);
        bool isCrit = canCrit && Random.value < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage;
        enemy.TakeDamageRpc(damage, isCrit);
        InvokeOnStrike(enemy, isCrit);
        if (isCrit) WeaponBase.RaiseOnCritStrike(_stats, enemy);
        SFXManager.instance?.Play(_stats.hitSound, enemy.transform.position, 1f, 0.05f);
    }

    private void HitAlly(CharacterStats ally)
    {
        if (ally == null || ally.IsDead) return;
        WeaponBase.RaiseOnAllyHit(_stats, ally);
        if (!_stats.canDamageAllies) return;
        float damage = _stats.WeaponDamage;
        bool isCrit = Random.value < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage;
        ally.TakeDamageRpc(damage);
    }
}
