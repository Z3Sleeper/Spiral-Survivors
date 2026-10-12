using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Axe weapon hit detection — a sibling of WeaponMeleePD under WeaponBasePD, not a parent/child relationship
// (see WeaponBasePD's own class doc comment for the full migration history). Axe's real hit detection is
// fundamentally different from Melee's single continuous box, so this can't just extend WeaponMeleePD — but
// it DOES reuse that exact same box-sweep code almost verbatim for the "handle" hit (below).
//
// (2026-09-30, per explicit design) Axe is just an extension of the Blade/Hilt/Tip convention with Hilt/Tip
// deliberately left unused — hitboxHilt/hitboxTip both stay at their default 0, and hiltParent/tipParent stay
// unassigned in the Inspector (base class fields remain available if a future axe weapon ever wants them —
// nothing here forecloses that). With both at 0, WeaponBasePD's shared ScaleModel()/
// NominalOrbitRadius() math degenerates correctly with NO changes needed: Blade alone spans the weapon's
// entire hitbox depth, starting right at Pivot.
//
// The axehead is a SEPARATE capsule whose long axis runs along local X (sideways), not the travel/forward
// direction — a wide, flat stadium shape sticking out past the handle box's own width (see user-supplied
// reference image), centered in the outer `axeHeadPercent`-fraction slice of Blade's own current (already
// range/size-scaled) world depth — a PERCENTAGE of the total, not a flat world-unit length, so it scales
// proportionally for free as range/size change (no separate tracking needed). Handle is simply the inferred
// remainder — no separate field. Because the capsule's axis isn't aligned with the sweep direction, it can't
// reuse a simple "endpoints = start/end travel position" trick — it's swept per substep with a real
// CapsuleCastNonAlloc instead, using the same start-position-plus-current-rotation approximation the handle
// box's own BoxCastNonAlloc already relies on. An enemy caught by both box and capsule counts as an axehead
// hit (full damage/crit), same "outer wins the tie-break" rule the old system already had — no more
// handleRatio, capsuleHalfHeight/outerXZExtent, or CharacterStatsPD.OuterXZMultiplier (that stat has since
// been fully removed from CharacterStatsPD — no live read or write sites left anywhere) — replaced by nothing
// more than properly authoring rangeScalingBaseline/rangeSizeScalingRatio for this weapon (2026-09-30 — a
// separate flat axeHeadMultiplier on top was tried and removed too, since it wouldn't track the actual
// authored visuals).
public class WeaponAxePD : WeaponBasePD<WeaponAxePD.State>
{
    public struct State : IPredictedData<State>
    {
        public bool prevSet;
        public DisposableList<HitCooldownEntry>     hitCooldowns;
        public DisposableList<AllyHitCooldownEntry> allyHitCooldowns;
        public void Dispose()
        {
            hitCooldowns.Dispose();
            allyHitCooldowns.Dispose();
        }
    }

    [SerializeField] private Vector3 meleeHalfExtents = new(0.1f, 0.1f, 0.5f);

    [SerializeField] private Transform orbitPivot;

    [Header("Axehead")]
    // What fraction of Blade's own current (already range/size-scaled) world depth is axehead, measured
    // inward from the far edge — 0.6 means the outer 60% of the whole hitbox. Handle is simply the inferred
    // remainder (1 - this), no separate field needed. A true PERCENTAGE of the total, not a flat world-unit
    // length (see class doc comment for why that distinction matters) — scales proportionally for free.
    [Range(0f, 1f)]
    [SerializeField] private float axeHeadPercent = 0.6f;
    // Capsule half-width along local X — how far the axehead sticks out SIDEWAYS past the handle box's own
    // width (see class doc comment / reference image: a flat stadium shape, not a Z-aligned sausage). Scaled
    // live by ComputeScaleMultiplier() (the shared rangeSizeScalingRatio resize every Normalized weapon gets)
    // — author rangeScalingBaseline/rangeSizeScalingRatio to match this weapon's actual visuals rather than
    // reaching for a separate axehead-only multiplier (one was tried and removed, see class doc comment).
    [SerializeField] private float axeHeadHalfWidth = 0.5f;
    // Capsule radius — governs the Z/Y roundness of the axehead shape (and rounds the X ends out further by
    // this same amount, standard capsule geometry). Scaled the same way as axeHeadHalfWidth above.
    [SerializeField] private float axeHeadRadius = 0.3f;

