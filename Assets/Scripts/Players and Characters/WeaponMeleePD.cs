using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Melee weapon hit detection (2026-09-28 rework, since renamed — see WeaponBasePD.cs's own class doc comment
// for the full migration history). The hit-detection box never grows/shifts for a tip/backtip decoration at
// all, unlike the old pre-rework system's own GetHitboxTipExtraReach/GetHitboxBackTipExtraReach mechanism
// (deleted along with it — see WeaponSharedPD.cs). meleeHalfExtents.z * hb.lossyScale.z IS the hitbox's whole
// depth now, full stop; hitboxHilt/hitboxTip (WeaponBasePD) only describe how that already-fixed total is
// split up for the MODEL's own bucket positions, never for extending the real hitbox beyond it.
public class WeaponMeleePD : WeaponBasePD<WeaponMeleePD.State>
{
    public struct State : IPredictedData<State>
    {
        public bool prevSet;
        public DisposableList<HitCooldownEntry> hitCooldowns;
        public DisposableList<AllyHitCooldownEntry> allyHitCooldowns;
        public void Dispose()
        {
            hitCooldowns.Dispose();
            allyHitCooldowns.Dispose();
        }
    }

    [SerializeField] private Vector3 meleeHalfExtents = new(0.1f, 0.1f, 0.5f);

    [SerializeField] private Transform orbitPivot;

    // Editor-only preview (2026-09-29, moved here from WeaponBasePD — [ContextMenu] doesn't work on
    // a method only inherited from an open generic base class, Unity's menu-command reflection can't bind to
    // it even though this concrete subclass isn't generic itself; confirmed via the "is generic and cannot be
    // used for menu commands" console warning). ScaleModel() only ever actually runs once a live
    // CharacterStatsPD registers this weapon (WeaponManagerPD.RegisterWeapon, Play mode only), so outside Play
    // mode Blade/Tip/the particle bucket just sit at whatever raw, unscaled state they were last left in — NOT
    // representative of how they'll actually line up at runtime. [ContextMenu], not [ExecuteAlways]/
    // OnValidate() — this is a PredictedIdentity, and running anything automatically during its own edit-mode
    // lifecycle risks interfering with PurrDiction's own Awake/OnEnable expectations; a manual, on-demand
    // right-click action has no such risk. Right-click this component's header in the Inspector -> "Preview
    // Range In Editor" to see Blade/Tip/particles laid out as they'd actually appear at editorPreviewRange,
    // without entering Play mode. Hilt is unaffected either way (its own Z position was never script-driven in
    // the first place — see hiltParent's own field comment on WeaponBasePD).
    [SerializeField] private float editorPreviewRange = 10f;

    [ContextMenu("Preview Range In Editor")]
    private void PreviewInEditor()
    {
        InitModelVisuals();
        ScaleModel(editorPreviewRange);
    }

    private static readonly Quaternion FlipTipRotation = Quaternion.Euler(0f, 0f, -90f);

    private bool _initialized;

    private readonly RaycastHit[] _sweepResults = new RaycastHit[32];
    private readonly Collider[] _overlapResults = new Collider[32];
    private readonly HashSet<IEnemyPD> _touchedThisTick = new();
    private readonly HashSet<IFriendlyPD> _touchedAlliesThisTick = new();
    private readonly HashSet<IEnemyPD> _tipHitsThisTick = new();

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

    // NominalOrbitRadius() is inherited directly from WeaponBasePD now (protected there) — no need
    // to duplicate it the way the original WeaponMeleePD had to (that was specifically to avoid touching the
    // shared WeaponBasePD other legacy weapon types still depend on; this new base has no such constraint).

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

        // Widens in step with the visual model's own resize now (2026-09-29 — was its own separate,
        // similarly hitboxTip-baseline-broken ratio) — see WeaponBasePD.ComputeScaleMultiplier's
        // own doc comment.
        float xRatio = ComputeScaleMultiplier();
        Vector3 half = new(
            meleeHalfExtents.x * hb.lossyScale.x * xRatio,
            meleeHalfExtents.y * hb.lossyScale.y,
            meleeHalfExtents.z * hb.lossyScale.z);

        // No more centerShift/half.z growth for a tip/backtip decoration — meleeHalfExtents.z * hb.lossyScale.z
        // IS the hitbox's whole depth now (see class doc comment). center is exactly hb's own position.
        _touchedThisTick.Clear();
        _touchedAlliesThisTick.Clear();
        _tipHitsThisTick.Clear();
        Vector3 center;
        Quaternion hitboxRot;

        if (state.prevSet)
        {
            Vector3 prevCenter = hb.position;

            float angleDelta = _stats.WeaponSpeed * delta;
            float startAngle = currentAngle - angleSign * angleDelta;
            int subArcs = SweepMath.ComputeSubArcCount(angleDelta);

            Vector3 segStart = prevCenter;
            Vector3 segCenter = prevCenter;
            Quaternion segRot = hb.rotation;

            for (int step = 1; step <= subArcs; step++)
            {
                bool isLastStep = step == subArcs;
                float stepAngle = isLastStep ? currentAngle : Mathf.LerpAngle(startAngle, currentAngle, step / (float)subArcs);

                transform.rotation = tip * Quaternion.Euler(0f, stepAngle, 0f);
                hb.GetPositionAndRotation(out segCenter, out segRot);

                Vector3 sweep = segCenter - segStart;
                float dist = sweep.magnitude;
                if (dist > 0.001f)
                {
                    Vector3 segForward = segRot * Vector3.forward;
                    Vector3 segNearEdge = segCenter - segForward * half.z;
                    Vector3 segFarEdge  = segCenter + segForward * half.z;

                    int n = Physics.BoxCastNonAlloc(segStart, half, sweep / dist,
                        _sweepResults, segRot, dist, CombatLayerMasks.EnemyHurtboxMask);
                    for (int i = 0; i < n; i++)
                    {
                        var enemy = _sweepResults[i].collider.GetComponent<IEnemyPD>();
                        if (enemy == null) continue;
                        _touchedThisTick.Add(enemy);
                        if (IsInTipZone(enemy.transform.position, segNearEdge, segFarEdge)) _tipHitsThisTick.Add(enemy);
                    }

                    int an = Physics.BoxCastNonAlloc(segStart, half, sweep / dist,
                        _sweepResults, segRot, dist, CombatLayerMasks.FriendlyHurtboxMask);
                    for (int i = 0; i < an; i++)
                    {
                        var ally = _sweepResults[i].collider.GetComponent<IFriendlyPD>();
                        if (ally != null) _touchedAlliesThisTick.Add(ally);
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

        Vector3 finalForward = hitboxRot * Vector3.forward;
        Vector3 finalNearEdge = center - finalForward * half.z;
        Vector3 finalFarEdge  = center + finalForward * half.z;

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

        if (!_stats.IsWeaponsDisabled)
        {
            foreach (var enemy in _touchedThisTick)
            {
                if (HitCooldownMath.TryStart(state.hitCooldowns, enemy.ObjectId, HitCooldown, delta))
                    Hit(enemy);
            }

            foreach (var ally in _touchedAlliesThisTick)
            {
                if (HitCooldownMath.TryStart(state.allyHitCooldowns, ally.ObjectId, HitCooldown, delta))
                    HitAlly(ally);
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

    // ── Tip ───────────────────────────────────────────────────────────────────

    private static bool IsInTipZone(Vector3 enemyPos, Vector3 nearEdge, Vector3 farEdge)
    {
        Vector3 axis = farEdge - nearEdge;
        float totalLenSq = axis.sqrMagnitude;
        if (totalLenSq < 0.0001f) return false;
        float t = Vector3.Dot(enemyPos - nearEdge, axis) / totalLenSq;
        return t >= 0.9f;
    }

    // ── Hit ───────────────────────────────────────────────────────────────────

    private void Hit(IEnemyPD enemy)
    {
        if (enemy._isDead) return;
        float damage = RollDamage(_stats.WeaponStrength, out bool isHighroll);
        bool isCrit = GameRandom.instance.NextFloat() < _stats.CritChance;
        damage = WeaponDamageMath.ApplyCritAndHighroll(damage, isHighroll, isCrit, _stats.HighrollBonus, _stats.CritDamage);
        if (isHighroll) _stats.NotifyStrikeHighroll(damage, _stats.WeaponOnHitPower);
        float weaponRadius = IsOuterTrack ? _stats.WeaponRadius + _stats.EffectiveWeaponRange : _stats.WeaponRadius;
        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = _stats.WeaponStrength, onHitPower = _stats.WeaponOnHitPower, isCrit = isCrit, isHighroll = isHighroll, hitDistance = Vector3.Distance(_stats.transform.position, enemy.transform.position), weaponRadius = weaponRadius, weaponOffset = _stats.bonusSizePercent * 0.5f, weaponRangeMultiplier = _stats.weaponRangeScalingBonusPercent * _stats.weaponRangeScalingPenalty, isTip = _tipHitsThisTick.Contains(enemy) };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default, isHighroll);
        Vector3 knockDir = (enemy.transform.position - _stats.transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback * hitData.onHitPower);
        NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = damage, isCrit = isCrit, isHighroll = isHighroll, attacker = _stats, isPriority = true });
    }

    private void HitAlly(IFriendlyPD ally)
    {
        if (ReferenceEquals(ally, _stats) || ally.IsDead) return;
        _stats.NotifyAllyStrike(new AllyStrikeData { ally = ally, onHitPower = _stats.WeaponOnHitPower });
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────

    private void OnDrawGizmos()
    {
        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        // ComputeScaleMultiplier() already no-ops safely (returns 1f) if _stats is null — same guard this
        // gizmo draw needs for viewing the prefab in isolation, outside a live scene.
        float xRatio = ComputeScaleMultiplier();
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
