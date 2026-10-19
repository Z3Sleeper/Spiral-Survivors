using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Ranged weapon orbit/fire logic (2026-09-30 rework, since renamed — see WeaponBasePD.cs's own class doc
// comment for the full migration history). This weapon type never had any per-model-piece visual fields to
// begin with — WeaponManagerPD.SetupWeaponRange early-returns for !IsMelee before any of that geometry ever
// runs. The one real behavioral change from the pre-rework version: the orbit radius's own "how far the
// muzzle sits out from Pivot" term used to be COMPUTED once, snapshotted from a rangeScaleTarget transform's
// own authored localPosition.z + localScale.z*0.5f at init — that field was a plain positioning trick, never
// actually used for any Blade-style resizing. WeaponBasePD has no equivalent snapshot-from-a-transform
// mechanism (its own designOrbit field is a dead NonSerialized placeholder), so this is now just a plain
// authored muzzleOrbitOffset constant instead (same "flat authored constant, not derived from a live
// transform" convention hitboxHilt/hitboxTip already use) — can't be mechanically copied from the old
// rangeScaleTarget-derived value, since that was itself derived, not authored; re-author per-weapon by
// eye/playtest instead. See GetNonBladeHalfRange's own doc comment on the base class for how this plugs into
// NominalOrbitRadius().
public class WeaponRangedPD : WeaponBasePD<WeaponRangedPD.State>
{
    public struct State : IPredictedData<State>
    {
        // Progress toward the next shot (0..1, one full fire cycle) — see WeaponRangedPD's own identical field
        // doc comment for the full reasoning (continuous rate-weighted accumulator, re-read fresh every tick).
        public float fireProgress;
        public DisposableList<HitCooldownEntry> hitCooldowns;
        // Replicated backing for IsOuterTrack/SlotIndex (2026-10-16) — see WeaponBasePD.IsOuterTrack's own
        // doc comment for the full "why".
        public bool isOuterTrack;
        public int  slotIndex;
        public void Dispose()
        {
            hitCooldowns.Dispose();
        }
    }

    // See WeaponBasePD.IsOuterTrack/SlotIndex's own doc comment.
    public override bool IsOuterTrack { get => currentState.isOuterTrack; set => currentState.isOuterTrack = value; }
    public override int  SlotIndex    { get => currentState.slotIndex;    set => currentState.slotIndex    = value; }

    public override bool IsMelee => false;

    [Header("Ranged")]
    [SerializeField] private GameObject projectilePrefab;

    // See WeaponMeleePD's own orbitPivot field comment for the full contract — identical here.
    [SerializeField] private Transform orbitPivot;

    // Replaces the old computed designOrbit — see class doc comment for why this is now a plain authored
    // constant instead of a snapshot off rangeScaleTarget's own transform. Feeds GetNonBladeHalfRange() below,
    // which the shared NominalOrbitRadius() (inherited, not duplicated — bladeParent is always null for this
    // weapon type) reads automatically.
    [SerializeField] private float muzzleOrbitOffset = 0.25f;
    protected override float GetNonBladeHalfRange() => muzzleOrbitOffset * RootScale.z;

    // Same sign/composition as WeaponMeleePD's own FlipTipRotation.
    private static readonly Quaternion FlipTipRotation = Quaternion.Euler(0f, 0f, -90f);

    private bool _initialized;

    // Same per-enemy hit cooldown as WeaponMeleePD — an enemy currently on cooldown is skipped when
    // picking a target, but if firing at a different eligible target happens to also hit a cooling-down
    // enemy along the way (e.g. it was standing behind the intended target), that's fine; the cooldown
    // only gates target SELECTION, not the projectile's own hit detection.
    private float HitCooldown => _stats != null ? 135f / _stats.WeaponSpeed : 0.25f;

    private readonly RaycastHit[] _rayResults = new RaycastHit[16];

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
        // Not ready yet — see WeaponManagerPD.IsInitialized's own doc comment (same race WeaponMeleePD's
        // identical TryInit() hit, confirmed live as a NullReferenceException in SetupWeaponRange).
        if (!mgr.IsInitialized) return;
        _manager = mgr;
        _initialized = true;
        mgr.RegisterWeapon(this);
    }

    protected override void Destroyed()
    {
        if (_manager != null) _manager.UnregisterWeapon(this);
    }

    // isOuterTrack/slotIndex explicitly carried forward (2026-10-16 fix) — see WeaponMeleePD.GetInitialState's
    // own doc comment for the full "why" (fresh-spawn LateAwake->RegisterWeapon runs before this assignment,
    // which would otherwise silently wipe RegisterWeapon's own classification back to false/0).
    protected override State GetInitialState() => new()
    {
        hitCooldowns = DisposableList<HitCooldownEntry>.Create(4),
        isOuterTrack = currentState.isOuterTrack,
        slotIndex = currentState.slotIndex,
    };

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!_initialized) TryInit();
        ApplyModelVisualAdjustments();
    }

    // Same as WeaponMeleePD's own ApplyModelVisualAdjustments — position-only, no rotation touch
    // needed here (unlike the base class's own ApplyVisualAdjustments, which also flips rotation for Melee).
    private void ApplyModelVisualAdjustments()
    {
        if (visualHeightTarget == null || _stats == null) return;
        Vector3 p = visualHeightTarget.localPosition;
        float localLift = _stats.IsOrbitFlipped ? 0f : WeaponVisualConstants.BaseMidHeightLift;
        visualHeightTarget.localPosition = new Vector3(p.x, localLift, p.z);
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    // Empty — orbit angle is ticked once, shared, on CharacterStatsPD; this weapon just reads
    // _stats.WeaponOrbitAngle in LateSimulate() instead of maintaining its own copy.
    protected override void Simulate(ref State state, float delta) { }

    private float SlotAngleOffset()
    {
        int total = _manager != null ? Mathf.Max(1, IsOuterTrack ? _manager.OuterWeaponCount : _manager.WeaponCount) : 1;
        return 360f / total * SlotIndex;
    }

    // Same post-physics-pass timing reasoning as WeaponMeleePD.LateSimulate() — _stats.transform.
    // position (and therefore this weapon's own orbital position) needs to reflect the player's current-tick,
    // post-physics position, not a tick-stale one.
    protected override void LateSimulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (orbitPivot == null) return;

        // Tick cooldowns here (deterministic, tick-based delta) rather than Time.deltaTime in UpdateView()
        // — see HitCooldownEntry's own doc comment for why the old approach was unsafe under resimulation.
        HitCooldownMath.Tick(state.hitCooldowns);

        // ArcherFocusFireAbilityPD freezes the whole ring by substituting FocusFireAimAngle (cursor-derived,
        // shared by every weapon this owner has) for the normal ever-incrementing WeaponOrbitAngle — see
        // CharacterStatsPD.FocusFireAimAngle's own doc comment.
        float baseAngle    = _stats.ContinuousFire ? _stats.FocusFireAimAngle : _stats.WeaponOrbitAngle;
        float angleSign    = IsOuterTrack ? -1f : 1f;
        // +90 — FocusFireAimAngle is computed as Atan2(dir.z, dir.x), the exact inverse of the OLD cos/sin
        // orbit formula's own position mapping — see WeaponRangedPD's own identical comment for the full
        // derivation history.
        float currentAngle = 90f - (angleSign * baseAngle + SlotAngleOffset());

        Quaternion tip = _stats.IsOrbitFlipped
            ? Quaternion.Euler(0f, _stats.OrbitFacingYaw, 0f) * FlipTipRotation
            : Quaternion.identity;

        float radius = NominalOrbitRadius();
        if (radius <= 0.0001f) return;

        // No sub-arc reconstruction here (unlike Melee/Axe) — this weapon does no hit-detection sweep of its
        // own, just samples ONE current position/direction per tick for raycasting, so only the final angle
        // matters, never an intermediate one.
        Vector3 origin = _stats.EffectiveOrbitOrigin;
        transform.position = ApplyHeightLift(origin);
        transform.rotation = tip * Quaternion.Euler(0f, currentAngle, 0f);

        float rootZ = transform.localScale.z;
        orbitPivot.localPosition = new Vector3(0f, 0f, radius / Mathf.Max(0.0001f, rootZ));
        // Forced to identity every tick — PredictedTransform's own Rollback() leaves a stray local rotation on
        // orbitPivot every resimulation otherwise, same fix WeaponMeleePD/WeaponAxePD use.
        orbitPivot.localRotation = Quaternion.identity;

        // The effective firing/aiming point — hitboxTarget if a dedicated muzzle child is wired (e.g. the
        // bow's own tip), orbitPivot itself otherwise.
        Transform muzzle = hitboxTarget != null ? hitboxTarget : orbitPivot;

        // Skipped entirely while _stats.IsWeaponsDisabled — fireProgress is deliberately left untouched too,
        // so it resumes exactly where it left off once re-enabled rather than losing progress.
        if (_stats.IsWeaponsDisabled) return;

        state.fireProgress += _stats.AttackSpeed * _stats.FireRate * delta;
        if (state.fireProgress < 1f) return;

        if (_stats.ContinuousFire)
        {
            if (!_stats.ContinuousFireHasTarget) return;
            // homingTargetId/homingDelay: default/0f (no homing) for every FocusFire user without
            // ArcherFocusFireMultishotHomingAugment — see CharacterStatsPD.FocusFireHomingTargetId's own
            // doc comment for why this is always safe to pass through unconditionally.
            Fire(muzzle.position + muzzle.forward * EffectiveWeaponRange, _stats.FocusFireHomingTargetId, _stats.FocusFireHomingDelay);
            state.fireProgress = 0f;
            return;
        }

        if (!FindTarget(muzzle.position, muzzle.forward, state.hitCooldowns, out IEnemyPD target, out Vector3 hurtboxCenter)) return;

        HitCooldownMath.TryStart(state.hitCooldowns, target.ObjectId, HitCooldown, delta);
        Fire(hurtboxCenter);
        // Hard reset, not -= 1f — see WeaponRangedPD's own identical comment for why.
        state.fireProgress = 0f;
    }

    // ── Targeting ─────────────────────────────────────────────────────────────

    // Pulls the ray origin back 1 unit and pads the range by 1 to match — same as WeaponRangedPD/the old
    // WeaponBow — so an enemy pressed right up against the weapon's own origin still gets hit instead of the
    // ray starting inside/past it. Enemy layer only — this weapon never aims at an ally.
    private bool FindTarget(Vector3 from, Vector3 forward, DisposableList<HitCooldownEntry> hitCooldowns,
        out IEnemyPD enemy, out Vector3 hurtboxCenter)
    {
        Vector3 origin = from - forward * 1f;
        float   range  = EffectiveWeaponRange + 1f;

        int   n        = Physics.RaycastNonAlloc(origin, forward, _rayResults, range, CombatLayerMasks.EnemyHurtboxMask);
        float bestDist = float.MaxValue;
        enemy = null;
        hurtboxCenter = default;

        for (int i = 0; i < n; i++)
        {
            if (_rayResults[i].distance >= bestDist) continue;
            var collider = _rayResults[i].collider;

            var e = collider.GetComponent<IEnemyPD>();
            if (e == null || e._isDead || HitCooldownMath.IsOnCooldown(hitCooldowns, e.ObjectId)) continue;
            bestDist      = _rayResults[i].distance;
            enemy         = e;
            hurtboxCenter = collider.bounds.center;
        }
        return enemy != null;
    }

    // Auto-aim targeting for ArcherFocusFireAbilityPD's own aim direction — see WeaponRangedPD's own identical
    // method doc comment for the full reasoning (deterministic/replay-safe fixed-radius query, 128-slot buffer
    // since OverlapSphereNonAlloc doesn't return results in distance order). Moved here from WeaponRangedPD as
    // part of the Normalized migration (2026-09-30) — stateless/static, so this is a safe move regardless of
    // which class is actually wired on any given weapon prefab; ArcherFocusFireAbilityPD's own call site now
    // points here instead.
    private static readonly Collider[] _autoAimResults = new Collider[128];

    // targetId: the same closest enemy's own ObjectId, output alongside direction (2026-10-06,
    // ArcherFocusFireMultishotHomingAugment) — ArcherFocusFireAbilityPD.UpdateAutoAimAngle carries this into
    // CharacterStatsPD.FocusFireHomingTargetId so a fired projectile can keep re-resolving the SAME enemy
    // every tick instead of a one-shot direction that goes stale the instant it moves. default when nothing
    // was found (found == false), same as direction's own default.
    public static bool TryFindAutoAimDirection(Vector3 from, float range, out Vector3 direction, out PredictedObjectID targetId)
    {
        int   n        = Physics.OverlapSphereNonAlloc(from, range, _autoAimResults, CombatLayerMasks.EnemyHurtboxMask);
        float bestDist = float.MaxValue;
        direction = default;
        targetId  = default;
        bool found = false;

        for (int i = 0; i < n; i++)
        {
            var enemy = _autoAimResults[i].GetComponent<IEnemyPD>();
            if (enemy == null || enemy._isDead) continue;

            float dist = Vector3.Distance(from, enemy.transform.position);
            if (dist >= bestDist) continue;

            bestDist  = dist;
            direction = (enemy.transform.position - from).normalized;
            targetId  = enemy.ObjectId;
            found     = true;
        }
        return found;
    }

    // ── Fire ──────────────────────────────────────────────────────────────────

    private static readonly List<float> _multishotAngles = new();

    // Ricochet/multishot fractional-roll mechanic — see WeaponRangedPD's own identical method doc comment.
    // homingTargetId/homingDelay: default/0f (every non-ContinuousFire caller) — forwarded as-is into every
    // spawned projectile this cast fans out (2026-10-06, ArcherFocusFireMultishotHomingAugment) — ALL of
    // them home, not just one, same "every projectile" framing the augment was specified with.
    private void Fire(Vector3 targetPosition, PredictedObjectID homingTargetId = default, float homingDelay = 0f)
    {
        if (projectilePrefab == null) return;

        float rawRicochet = _stats.Ricochet;
        int ricochet = (int)rawRicochet;
        if (_stats.NextFloat() < rawRicochet - ricochet) ricochet++;

        float rawMultishot = _stats.Multishot;
        int bonusShots = (int)rawMultishot;
        if (_stats.NextFloat() < rawMultishot - bonusShots) bonusShots++;

        Transform muzzle = hitboxTarget != null ? hitboxTarget : orbitPivot;
        Vector3 origin = muzzle.position;
        Vector3 baseDir = (targetPosition - origin).normalized;
        MultishotMath.ComputeAngles(bonusShots, _stats.MultishotSpreadAngle, _multishotAngles);

        foreach (float angle in _multishotAngles)
        {
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * baseDir;
            var id = hierarchy.Create(projectilePrefab, origin, Quaternion.LookRotation(dir), owner);
            if (!id.HasValue) continue;

            var go = hierarchy.GetGameObject(id.Value);
            if (go != null && go.TryGetComponent(out WeaponRangedProjectilePD projectile))
            {
                float weaponRadius = IsOuterTrack ? _stats.WeaponRadius + EffectiveWeaponRange : _stats.WeaponRadius;
                projectile.Launch(_stats, dir, _stats.ProjectileSpeed, ricochet, EffectiveWeaponRange, EffectiveRangeMultiplier, weaponRadius, _stats.Size * 0.5f, homingTargetId, homingDelay);
            }
        }
    }
}