    [Header("Handle Damage")]
    // Handle (box-only, non-axehead) hits deal reduced damage and never crit — same convention the old
    // WeaponAxePD already used.
    [SerializeField] private float innerDamageMultiplier = 0.5f;
    [SerializeField] private float innerOnHitPowerMultiplier = 0.25f;

    // Editor-only preview — see WeaponMeleePD's own identical field/method for the full reasoning
    // ([ContextMenu] doesn't bind to a method only inherited from an open generic base class).
    [SerializeField] private float editorPreviewRange = 10f;

    [ContextMenu("Preview Range In Editor")]
    private void PreviewInEditor()
    {
        InitModelVisuals();
        ScaleModel(editorPreviewRange);
    }

    private static readonly Quaternion FlipTipRotation = Quaternion.Euler(0f, 0f, -90f);

    private bool _initialized;

    private readonly RaycastHit[] _sweepResults        = new RaycastHit[32];
    private readonly Collider[]   _overlapResults       = new Collider[32];
    private readonly Collider[]   _axeHeadOverlapResults = new Collider[16];
    private readonly HashSet<IEnemyPD>    _touchedThisTick       = new();
    private readonly HashSet<IFriendlyPD> _touchedAlliesThisTick = new();
    // Which of this tick's touched enemies/allies were ALSO caught by the axehead capsule — "outer wins the
    // tie-break" (full damage/crit for enemies, full onHitPower for allies), same convention the old
    // WeaponAxePD's own _outerHitsThisTick/_outerAlliesThisTick used.
    private readonly HashSet<IEnemyPD>    _axeHeadHitsThisTick       = new();
    private readonly HashSet<IFriendlyPD> _axeHeadAlliesThisTick     = new();
    // "Tip" hits — see WeaponHitData.isTip's own doc comment. Independent of the handle/axehead split above
    // (a handle hit landing in the outer 10% of the TOTAL hitbox depth still counts, same as it always did on
    // every other weapon) — checked at every detection site, box and capsule alike, same "no special-casing
    // needed" reasoning the old WeaponAxePD already used.
    private readonly HashSet<IEnemyPD>    _tipHitsThisTick = new();

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

        HitCooldownMath.Tick(state.hitCooldowns);
        HitCooldownMath.Tick(state.allyHitCooldowns);

        float angleSign = IsOuterTrack ? -1f : 1f;
        float currentAngle = -(angleSign * _stats.WeaponOrbitAngle + SlotAngleOffset());

        Quaternion tip = _stats.IsOrbitFlipped
            ? Quaternion.Euler(0f, _stats.OrbitFacingYaw, 0f) * FlipTipRotation
            : Quaternion.identity;

        float radius = NominalOrbitRadius();
        if (radius <= 0.0001f) return;

        Vector3 origin = _stats.EffectiveOrbitOrigin;
        transform.position = ApplyHeightLift(origin);
        float rootZ = transform.localScale.z;
        orbitPivot.localPosition = new Vector3(0f, 0f, radius / Mathf.Max(0.0001f, rootZ));
        orbitPivot.localRotation = Quaternion.identity;

        Transform hb = hitboxTarget != null ? hitboxTarget : orbitPivot;

        // Same widen-in-step-with-the-visual-model treatment as WeaponMeleePD — see
        // WeaponBasePD.ComputeScaleMultiplier's own doc comment.
        float xRatio = ComputeScaleMultiplier();
        Vector3 half = new(
            meleeHalfExtents.x * hb.lossyScale.x * xRatio,
            meleeHalfExtents.y * hb.lossyScale.y,
            meleeHalfExtents.z * hb.lossyScale.z);

        // Axehead capsule — see field comments for why both multipliers apply to each. axeHeadDepth is a
        // PERCENTAGE of the box's own current world depth (half.z*2), not a flat world-unit length — scales
        // proportionally for free as range/size change, no separate tracking needed.
        float axeHeadR          = axeHeadRadius    * xRatio;
        float axeHeadHalfWidthS = axeHeadHalfWidth * xRatio;
        float axeHeadDepth      = half.z * 2f * axeHeadPercent;

