using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Orbits its owner via the same pivot-based approach as WeaponMeleePD (2026-08-25 rework — see that class's
// own doc comment for the full design/PredictedTransform-on-Pivot contract, identical here) — root tracks
// the orbit origin and sweep rotation only, orbitPivot (its child) carries the live radius, Visuals/
// hitboxTarget hang off orbitPivot. The ONLY thing that's Axe-specific is ComputeZones()/the hit-detection
// shape below: two zones swept per tick instead of one — a narrow inner zone (the handle) and a much wider
// outer zone (the axehead). Only the axehead can crit and deals full damage; the handle deals reduced
// damage and never crits. If an enemy overlaps both zones in the same tick, it counts as an outer hit
// (outer wins the tie-break) — ported from the old WeaponAxe.cs, unchanged by the pivot rework.
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

    // See WeaponMeleePD's own orbitPivot field comment for the full contract — identical here.
    [SerializeField] private Transform orbitPivot;

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

    // Same sign/composition as WeaponMeleePD's own FlipTipRotation — see that field's own comment.
    private static readonly Quaternion FlipTipRotation = Quaternion.Euler(0f, 0f, -90f);

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
    // "Tip" hits — see WeaponHitData.isTip's own doc comment. Completely independent of the inner/outer
    // (handle/axehead) split above — the outer 10% of the TOTAL hitbox (handle near edge to axehead far
    // edge), computed fresh from whatever ComputeZones() actually returns each check (see ComputeTipEdges),
    // so it automatically tracks range/tier/OuterXZMultiplier scaling without a separate formula to keep in
    // sync. Populated at every detection site (inner box AND outer capsule alike — a handle hit can't
    // realistically land in the outer 10% given the zones' own relative sizes, so no special-casing needed
    // to keep the two concepts from interfering).
    private readonly HashSet<IEnemyPD>            _tipHitsThisTick   = new();

    // Ally-strike detection (OnStrikeHealAllyAugment and future "hit ally, do something" augments) — always
    // runs now, no enable gate (see CharacterStatsPD.NotifyAllyStrike's own doc comment).
    // _outerAlliesThisTick mirrors _outerHitsThisTick's tie-break (an ally touching both zones counts as
    // outer) so handle touches still trigger a weaker onHitPower, matching Hit()'s own
    // innerOnHitPowerMultiplier treatment.
    private readonly HashSet<IFriendlyPD>                _touchedAlliesThisTick = new();
    private readonly HashSet<IFriendlyPD>                _outerAlliesThisTick   = new();

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

    protected override void Simulate(ref State state, float delta) { }

    // WeaponBasePD.NominalOrbitRadius() is private — duplicated here, same as WeaponMeleePD's own copy.
    private float NominalOrbitRadius()
    {
        float halfRange = rangeScaleTarget != null
            ? designOrbit * RootScale.z
            : (IsMelee ? RangeValue / 2f : 0.25f);
        // EffectiveWeaponRange, not the raw WeaponRange stat — see WeaponBasePD.NominalOrbitRadius's own
        // identical 2026-09-14 doc comment.
        float weaponRadiusOffset = IsOuterTrack ? _stats.WeaponRadius + _stats.EffectiveWeaponRange : _stats.WeaponRadius;
        return weaponRadiusOffset + _stats.bonusSizePercent * 0.5f + halfRange;
    }

    private float SlotAngleOffset()
    {
        int total = _manager != null ? Mathf.Max(1, IsOuterTrack ? _manager.OuterWeaponCount : _manager.WeaponCount) : 1;
        return 360f / total * SlotIndex;
    }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (orbitPivot == null) return;

        // Tick cooldowns here (deterministic, tick-based delta) rather than Time.deltaTime in UpdateView()
        // — see HitCooldownEntry's own doc comment for why the old approach was unsafe under resimulation.
        HitCooldownMath.Tick(state.hitCooldowns);
        HitCooldownMath.Tick(state.allyHitCooldowns);

        // Same negation as WeaponMeleePD.LateSimulate's own currentAngle — see its own comment for why
        // (axis-convention parity between this rigid-composition approach and the old cos/sin+LookRotation
        // math).
        float angleSign = IsOuterTrack ? -1f : 1f;
        float currentAngle = -(angleSign * _stats.WeaponOrbitAngle + SlotAngleOffset());

        // Same FlipTipRotation composition as WeaponMeleePD — see its own comment.
        Quaternion tip = _stats.IsOrbitFlipped
            ? Quaternion.Euler(0f, _stats.OrbitFacingYaw, 0f) * FlipTipRotation
            : Quaternion.identity;

        float radius = NominalOrbitRadius();
        if (radius <= 0.0001f) return;

        // Root just tracks the origin — see WeaponMeleePD.LateSimulate's own comment for why ApplyHeightLift()
        // is called unconditionally now (no flip-branching needed there at all).
        Vector3 origin = _stats.EffectiveOrbitOrigin;
        transform.position = ApplyHeightLift(origin);

        // Counter-scaled by rootZ — see WeaponMeleePD.LateSimulate's own comment for the full reasoning.
        float rootZ = transform.localScale.z;
        orbitPivot.localPosition = new Vector3(0f, 0f, radius / Mathf.Max(0.0001f, rootZ));

        Transform hb = hitboxTarget != null ? hitboxTarget : orbitPivot;

        _touchedThisTick.Clear();
        _outerHitsThisTick.Clear();
        _touchedAlliesThisTick.Clear();
        _outerAlliesThisTick.Clear();
        _tipHitsThisTick.Clear();

        (Vector3 innerCenter, Vector3 innerExt, Vector3 outerPoint1, Vector3 outerPoint2, float capsuleRadius, Quaternion rot) zones;

        if (state.prevSet)
        {
            // Same sub-arc reasoning as WeaponMeleePD.LateSimulate() — a straight sweep between this tick's
            // start and end zone geometry is a chord across the orbit circle, not the arc itself, and can
            // miss an enemy sitting on the arc between them at high enough rotation speed. Subdivide into
            // intermediate stops along the true arc when needed (see SweepMath.ComputeSubArcCount()) — at
            // normal speeds this is always exactly 1 segment, identical to a plain single sweep.
            float angleDelta = _stats.WeaponSpeed * delta;
            float startAngle = currentAngle - angleSign * angleDelta;
            int   subArcs    = SweepMath.ComputeSubArcCount(angleDelta);

            // Last tick's final zone geometry, before this tick's rotation is applied below — hb still
            // reflects last tick's rotation here (root's rotation hasn't been touched yet this tick), same
            // trick WeaponMeleePD's own prevCenter uses.
            var segZones = ComputeZones(hb);

            for (int step = 1; step <= subArcs; step++)
            {
                bool  isLastStep = step == subArcs;
                float stepAngle  = isLastStep ? currentAngle : Mathf.LerpAngle(startAngle, currentAngle, step / (float)subArcs);

                transform.rotation = tip * Quaternion.Euler(0f, stepAngle, 0f);
                var newSegZones = ComputeZones(hb);
                var (segTipNear, segTipFar) = ComputeTipEdges(newSegZones);

                Vector3 innerDelta = newSegZones.innerCenter - segZones.innerCenter;
                float   id = innerDelta.magnitude;
                if (id > 0.001f)
                {
                    int c = Physics.BoxCastNonAlloc(segZones.innerCenter, newSegZones.innerExt, innerDelta / id,
                        _innerSweep, newSegZones.rot, id, CombatLayerMasks.EnemyHurtboxMask);
                    for (int i = 0; i < c; i++)
                    {
                        var e = _innerSweep[i].collider.GetComponent<IEnemyPD>();
                        if (e == null) continue;
                        _touchedThisTick.Add(e);
                        if (IsInTipZone(e.transform.position, segTipNear, segTipFar)) _tipHitsThisTick.Add(e);
                    }

                    int ac = Physics.BoxCastNonAlloc(segZones.innerCenter, newSegZones.innerExt, innerDelta / id,
                        _innerSweep, newSegZones.rot, id, CombatLayerMasks.FriendlyHurtboxMask);
                    for (int i = 0; i < ac; i++)
                    {
                        var a = _innerSweep[i].collider.GetComponent<IFriendlyPD>();
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
                        if (e == null) continue;
                        _touchedThisTick.Add(e);
                        _outerHitsThisTick.Add(e);
                        if (IsInTipZone(e.transform.position, segTipNear, segTipFar)) _tipHitsThisTick.Add(e);
                    }

                    int ac = Physics.CapsuleCastNonAlloc(segZones.outerPoint1, segZones.outerPoint2, newSegZones.capsuleRadius,
                        p1Delta / od, _outerSweep, od, CombatLayerMasks.FriendlyHurtboxMask);
                    for (int i = 0; i < ac; i++)
                    {
                        var a = _outerSweep[i].collider.GetComponent<IFriendlyPD>();
                        if (a != null) { _touchedAlliesThisTick.Add(a); _outerAlliesThisTick.Add(a); }
                    }
                }

                segZones = newSegZones;
            }

            zones = segZones;
        }
        else
        {
            transform.rotation = tip * Quaternion.Euler(0f, currentAngle, 0f);
            zones = ComputeZones(hb);
        }

        var (tipNear, tipFar) = ComputeTipEdges(zones);

        int ic = Physics.OverlapBoxNonAlloc(zones.innerCenter, zones.innerExt, _innerOverlap, zones.rot, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < ic; i++)
        {
            var e = _innerOverlap[i].GetComponent<IEnemyPD>();
            if (e == null) continue;
            _touchedThisTick.Add(e);
            if (IsInTipZone(e.transform.position, tipNear, tipFar)) _tipHitsThisTick.Add(e);
        }

        int oc = Physics.OverlapCapsuleNonAlloc(zones.outerPoint1, zones.outerPoint2, zones.capsuleRadius, _outerOverlap, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < oc; i++)
        {
            var e = _outerOverlap[i].GetComponent<IEnemyPD>();
            if (e == null) continue;
            _touchedThisTick.Add(e);
            _outerHitsThisTick.Add(e);
            if (IsInTipZone(e.transform.position, tipNear, tipFar)) _tipHitsThisTick.Add(e);
        }

        int aic = Physics.OverlapBoxNonAlloc(zones.innerCenter, zones.innerExt, _innerOverlap, zones.rot, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < aic; i++)
        {
            var a = _innerOverlap[i].GetComponent<IFriendlyPD>();
            if (a != null) _touchedAlliesThisTick.Add(a);
        }

        int aoc = Physics.OverlapCapsuleNonAlloc(zones.outerPoint1, zones.outerPoint2, zones.capsuleRadius, _outerOverlap, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < aoc; i++)
        {
            var a = _outerOverlap[i].GetComponent<IFriendlyPD>();
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
                if (HitCooldownMath.TryStart(state.allyHitCooldowns, ally.ObjectId, HitCooldown, delta))
                    HitAlly(ally, _outerAlliesThisTick.Contains(ally));
            }
        }

        state.prevSet = true;
    }

    // Recomputes both zones from the hitbox transform's CURRENT state — called twice per tick (once
    // before moving, for the previous-tick points; once after, for this tick's) and once per gizmo draw.
    // Unchanged by the pivot rework — fully self-contained off hb's own live transform regardless of how
    // it got positioned/rotated.
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

    // The TOTAL hitbox's own [near, far] depth edges — handle's own back face to the axehead capsule's own
    // forward-most reach — for the tip-zone check below. Derived entirely from whatever ComputeZones() just
    // returned rather than re-deriving handleHZ/bladeHZ/centerShift by hand, so this can never drift out of
    // sync with range/tier/OuterXZMultiplier scaling or a future hitboxTip addition. The capsule's own radius
    // (not capsuleHalfHeight, which only measures its SIDEWAYS extent along hb.right) is what actually
    // determines its forward/back reach from outerCenter — see ComputeZones's own capsuleRadius comment.
    private static (Vector3 near, Vector3 far) ComputeTipEdges(
        (Vector3 innerCenter, Vector3 innerExt, Vector3 outerPoint1, Vector3 outerPoint2, float capsuleRadius, Quaternion rot) zones)
    {
        Vector3 forward = zones.rot * Vector3.forward;
        Vector3 near = zones.innerCenter - forward * zones.innerExt.z;
        Vector3 outerCenter = (zones.outerPoint1 + zones.outerPoint2) * 0.5f;
        Vector3 far = outerCenter + forward * zones.capsuleRadius;
        return (near, far);
    }

    // See WeaponMeleePD's own identical helper — same "outer 10% of the [near, far] depth" definition,
    // shared conceptually (not literally shared code, per this file's own "duplicate rather than touch
    // WeaponBasePD" convention) across both weapon types.
    private static bool IsInTipZone(Vector3 enemyPos, Vector3 nearEdge, Vector3 farEdge)
    {
        Vector3 axis = farEdge - nearEdge;
        float totalLenSq = axis.sqrMagnitude;
        if (totalLenSq < 0.0001f) return false;
        float t = Vector3.Dot(enemyPos - nearEdge, axis) / totalLenSq;
        return t >= 0.9f;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!_initialized) TryInit();
        ApplyModelVisualAdjustments();
        UpdateWeaponParticlePause();

        // Purely cosmetic (outer blade visual width) — render rate, not simulation, since it has no
        // gameplay effect and doesn't need to be replay-consistent.
        if (outerXZScalesWithRange && outerBladeVisual != null && _stats != null)
            UpdateOuterBladeVisual();
    }

    // Same as WeaponMeleePD's own ApplyModelVisualAdjustments — see that method's own comment for why this
    // reuses WeaponBasePD's (protected) visualHeightTarget directly rather than a separate reference, and
    // why the rotation half of the old ApplyVisualAdjustments() isn't needed at all here. (2026-08-25: an
    // idle wobble sway used to live here too — reverted fleet-wide after live testing, see WeaponMeleePD's
    // own equivalent comment.)
    private void ApplyModelVisualAdjustments()
    {
        if (visualHeightTarget == null || _stats == null) return;
        Vector3 p = visualHeightTarget.localPosition;
        float localLift = _stats.IsOrbitFlipped ? 0f : WeaponVisualConstants.BaseMidHeightLift;
        visualHeightTarget.localPosition = new Vector3(p.x, localLift, p.z);
    }

    private void UpdateOuterBladeVisual()
    {
        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        // EffectiveWeaponRange (2026-09-16, was WeaponRange directly) — this is the weapon's own physical
        // blade geometry, same "real weapon geometry reads EffectiveWeaponRange, never the raw stat" convention
        // every other weapon-geometry consumer in this codebase already follows; this call site was missed
        // when that convention was established.
        float fullRange = _stats.EffectiveWeaponRange;
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
        float baseDamage = isOuter ? _stats.WeaponStrength : _stats.WeaponStrength * innerDamageMultiplier;
        float damage = RollDamage(baseDamage, out bool isHighroll);
        bool  isCrit = isOuter && GameRandom.instance.NextFloat() < _stats.CritChance;
        damage = WeaponDamageMath.ApplyCritAndHighroll(damage, isHighroll, isCrit, _stats.HighrollBonus, _stats.CritDamage);
        // baseDamage already accounts for innerDamageMultiplier on handle (inner) hits, and onHitPower gets
        // the equivalent innerOnHitPowerMultiplier treatment — downstream on-hit effects see the actual
        // per-hit damage/power this hit represented, not a flat re-read of WeaponStrength/WeaponOnHitPower.
        // Computed here (moved above NotifyStrikeHighroll) so that call can pass this hit's own onHitPower too.
        float onHitPower = isOuter ? _stats.WeaponOnHitPower : _stats.WeaponOnHitPower * innerOnHitPowerMultiplier;
        if (isHighroll) _stats.NotifyStrikeHighroll(damage, onHitPower);
        // weaponRadius/weaponOffset/weaponRangeMultiplier — see WeaponMeleePD.Hit's own identical 2026-09-14
        // doc comment. IsOuterTrack (this weapon's own orbit-track property) here, NOT the isOuter local
        // above (this hit's own inner/outer HITBOX ZONE, handle vs. blade — an unrelated concept).
        float weaponRadius = IsOuterTrack ? _stats.WeaponRadius + _stats.EffectiveWeaponRange : _stats.WeaponRadius;
        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = baseDamage, onHitPower = onHitPower, isCrit = isCrit, isHighroll = isHighroll, hitDistance = Vector3.Distance(_stats.transform.position, enemy.transform.position), weaponRadius = weaponRadius, weaponOffset = _stats.bonusSizePercent * 0.5f, weaponRangeMultiplier = _stats.weaponRangeScalingBonusPercent * _stats.weaponRangeScalingPenalty, isTip = _tipHitsThisTick.Contains(enemy) };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default, isHighroll);
        Vector3 knockDir = (enemy.transform.position - _stats.transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback * onHitPower);
        NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = damage, isCrit = isCrit, isHighroll = isHighroll, attacker = _stats, isPriority = true });
    }

    // A touched ally only ever fires the _onAllyStrike notification now — real friendly fire has been
    // removed from the project entirely (see CharacterStatsPD.NotifyAllyStrike's own doc comment). Handle
    // (inner) touches still report the weaker onHitPower, matching Hit()'s own treatment.
    private void HitAlly(IFriendlyPD ally, bool isOuter)
    {
        if (ReferenceEquals(ally, _stats) || ally.IsDead) return;
        float onHitPower = isOuter ? _stats.WeaponOnHitPower : _stats.WeaponOnHitPower * innerOnHitPowerMultiplier;
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
