using System.Collections.Generic;
using PurrNet.Pooling;
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
        public bool  prevSet;
        public DisposableList<HitCooldownEntry>     hitCooldowns;
        public DisposableList<AllyHitCooldownEntry> allyHitCooldowns;
        public void Dispose()
        {
            hitCooldowns.Dispose();
            allyHitCooldowns.Dispose();
        }
    }

    [Header("Zone Half-Extents (normalized, multiplied by weapon scale)")]
    [SerializeField] private float innerXExtent = 0.1f;
    [SerializeField] private float outerXZExtent = 0.375f;
    [SerializeField] private float yExtent = 0.1f;

    [SerializeField] [Range(0f, 1f)] private float handleRatio = 0.6f;
    [SerializeField] private float outerXZRangeScaleRatio = 0.75f;
    [SerializeField] private Transform outerBladeVisual;
    // Rescales outerBladeVisual's local width to visually match the outer zone's actual extent as
    // WeaponRange changes — purely cosmetic, weapon-specific, not a character stat (unlike the old
    // system, where this lived on CharacterStats).
    [SerializeField] private bool outerXZScalesWithRange = false;

    [Header("Handle Damage")]
    [SerializeField] private float innerDamageMultiplier = 0.5f;
    // Handle (inner) hits carry innately weaker on-hit-power in addition to their reduced damage.
    [SerializeField] private float innerOnHitPowerMultiplier = 0.25f;

    private bool _initialized;
    private float _baseOuterBladeLocalScaleX;
    // Cached alongside X (see Awake()) so OuterXZMultiplier can scale the visual mesh's Z too, matching
    // ComputeZones's own capsuleRadius treatment — read fresh from this cached base each frame in
    // UpdateOuterBladeVisual(), never from the transform's own current (already-scaled) value, or repeated
    // calls would compound the multiplier geometrically instead of applying it once. Y is deliberately left
    // alone (no cache needed) — OuterXZMultiplier only ever scales X/Z (a bigger axehead reaching wider and
    // further), never Y (which would just make it thicker, not bigger).
    private float _baseOuterBladeLocalScaleZ;

    private readonly RaycastHit[] _innerSweep   = new RaycastHit[16];
    private readonly RaycastHit[] _outerSweep   = new RaycastHit[16];
    private readonly Collider[]   _innerOverlap = new Collider[16];
    private readonly Collider[]   _outerOverlap = new Collider[16];

    private readonly HashSet<IEnemyPD>            _touchedThisTick  = new();
    private readonly HashSet<IEnemyPD>            _outerHitsThisTick = new();

    // Ally-strike detection (OnStrikeHealAllyAugment and future "hit ally, do something" augments) — always
    // runs now, no enable gate (see CharacterStatsPD.NotifyAllyStrike's own doc comment).
    // _outerAlliesThisTick mirrors _outerHitsThisTick's tie-break (an ally touching both zones counts as
    // outer) so handle touches still trigger a weaker onHitPower, matching Hit()'s own
    // innerOnHitPowerMultiplier treatment.
    private readonly HashSet<CharacterStatsPD>           _touchedAlliesThisTick = new();
    private readonly HashSet<CharacterStatsPD>           _outerAlliesThisTick   = new();

    private float HitCooldown => _stats != null ? 135f / _stats.WeaponSpeed : 0.25f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (outerBladeVisual != null)
        {
            var s = outerBladeVisual.localScale;
            _baseOuterBladeLocalScaleX = s.x;
            _baseOuterBladeLocalScaleZ = s.z;
        }
    }

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

    // Destroyed(), not OnDestroy() — see WeaponMeleePD's own comment on the same change for why (pooled
    // removal never reliably fired OnDestroy(), leaving a stale WeaponManagerPD entry behind).
    protected override void Destroyed()
    {
        if (_manager != null) _manager.UnregisterWeapon(this);
    }

    protected override State GetInitialState() => new()
    {
        hitCooldowns = DisposableList<HitCooldownEntry>.Create(4),
        allyHitCooldowns = DisposableList<AllyHitCooldownEntry>.Create(4),
    };

    // ── Simulation ────────────────────────────────────────────────────────────

    // Empty — orbit angle is now ticked once, shared, on CharacterStatsPD (see
    // CharacterStatsPD.State.weaponOrbitAngle's own doc comment); this weapon just reads _stats.WeaponOrbitAngle
    // in LateSimulate() instead of maintaining its own copy.
    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Tick cooldowns here (deterministic, tick-based delta) rather than Time.deltaTime in UpdateView()
        // — see HitCooldownEntry's own doc comment for why the old approach was unsafe under resimulation.
        HitCooldownMath.Tick(state.hitCooldowns);
        HitCooldownMath.Tick(state.allyHitCooldowns);

        // Outer-track weapons (OuterTrackAugment) spin the opposite direction — see
        // WeaponMeleePD.LateSimulate's own angleSign comment for the full reasoning.
        float   angleSign   = IsOuterTrack ? -1f : 1f;
        float   currentAngle = angleSign * _stats.WeaponOrbitAngle;
        Vector3 origin = _stats.EffectiveOrbitOrigin;
        Vector3 orbitPos = OrbitalPosition(currentAngle, origin);
        Vector3 outward  = orbitPos - origin;
        if (outward == Vector3.zero) return;

        Transform hb = hitboxTarget != null ? hitboxTarget : transform;

        _touchedThisTick.Clear();
        _outerHitsThisTick.Clear();
        _touchedAlliesThisTick.Clear();
        _outerAlliesThisTick.Clear();

        (Vector3 innerCenter, Vector3 innerExt, Vector3 outerPoint1, Vector3 outerPoint2, float capsuleRadius, Quaternion rot) zones;

        if (state.prevSet)
        {
            // Same sub-arc reasoning as WeaponMeleePD.LateSimulate() — a straight sweep between this tick's
            // start and end position is a chord across the orbit circle, not the arc itself, and can miss
            // an enemy sitting on the arc between them at high enough rotation speed. Subdivide into
            // intermediate stops along the true arc when needed (see SweepMath.ComputeSubArcCount()) —
            // at normal speeds this is always exactly 1 segment, identical to a plain single sweep.
            float angleDelta = _stats.WeaponSpeed * delta;
            // angle before this tick's movement — see WeaponMeleePD.LateSimulate's own startAngle comment.
            float startAngle = currentAngle + angleSign * angleDelta;
            int   subArcs    = SweepMath.ComputeSubArcCount(angleDelta);

            // Last tick's final zone geometry, before any movement this tick — same trick as
            // WeaponMeleePD, hb still reflects last tick's transform here.
            var segZones = ComputeZones(hb);

            for (int step = 1; step <= subArcs; step++)
            {
                bool    isLastStep = step == subArcs;
                Vector3 stepPos = isLastStep
                    ? orbitPos
                    : OrbitalPosition(Mathf.Lerp(startAngle, currentAngle, step / (float)subArcs), origin);
                Vector3 stepOutward = stepPos - origin;
                if (stepOutward == Vector3.zero) continue;

                transform.SetPositionAndRotation(ApplyHeightLift(stepPos), Quaternion.LookRotation(stepOutward));
                var newSegZones = ComputeZones(hb);

                Vector3 innerDelta = newSegZones.innerCenter - segZones.innerCenter;
                float   id = innerDelta.magnitude;
                if (id > 0.001f)
                {
                    int c = Physics.BoxCastNonAlloc(segZones.innerCenter, newSegZones.innerExt, innerDelta / id,
                        _innerSweep, newSegZones.rot, id, CombatLayerMasks.EnemyHurtboxMask);
                    for (int i = 0; i < c; i++)
                    {
                        var e = _innerSweep[i].collider.GetComponent<IEnemyPD>();
                        if (e != null) _touchedThisTick.Add(e);
                    }

                    int ac = Physics.BoxCastNonAlloc(segZones.innerCenter, newSegZones.innerExt, innerDelta / id,
                        _innerSweep, newSegZones.rot, id, CombatLayerMasks.PlayerHurtboxMask);
                    for (int i = 0; i < ac; i++)
                    {
                        var a = _innerSweep[i].collider.GetComponent<CharacterStatsPD>();
                        if (a != null) _touchedAlliesThisTick.Add(a);
                    }
                }

                Vector3 p1Delta = newSegZones.outerPoint1 - segZones.outerPoint1;
                float   od = p1Delta.magnitude;
                if (od > 0.001f)
                {
                    int c = Physics.CapsuleCastNonAlloc(segZones.outerPoint1, segZones.outerPoint2, newSegZones.capsuleRadius,
                        p1Delta / od, _outerSweep, od, CombatLayerMasks.EnemyHurtboxMask);
                    for (int i = 0; i < c; i++)
                    {
                        var e = _outerSweep[i].collider.GetComponent<IEnemyPD>();
                        if (e != null) { _touchedThisTick.Add(e); _outerHitsThisTick.Add(e); }
                    }

                    int ac = Physics.CapsuleCastNonAlloc(segZones.outerPoint1, segZones.outerPoint2, newSegZones.capsuleRadius,
                        p1Delta / od, _outerSweep, od, CombatLayerMasks.PlayerHurtboxMask);
                    for (int i = 0; i < ac; i++)
                    {
                        var a = _outerSweep[i].collider.GetComponent<CharacterStatsPD>();
                        if (a != null) { _touchedAlliesThisTick.Add(a); _outerAlliesThisTick.Add(a); }
                    }
                }

                segZones = newSegZones;
            }

            zones = segZones;
        }
        else
        {
            transform.SetPositionAndRotation(ApplyHeightLift(orbitPos), Quaternion.LookRotation(outward));
            zones = ComputeZones(hb);
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

        int aic = Physics.OverlapBoxNonAlloc(zones.innerCenter, zones.innerExt, _innerOverlap, zones.rot, CombatLayerMasks.PlayerHurtboxMask);
        for (int i = 0; i < aic; i++)
        {
            var a = _innerOverlap[i].GetComponent<CharacterStatsPD>();
            if (a != null) _touchedAlliesThisTick.Add(a);
        }

        int aoc = Physics.OverlapCapsuleNonAlloc(zones.outerPoint1, zones.outerPoint2, zones.capsuleRadius, _outerOverlap, CombatLayerMasks.PlayerHurtboxMask);
        for (int i = 0; i < aoc; i++)
        {
            var a = _outerOverlap[i].GetComponent<CharacterStatsPD>();
            if (a != null) { _touchedAlliesThisTick.Add(a); _outerAlliesThisTick.Add(a); }
        }

        // Pure cooldown gate — an enemy standing continuously inside a zone gets rehit as soon as its
        // cooldown clears, no need to actually separate first. HitCooldown is a fraction of a full weapon
        // rotation, which is enough on its own to handle continuous-overlap cases sanely. An enemy
        // overlapping both zones this tick still counts as an outer hit (outer wins the tie-break).
        // Skipped entirely while _stats.IsWeaponsDisabled — see WeaponMeleePD's own equivalent comment.
        if (!_stats.IsWeaponsDisabled)
        {
            foreach (var enemy in _touchedThisTick)
            {
                if (HitCooldownMath.TryStart(state.hitCooldowns, enemy.ObjectId, HitCooldown, delta))
                    Hit(enemy, _outerHitsThisTick.Contains(enemy));
            }

            foreach (var ally in _touchedAlliesThisTick)
            {
                if (HitCooldownMath.TryStart(state.allyHitCooldowns, ally.owner ?? default, HitCooldown, delta))
                    HitAlly(ally, _outerAlliesThisTick.Contains(ally));
            }
        }

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

        // Null-guarded, not a plain _stats.OuterXZMultiplier read — OnDrawGizmos() below calls ComputeZones()
        // directly, unguarded, which can run in the editor before this weapon's own _stats has ever been
        // resolved (e.g. viewing the prefab in isolation, not in a live scene).
        float outerXZMultiplier = _stats != null ? _stats.OuterXZMultiplier : 1f;

        Vector3 innerCenter = hb.position - hb.forward * bladeHZ;
        Vector3 outerCenter = hb.position + hb.forward * handleHZ;
        float outerXZ = (outerXZScalesWithRange
            ? outerXZExtent * Mathf.Lerp(sx, fullRange, outerXZRangeScaleRatio)
            : outerXZExtent * sx) * outerXZMultiplier;
        // capsuleRadius scaled by the SAME multiplier as outerXZ — a capsule's radius is its reach in every
        // direction perpendicular to its own segment (forward/back along hb.forward, up/down along hb.up),
        // so scaling only outerXZ (the segment's own half-length, i.e. how far apart the two end-spheres
        // sit) just stretches the zone sideways into a thin bar without growing it, rather than growing the
        // whole outer zone proportionally the way a bigger axehead actually should look/hit. See
        // UpdateOuterBladeVisual's own matching Y/Z scale for why the visual needs the identical treatment.
        float capsuleRadius     = bladeHZ * outerXZMultiplier;
        float capsuleHalfHeight = Mathf.Max(0f, outerXZ - capsuleRadius);

        // Extends the capsule's own forward reach to match hitboxTip's visual reach — same technique as
        // WeaponMeleePD's box (see GetHitboxTipExtraReach's own comment): shifting outerCenter forward and
        // growing capsuleRadius by the same half-extra-reach amount nets a far edge exactly tipExtra
        // further out while the near edge (relative to outerCenter) stays fixed, the shift terms cancelling
        // out identically to the melee case. Applied after capsuleHalfHeight above so it doesn't feed back
        // into the outerXZ-derived side width. Growing capsuleRadius does also widen the capsule's reach
        // perpendicular to hb.right (up/down and past the endpoints) slightly, an unavoidable consequence
        // of a capsule's radius being uniform in every direction perpendicular to its own line segment —
        // no separate "length" knob exists the way a box's half-extents give per-axis independence. 0 when
        // no hitboxTip is assigned (every axe currently), so this is a no-op today.
        float centerShift = GetHitboxTipExtraReach() * 0.5f;
        outerCenter   += hb.forward * centerShift;
        capsuleRadius += centerShift;

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
        ApplyVisualAdjustments();

        // Purely cosmetic (outer blade visual width) — render rate, not simulation, since it has no
        // gameplay effect and doesn't need to be replay-consistent.
        if (outerXZScalesWithRange && outerBladeVisual != null && _stats != null)
            UpdateOuterBladeVisual();
    }

    private void UpdateOuterBladeVisual()
    {
        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        float fullRange = _stats.WeaponRange;
        float sx = hb.lossyScale.x;
        float outerXZ = outerXZExtent * Mathf.Lerp(sx, fullRange, outerXZRangeScaleRatio);
        float baseOuter = outerXZExtent * sx;
        float hitboxRatio = baseOuter > 0f ? outerXZ / baseOuter : 1f;
        // rangeScaleTarget's X is already multiplied by parentXRatio via ScaleModel — divide it out.
        float parentXRatio = xRangeScaleRatio > 0f
            ? Mathf.Lerp(1f, fullRange / Mathf.Max(0.001f, designOrbit * transform.localScale.z), xRangeScaleRatio)
            : 1f;
        float localRatio = parentXRatio > 0.001f ? hitboxRatio / parentXRatio : hitboxRatio;
        // OuterXZMultiplier applied here, at the end, rather than folded into outerXZ/baseOuter above — both
        // of those would need it equally (it's flat, not range-driven), so baking it into the ratio between
        // them would just cancel it right back out. Matches ComputeZones's own read of the same stat, so
        // the visual can never drift out of sync with the actual hitbox capsule. Z scales from it too now
        // (not just X) — same "grow the whole outer zone proportionally, not just stretch it sideways"
        // reasoning as ComputeZones's own capsuleRadius fix; read from the cached _baseOuterBladeLocalScaleZ
        // (set once in Awake()), never the transform's own current scale, or this would compound every
        // frame. Y is deliberately left as the transform's own current value, untouched — OuterXZMultiplier
        // only ever grows X/Z (a bigger axehead reaching wider and further), never Y (thickness).
        Vector3 vs = outerBladeVisual.localScale;
        outerBladeVisual.localScale = new Vector3(
            _baseOuterBladeLocalScaleX * localRatio * _stats.OuterXZMultiplier,
            vs.y,
            _baseOuterBladeLocalScaleZ * _stats.OuterXZMultiplier);
    }

    // ── Hit ───────────────────────────────────────────────────────────────────

    private void Hit(IEnemyPD enemy, bool isOuter)
    {
        if (enemy._isDead) return;
        float baseDamage = isOuter ? _stats.Strength : _stats.Strength * innerDamageMultiplier;
        float damage = RollDamage(baseDamage, out bool isHighroll);
        if (isHighroll) damage *= 1f + _stats.HighrollBonus;
        bool  isCrit = isOuter && GameRandom.instance.NextFloat() < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage;
        if (isHighroll) _stats.NotifyStrikeHighroll(damage);
        // baseDamage already accounts for innerDamageMultiplier on handle (inner) hits, and onHitPower gets
        // the equivalent innerOnHitPowerMultiplier treatment — downstream on-hit effects see the actual
        // per-hit damage/power this hit represented, not a flat re-read of Strength/OnHitPower.
        float onHitPower = isOuter ? _stats.OnHitPower : _stats.OnHitPower * innerOnHitPowerMultiplier;
        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = baseDamage, onHitPower = onHitPower, isCrit = isCrit };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
        Vector3 knockDir = (enemy.transform.position - _stats.transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback * onHitPower);
        NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = damage, isCrit = isCrit, isHighroll = isHighroll, attacker = _stats, isPriority = true });
    }

    // A touched ally only ever fires the _onAllyStrike notification now — real friendly fire has been
    // removed from the project entirely (see CharacterStatsPD.NotifyAllyStrike's own doc comment). Handle
    // (inner) touches still report the weaker onHitPower, matching Hit()'s own treatment.
    private void HitAlly(CharacterStatsPD ally, bool isOuter)
    {
        if (ally == _stats || ally.IsDead) return;
        float onHitPower = isOuter ? _stats.OnHitPower : _stats.OnHitPower * innerOnHitPowerMultiplier;
        _stats.NotifyAllyStrike(new AllyStrikeData { ally = ally, onHitPower = onHitPower });
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