        _touchedThisTick.Clear();
        _touchedAlliesThisTick.Clear();
        _axeHeadHitsThisTick.Clear();
        _axeHeadAlliesThisTick.Clear();
        _tipHitsThisTick.Clear();
        Vector3 center;
        Quaternion hitboxRot;

        if (state.prevSet)
        {
            float angleDelta = _stats.WeaponSpeed * delta;
            float startAngle = currentAngle - angleSign * angleDelta;
            int subArcs = SweepMath.ComputeSubArcCount(angleDelta);

            Vector3 segStart  = hb.position;
            Vector3 segCenter = segStart;
            Quaternion segRot = hb.rotation;

            for (int step = 1; step <= subArcs; step++)
            {
                bool isLastStep = step == subArcs;
                float stepAngle = isLastStep ? currentAngle : Mathf.LerpAngle(startAngle, currentAngle, step / (float)subArcs);

                transform.rotation = tip * Quaternion.Euler(0f, stepAngle, 0f);
                hb.GetPositionAndRotation(out segCenter, out segRot);
                Vector3 segForward = segRot * Vector3.forward;
                Vector3 segRight   = segRot * Vector3.right;
                Vector3 segNearEdge = segCenter - segForward * half.z;
                Vector3 segFarEdge  = segCenter + segForward * half.z;

                // Axehead zone — centered in the outer axeHeadDepth-deep slice of the SAME full box, capsule
                // long axis along local X (sideways). Starting shape uses segStart (last substep's position)
                // with THIS substep's rotation, same start-position-plus-current-rotation approximation the
                // box's own BoxCastNonAlloc already relies on.
                Vector3 segZoneCenterStart = segStart + segForward * (half.z - axeHeadDepth * 0.5f);
                Vector3 segP1Start = segZoneCenterStart - segRight * axeHeadHalfWidthS;
                Vector3 segP2Start = segZoneCenterStart + segRight * axeHeadHalfWidthS;

                Vector3 sweep = segCenter - segStart;
                float dist = sweep.magnitude;
                if (dist > 0.001f)
                {
                    Vector3 sweepDir = sweep / dist;

                    int n = Physics.BoxCastNonAlloc(segStart, half, sweepDir,
                        _sweepResults, segRot, dist, CombatLayerMasks.EnemyHurtboxMask);
                    for (int i = 0; i < n; i++)
                    {
                        var enemy = _sweepResults[i].collider.GetComponent<IEnemyPD>();
                        if (enemy == null) continue;
                        _touchedThisTick.Add(enemy);
                        if (IsInTipZone(enemy.transform.position, segNearEdge, segFarEdge)) _tipHitsThisTick.Add(enemy);
                    }

                    int an = Physics.BoxCastNonAlloc(segStart, half, sweepDir,
                        _sweepResults, segRot, dist, CombatLayerMasks.FriendlyHurtboxMask);
                    for (int i = 0; i < an; i++)
                    {
                        var ally = _sweepResults[i].collider.GetComponent<IFriendlyPD>();
                        if (ally != null) _touchedAlliesThisTick.Add(ally);
                    }

                    int ac = Physics.CapsuleCastNonAlloc(segP1Start, segP2Start, axeHeadR, sweepDir,
                        _sweepResults, dist, CombatLayerMasks.EnemyHurtboxMask);
                    for (int i = 0; i < ac; i++)
                    {
                        var enemy = _sweepResults[i].collider.GetComponent<IEnemyPD>();
                        if (enemy == null) continue;
                        _touchedThisTick.Add(enemy);
                        _axeHeadHitsThisTick.Add(enemy);
                        if (IsInTipZone(enemy.transform.position, segNearEdge, segFarEdge)) _tipHitsThisTick.Add(enemy);
                    }

                    int aac = Physics.CapsuleCastNonAlloc(segP1Start, segP2Start, axeHeadR, sweepDir,
                        _sweepResults, dist, CombatLayerMasks.FriendlyHurtboxMask);
                    for (int i = 0; i < aac; i++)
                    {
                        var ally = _sweepResults[i].collider.GetComponent<IFriendlyPD>();
                        if (ally == null) continue;
                        _touchedAlliesThisTick.Add(ally);
                        _axeHeadAlliesThisTick.Add(ally);
                    }
                }

                segStart = segCenter;
            }

            center = segCenter;
            hitboxRot = segRot;
        }
        else
        {
            transform.rotation = tip * Quaternion.Euler(0f, currentAngle, 0f);
            hb.GetPositionAndRotation(out center, out hitboxRot);
        }

