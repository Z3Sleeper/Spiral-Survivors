using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Pivot-based orbit rework (2026-08-25) — replaced the old per-tick cos/sin position + LookRotation facing
// (still readable in WeaponMeleePDOld.cs, deprecated/unused, kept only for reference) with a child orbitPivot
// transform whose local position carries the live orbit radius (a single float write, not a vector built
// from cos/sin) and whose parent (this weapon's own root) rotation carries the sweep angle via a plain
// Quaternion.Euler — position and facing both fall out of ordinary Unity hierarchy composition instead of
// being separately recomputed every substep of the hit-sweep. Same trick the King Orbiter indicator's own
// pivot fix already used, generalized to melee weapon orbiting; validated live across WeaponRange, Size,
// and WeaponRadius on Knight/Rogue/Bladeweaver's prefabs.
//
// Prefab contract: orbitPivot is a child of this weapon's root, empty of any local-offset meaning of its own
// (script overwrites its localPosition every tick). Visuals and hitboxTarget are both children of orbitPivot
// (not nested under each other any deeper than they already were) — their own local positions/rotations
// relative to their parent are whatever small internal-geometry offsets they've always represented (the
// orbit radius itself lives on orbitPivot, not baked into them). Whatever local rotation the model sits at
// when orbitPivot's rotation is identity IS this weapon's "angle zero" facing.
//
// The PredictedTransform component (graphics smoothing) MUST live on orbitPivot itself, not on this weapon's
// root — root no longer carries the orbit radius at all, so PredictedTransform reading root's own transform
// would leave Visuals/Model completely blind to it, frozen as if WeaponRadius were always 0. Safe against
// PurrDiction's actual tick loop (PredictionManager.SimulateFrame*): every identity's LateSimulate() —
// including this weapon's own, which is what actually moves orbitPivot — finishes for the WHOLE scene before
// ANY identity's GetLatestUnityState() (PredictedTransform's own capture hook) runs, as two strictly
// separate, sequential passes. That barrier holds regardless of which GameObject within a spawned prefab
// each identity lives on, so orbitPivot's position/rotation is always fully up to date by the time
// PredictedTransform captures it — no dual-write, no per-frame fighting over Visuals; Model just stays a
// plain, statically-authored child, same as Guard/Hilt/Connector always were.
//
// IsOrbitFlipped (RogueSpinAbilityPD's vertical-plane leap) is supported — see FlipTipRotation's own field
// comment for the composition. Confirmed live: position tracks the player's own body center (via the
// inherited ApplyHeightLift(), unconditionally — no flip-branching needed there at all, since that method
// already adds the Size-scaled body-center lift specifically while IsOrbitFlipped), and the model's own rest
// orientation already slices edge-first once rigidly tipped, with no extra local rotation needed — see
// ApplyModelVisualAdjustments's own comment for why this reuses WeaponBasePD's visualHeightTarget directly
// (widened to protected) rather than a separate Inspector reference.
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

    // The new pivot — see this file's own class-level doc comment for the full contract. Root tracks
    // position (the orbit origin) and rotation (the orbit angle); this transform's LOCAL position is
    // overwritten every tick with (0, 0, radius) — a plain magnitude, no trig — and Visuals/hitboxTarget
    // hang off it as children, inheriting both the radius offset and root's sweep rotation automatically.
    [SerializeField] private Transform orbitPivot;

    // Same sign/composition as WeaponBasePD's own OrbitFlipTip — RogueSpinAbilityPD (the one caller of
    // CharacterStatsPD.SetOrbitFlipped) is unchanged and untouched by the pivot rework, so this just needs
    // to reproduce the same "stand the sweep plane up, oriented toward OrbitFacingYaw" composition the old
    // per-tick cos/sin math used, applied once as an extra rotation layered on top of the ordinary sweep
    // (Quaternion.Euler(0, stepAngle, 0)) instead of baked into a one-off position formula. Composed as the
    // OUTERMOST rotation (tip * sweep) so it tips the whole circle — position, facing, hit-sweep sub-
    // stepping, all of it — rather than needing separate handling anywhere.
    private static readonly Quaternion FlipTipRotation = Quaternion.Euler(0f, 0f, -90f);

    private bool _initialized;

    private readonly RaycastHit[] _sweepResults = new RaycastHit[32];
    private readonly Collider[] _overlapResults = new Collider[32];
    private readonly HashSet<IEnemyPD> _touchedThisTick = new();
    private readonly HashSet<IFriendlyPD> _touchedAlliesThisTick = new();

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

    // WeaponBasePD.NominalOrbitRadius() is private, so it can't be called from here — duplicated rather
    // than touching WeaponBasePD (this whole prototype is meant to be zero-risk to the real weapon system).
    // Same formula, same meaning: nominal distance from the orbit origin to where this weapon's root sits.
    private float NominalOrbitRadius()
    {
        float halfRange = rangeScaleTarget != null
            ? designOrbit * RootScale.z
            : (IsMelee ? RangeValue / 2f : 0.25f);
        float weaponRadiusOffset = IsOuterTrack ? _stats.WeaponRadius + _stats.WeaponRange : _stats.WeaponRadius;
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

        HitCooldownMath.Tick(state.hitCooldowns);
        HitCooldownMath.Tick(state.allyHitCooldowns);

        // Negated (2026-08-25) — the pivot's rotation-only sweep came out spinning counterclockwise by
        // default (opposite axis-convention parity from the old cos/sin+LookRotation math), backwards from
        // the clockwise motion WeaponMeleePD always produced. Flipping the sign here, uniformly, reverses
        // BOTH the sweep rotation AND the hit-detection sub-stepping direction together (both derive from
        // this same currentAngle/startAngle) — orbitPivot's own RADIUS write further down never depends on
        // angle sign at all (magnitude only), so nothing else needs touching for this.
        float angleSign = IsOuterTrack ? -1f : 1f;
        float currentAngle = -(angleSign * _stats.WeaponOrbitAngle + SlotAngleOffset());

        // Extra outermost rotation layered onto the ordinary flat sweep while IsOrbitFlipped (e.g.
        // RogueSpinAbilityPD's leap) — see FlipTipRotation's own field comment. Fixed for the whole tick
        // (OrbitFacingYaw/IsOrbitFlipped don't change intra-tick), so computed once here rather than per
        // substep below.
        Quaternion tip = _stats.IsOrbitFlipped
            ? Quaternion.Euler(0f, _stats.OrbitFacingYaw, 0f) * FlipTipRotation
            : Quaternion.identity;

        float radius = NominalOrbitRadius();
        if (radius <= 0.0001f) return;

        // Root just tracks the origin — no cos/sin, the radius lives on orbitPivot's local offset instead.
        // ApplyHeightLift() (inherited, unmodified) already adds SpecialHeightOffset (the player's own live
        // leap-arc height) unconditionally, PLUS WeaponVisualConstants.BaseMidHeightLift * bonusSizePercent
        // specifically while IsOrbitFlipped — i.e. exactly the player's own body CENTER (0.5 for the default
        // 1-unit sphere, scaling with Size), not their ground-level root. Two earlier attempts at this (a
        // flat 0, then SpecialHeightOffset + radius) were both reinventing this same term and getting it
        // wrong — this already-inherited call is the correct one, unconditionally, no flip-branching needed
        // here at all: the standing circle's CENTER now tracks the player's own body center, leaping with
        // them, with the circle extending symmetrically above/below that by radius.
        Vector3 origin = _stats.EffectiveOrbitOrigin;
        transform.position = ApplyHeightLift(origin);
        // Counter-scaled by rootZ (root's own Size-driven localScale.z) — orbitPivot is root's CHILD, so its
        // local position would otherwise get implicitly multiplied by root's own scale when resolving to
        // world space, unlike the old system where root's world position was assigned directly with no
        // parent to inherit scale from. radius's own bonusSizePercent clearance term is deliberately meant
        // to stay a flat, unscaled world-space addend (same reasoning WeaponManagerPD.SetupWeaponRange's own
        // hitboxTarget division already documents) — without this, that term got scaled by Size a second
        // time, ballooning the whole orbit outward during Size buffs.
        float rootZ = transform.localScale.z;
        orbitPivot.localPosition = new Vector3(0f, 0f, radius / Mathf.Max(0.0001f, rootZ));

        Transform hb = hitboxTarget != null ? hitboxTarget : orbitPivot;

        float xRatio = xRangeScaleRatio > 0f
            ? Mathf.Lerp(1f, hb.lossyScale.z / Mathf.Max(0.001f, designOrbit * transform.localScale.z), xRangeScaleRatio)
            : 1f;
        Vector3 half = new(
            meleeHalfExtents.x * hb.lossyScale.x * xRatio,
            meleeHalfExtents.y * hb.lossyScale.y,
            meleeHalfExtents.z * hb.lossyScale.z);

        // centerShift is the NET forward shift. Growing half.z by (tipExtra+backTipExtra)/2 while shifting
        // center by (tipExtra-backTipExtra)/2 means the front edge only ever moves by tipExtra and the back
        // edge only ever moves by backTipExtra — the growth term and the shift term cancel out for whichever
        // end ISN'T being extended, so front/back extensions never interfere with each other.
        float tipExtra = GetHitboxTipExtraReach();
        float backTipExtra = GetHitboxBackTipExtraReach();
        float centerShift = (tipExtra - backTipExtra) * 0.5f;
        half.z += (tipExtra + backTipExtra) * 0.5f;

        _touchedThisTick.Clear();
        _touchedAlliesThisTick.Clear();
        Vector3 center;
        Quaternion hitboxRot;

        if (state.prevSet)
        {
            // hb's rotation here is still last tick's — root's rotation hasn't been touched yet this tick.
            Vector3 prevCenter = hb.position + hb.rotation * Vector3.forward * centerShift;

            float angleDelta = _stats.WeaponSpeed * delta;
            // Angle at the START of this tick, before this tick's movement. Sign untested against the real
            // spin direction yet — if the sub-arc sweep looks like it's sampling backward once this is
            // live-tested, flip this to "+ angleSign * angleDelta" instead.
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
                segCenter += segRot * Vector3.forward * centerShift;

                Vector3 sweep = segCenter - segStart;
                float dist = sweep.magnitude;
                if (dist > 0.001f)
                {
                    int n = Physics.BoxCastNonAlloc(segStart, half, sweep / dist,
                        _sweepResults, segRot, dist, CombatLayerMasks.EnemyHurtboxMask);
                    for (int i = 0; i < n; i++)
                    {
                        var enemy = _sweepResults[i].collider.GetComponent<IEnemyPD>();
                        if (enemy != null) _touchedThisTick.Add(enemy);
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
            center += hitboxRot * Vector3.forward * centerShift;
        }

        int oc = Physics.OverlapBoxNonAlloc(center, half, _overlapResults, hitboxRot, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < oc; i++)
        {
            var enemy = _overlapResults[i].GetComponent<IEnemyPD>();
            if (enemy != null) _touchedThisTick.Add(enemy);
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

    // Own version of WeaponBasePD.ApplyVisualAdjustments() — reuses the SAME (now protected) visualHeightTarget
    // field rather than a separate Inspector reference, since ApplyVisualAdjustments() itself is still used
    // as-is by ExeAxeThrowProjectilePD (still on the old orbit system) and shouldn't be touched. Position-
    // lift half: 0 while flipped — the leap-arc/body-center lift already lives on root itself now, see
    // LateSimulate's own ApplyHeightLift() call — vs. BaseMidHeightLift while flat, same as before. The old
    // system's own local-rotation tip (ModelTipRotation, calibrated for a LookRotation-based flip this
    // script doesn't use) isn't needed at all under this rigid-composition approach — the model's own rest
    // orientation already slices edge-first once tipped, confirmed live. (2026-08-25: an idle wobble sway
    // used to live here too — tried, tuned, and fully reverted after live testing showed it read as
    // negligible/off-putting from a top-down view; see project_pd_backlog memory for the full history if
    // this ever gets revisited.)
    private void ApplyModelVisualAdjustments()
    {
        if (visualHeightTarget == null || _stats == null) return;
        Vector3 p = visualHeightTarget.localPosition;
        float localLift = _stats.IsOrbitFlipped ? 0f : WeaponVisualConstants.BaseMidHeightLift;
        visualHeightTarget.localPosition = new Vector3(p.x, localLift, p.z);
    }

    // ── Hit ───────────────────────────────────────────────────────────────────

    private void Hit(IEnemyPD enemy)
    {
        if (enemy._isDead) return;
        float damage = RollDamage(_stats.WeaponStrength, out bool isHighroll);
        bool isCrit = GameRandom.instance.NextFloat() < _stats.CritChance;
        damage = WeaponDamageMath.ApplyCritAndHighroll(damage, isHighroll, isCrit, _stats.HighrollBonus, _stats.CritDamage);
        if (isHighroll) _stats.NotifyStrikeHighroll(damage, _stats.WeaponOnHitPower);
        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = _stats.WeaponStrength, onHitPower = _stats.WeaponOnHitPower, isCrit = isCrit };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
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
        float xRatio = (xRangeScaleRatio > 0f && designOrbit > 0f)
            ? Mathf.Lerp(1f, hb.lossyScale.z / Mathf.Max(0.001f, designOrbit * transform.localScale.z), xRangeScaleRatio)
            : 1f;
        Vector3 half = new(
            meleeHalfExtents.x * hb.lossyScale.x * xRatio,
            meleeHalfExtents.y * hb.lossyScale.y,
            meleeHalfExtents.z * hb.lossyScale.z);
        float tipExtra = GetHitboxTipExtraReach();
        float backTipExtra = GetHitboxBackTipExtraReach();
        float centerShift = (tipExtra - backTipExtra) * 0.5f;
        half.z += (tipExtra + backTipExtra) * 0.5f;
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(hb.position, hb.rotation, Vector3.one);
        Gizmos.DrawWireCube(new Vector3(0f, 0f, centerShift), half * 2f);
        Gizmos.matrix = prev;
    }
}