        Vector3 finalForward  = hitboxRot * Vector3.forward;
        Vector3 finalRight    = hitboxRot * Vector3.right;
        Vector3 finalNearEdge = center - finalForward * half.z;
        Vector3 finalFarEdge  = center + finalForward * half.z;

        // Static (unswept) axehead shape at the settled position — catches anything already overlapping at
        // rest, same safety-net role the box's own final OverlapBoxNonAlloc already serves (this is also the
        // ONLY axehead check on the very first tick, when state.prevSet is false and no substep loop ran).
        Vector3 finalZoneCenter = center + finalForward * (half.z - axeHeadDepth * 0.5f);
        Vector3 finalP1 = finalZoneCenter - finalRight * axeHeadHalfWidthS;
        Vector3 finalP2 = finalZoneCenter + finalRight * axeHeadHalfWidthS;

        int oc = Physics.OverlapBoxNonAlloc(center, half, _overlapResults, hitboxRot, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < oc; i++)
        {
            var enemy = _overlapResults[i].GetComponent<IEnemyPD>();
            if (enemy == null) continue;
            _touchedThisTick.Add(enemy);
            if (IsInTipZone(enemy.transform.position, finalNearEdge, finalFarEdge)) _tipHitsThisTick.Add(enemy);
        }

        int ao = Physics.OverlapBoxNonAlloc(center, half, _overlapResults, hitboxRot, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < ao; i++)
        {
            var ally = _overlapResults[i].GetComponent<IFriendlyPD>();
            if (ally != null) _touchedAlliesThisTick.Add(ally);
        }

        int fac = Physics.OverlapCapsuleNonAlloc(finalP1, finalP2, axeHeadR, _axeHeadOverlapResults, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < fac; i++)
        {
            var enemy = _axeHeadOverlapResults[i].GetComponent<IEnemyPD>();
            if (enemy == null) continue;
            _touchedThisTick.Add(enemy);
            _axeHeadHitsThisTick.Add(enemy);
            if (IsInTipZone(enemy.transform.position, finalNearEdge, finalFarEdge)) _tipHitsThisTick.Add(enemy);
        }

        int faac = Physics.OverlapCapsuleNonAlloc(finalP1, finalP2, axeHeadR, _axeHeadOverlapResults, CombatLayerMasks.FriendlyHurtboxMask);
        for (int i = 0; i < faac; i++)
        {
            var ally = _axeHeadOverlapResults[i].GetComponent<IFriendlyPD>();
            if (ally == null) continue;
            _touchedAlliesThisTick.Add(ally);
            _axeHeadAlliesThisTick.Add(ally);
        }

        if (!_stats.IsWeaponsDisabled)
        {
            foreach (var enemy in _touchedThisTick)
            {
                if (HitCooldownMath.TryStart(state.hitCooldowns, enemy.ObjectId, HitCooldown, delta))
                    Hit(enemy, _axeHeadHitsThisTick.Contains(enemy));
            }

            foreach (var ally in _touchedAlliesThisTick)
            {
                if (HitCooldownMath.TryStart(state.allyHitCooldowns, ally.ObjectId, HitCooldown, delta))
                    HitAlly(ally, _axeHeadAlliesThisTick.Contains(ally));
            }
        }

        state.prevSet = true;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!_initialized) TryInit();
        ApplyModelVisualAdjustments();
        UpdateWeaponParticlePause();
    }

    private void ApplyModelVisualAdjustments()
    {
        if (visualHeightTarget == null || _stats == null) return;
        Vector3 p = visualHeightTarget.localPosition;
        float localLift = _stats.IsOrbitFlipped ? 0f : WeaponVisualConstants.BaseMidHeightLift;
        visualHeightTarget.localPosition = new Vector3(p.x, localLift, p.z);
    }

    // ── Hit ───────────────────────────────────────────────────────────────────

    private void Hit(IEnemyPD enemy, bool isOuter)
    {
        if (enemy._isDead) return;
        float baseDamage = isOuter ? _stats.WeaponStrength : _stats.WeaponStrength * innerDamageMultiplier;
        float damage = RollDamage(baseDamage, out bool isHighroll);
        bool  isCrit = isOuter && GameRandom.instance.NextFloat() < _stats.CritChance;
        damage = WeaponDamageMath.ApplyCritAndHighroll(damage, isHighroll, isCrit, _stats.HighrollBonus, _stats.CritDamage);
        float onHitPower = isOuter ? _stats.WeaponOnHitPower : _stats.WeaponOnHitPower * innerOnHitPowerMultiplier;
        if (isHighroll) _stats.NotifyStrikeHighroll(damage, onHitPower);
        float weaponRadius = IsOuterTrack ? _stats.WeaponRadius + _stats.EffectiveWeaponRange : _stats.WeaponRadius;
        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = baseDamage, onHitPower = onHitPower, isCrit = isCrit, isHighroll = isHighroll, hitDistance = Vector3.Distance(_stats.transform.position, enemy.transform.position), weaponRadius = weaponRadius, weaponOffset = _stats.Size * 0.5f, weaponRangeMultiplier = _stats.weaponRangeScalingBonusPercent * _stats.weaponRangeScalingPenalty, isTip = _tipHitsThisTick.Contains(enemy) };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default, isHighroll, _stats.executeThreshold, _stats.critExecuteThreshold);
        Vector3 knockDir = (enemy.transform.position - _stats.transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback * onHitPower);
        NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = damage, isCrit = isCrit, isHighroll = isHighroll, attacker = _stats, isPriority = true });
    }

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
        float xRatio = ComputeScaleMultiplier();
        Vector3 half = new(
            meleeHalfExtents.x * hb.lossyScale.x * xRatio,
            meleeHalfExtents.y * hb.lossyScale.y,
            meleeHalfExtents.z * hb.lossyScale.z);
        float axeHeadR          = axeHeadRadius    * xRatio;
        float axeHeadHalfWidthS = axeHeadHalfWidth * xRatio;
        float axeHeadDepth      = half.z * 2f * axeHeadPercent;

        Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(hb.position, hb.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, half * 2f);
        Gizmos.matrix = prev;

        // Axehead capsule — flat stadium along local X, centered in the outer axeHeadDepth-deep slice.
        Vector3 zoneCenter = hb.position + hb.forward * (half.z - axeHeadDepth * 0.5f);
        Vector3 p1 = zoneCenter - hb.right * axeHeadHalfWidthS;
        Vector3 p2 = zoneCenter + hb.right * axeHeadHalfWidthS;
        Gizmos.color = new Color(1f, 0f, 0f, 0.4f);
        Gizmos.DrawWireSphere(p1, axeHeadR);
        Gizmos.DrawWireSphere(p2, axeHeadR);
        Gizmos.DrawLine(p1 + hb.up * axeHeadR, p2 + hb.up * axeHeadR);
        Gizmos.DrawLine(p1 - hb.up * axeHeadR, p2 - hb.up * axeHeadR);
        Gizmos.DrawLine(p1 + hb.forward * axeHeadR, p2 + hb.forward * axeHeadR);
        Gizmos.DrawLine(p1 - hb.forward * axeHeadR, p2 - hb.forward * axeHeadR);
    }

    // Outer-10%-of-total-hitbox-depth check, identical to WeaponMeleePD's own copy.
    // Independent of the handle/axehead split — a handle hit can still land in the tip zone.
    private static bool IsInTipZone(Vector3 enemyPos, Vector3 nearEdge, Vector3 farEdge)
    {
        Vector3 axis = farEdge - nearEdge;
        float totalLenSq = axis.sqrMagnitude;
        if (totalLenSq < 0.0001f) return false;
        float t = Vector3.Dot(enemyPos - nearEdge, axis) / totalLenSq;
        return t >= 0.9f;
    }
}
