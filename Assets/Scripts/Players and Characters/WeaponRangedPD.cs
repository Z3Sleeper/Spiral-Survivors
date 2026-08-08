using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Orbits its owner exactly like WeaponMeleePD (same shared OrbitalPosition() formula on WeaponBasePD<TState>)
// but never touches anything directly — instead, every tick it raycasts forward (its current orbit-facing
// direction) out to WeaponRange looking for an enemy hurtbox. If it finds one and isn't on cooldown, it
// fires a WeaponRangedProjectilePD at that enemy's hurtbox bounds CENTER, not the raw raycast hit point —
// since the ray is thin and the weapon is orbiting, the hit point is usually on the near/side edge of the
// collider, which would cause frequent near-misses. If it finds one but IS on cooldown, it doesn't fire and
// doesn't reset the cooldown, so it retries every subsequent tick until the cooldown clears. Ported from
// the old WeaponBow.GetTargetPosition()/Fire().
//
// Enemies only, deliberately — this weapon never aims at an ally, so "hit ally, do something" augments
// (OnStrikeHealAllyAugment etc.) only ever trigger off WeaponRangedProjectilePD's own flight-path sweep
// grazing an ally standing between the shooter and its real (enemy) target, a pure "happy accident" of
// positioning. To land one on purpose you either line an ally up in that path yourself or use something
// like ArcherFocusFireAbilityPD, which fires in a fixed direction with no target-seeking at all.
public class WeaponRangedPD : WeaponBasePD<WeaponRangedPD.State>
{
    public struct State : IPredictedData<State>
    {
        // Ticks, not seconds — see EnemyBaseState.ContactCooldown.remaining's own doc comment for why (always
        // mutates every tick, so an int's cheap zigzag-varint diff beats a float's flat 33-bit cost).
        // Allowed to go arbitrarily negative (see the reset comment below), same as it always could as a
        // float — an int just represents that identical "debt" in tick units instead of seconds.
        public int fireTimer;
        public DisposableList<HitCooldownEntry> hitCooldowns;
        public void Dispose()
        {
            hitCooldowns.Dispose();
        }
    }

    public override bool IsMelee => false;

    [Header("Ranged")]
    [SerializeField] private GameObject projectilePrefab;

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
    };

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!_initialized) TryInit();
        ApplyVisualAdjustments();
    }

    // ── Simulation ────────────────────────────────────────────────────────────

    // Empty — orbit angle is now ticked once, shared, on CharacterStatsPD (see
    // CharacterStatsPD.State.weaponOrbitAngle's own doc comment); this weapon just reads _stats.WeaponOrbitAngle
    // in LateSimulate() instead of maintaining its own copy.
    protected override void Simulate(ref State state, float delta) { }

    // Same post-physics-pass timing reasoning as WeaponMeleePD.LateSimulate() — _stats.transform.position
    // (and therefore this weapon's own orbital position) needs to reflect the player's current-tick,
    // post-physics position, not a tick-stale one.
    protected override void LateSimulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Tick cooldowns here (deterministic, tick-based delta) rather than Time.deltaTime in UpdateView()
        // — see HitCooldownEntry's own doc comment for why the old approach was unsafe under resimulation:
        // a replayed tick could see a target's cooldown already expired and pick it again, choosing a
        // different target than the original tick actually did — a guaranteed misprediction every time.
        HitCooldownMath.Tick(state.hitCooldowns);

        // Outer-track weapons (OuterTrackAugment) spin the opposite direction — see
        // WeaponMeleePD.LateSimulate's own angleSign comment for the full reasoning. No sub-arc sweep here
        // (ranged just fires at whatever's in front of it), so no startAngle reconstruction needed.
        //
        // ArcherFocusFireAbilityPD freezes the whole ring by substituting FocusFireAimAngle (cursor-derived,
        // shared by every weapon this owner has) for the normal ever-incrementing WeaponOrbitAngle — see
        // CharacterStatsPD.FocusFireAimAngle's own doc comment for why only the SHARED base changes here
        // and each weapon's own SlotIndex offset (applied inside OrbitalPosition below) still does the rest.
        float   baseAngle    = _stats.ContinuousFire ? _stats.FocusFireAimAngle : _stats.WeaponOrbitAngle;
        float   currentAngle = (IsOuterTrack ? -1f : 1f) * baseAngle;
        Vector3 origin = _stats.EffectiveOrbitOrigin;
        Vector3 orbitPos = OrbitalPosition(currentAngle, origin);
        Vector3 outward  = orbitPos - origin;
        if (outward == Vector3.zero) return;

        transform.SetPositionAndRotation(ApplyHeightLift(orbitPos), Quaternion.LookRotation(outward));

        // Skipped entirely while _stats.IsWeaponsDisabled (e.g. the spin ability's leap) — the weapon keeps
        // orbiting above (purely visual), it just never actually fires; fireTimer is deliberately left
        // untouched too, so it resumes exactly where it left off once re-enabled rather than losing progress.
        if (_stats.IsWeaponsDisabled) return;

        state.fireTimer--;
        if (state.fireTimer > 0) return;

        if (_stats.ContinuousFire)
        {
            // Still skips FindTarget()/LOS entirely — fires straight at wherever this weapon is currently
            // aimed (see baseAngle above), not along a raycast — but now requires ContinuousFireHasTarget
            // (set by ArcherFocusFireAbilityPD.UpdateAutoAimAngle alongside the aim angle itself) so it
            // doesn't fire blindly into empty space when nothing's actually in range. Deliberately NOT reset
            // on the no-target branch — same "hard reset only on an actual shot, let debt accumulate
            // otherwise" reasoning FindTarget's own fireTimer reset below already uses, so a target coming
            // back into range fires immediately instead of waiting out a fresh full interval. No per-target
            // hit cooldown bookkeeping either — there's no selected target to gate.
            if (!_stats.ContinuousFireHasTarget) return;
            Fire(transform.position + transform.forward * _stats.WeaponRange);
            state.fireTimer = Mathf.RoundToInt(1f / Mathf.Max(0.01f, _stats.AttackSpeed * _stats.FireRate) / delta);
            return;
        }

        if (!FindTarget(transform.position, transform.forward, state.hitCooldowns, out IEnemyPD target, out Vector3 hurtboxCenter)) return;

        HitCooldownMath.TryStart(state.hitCooldowns, target.ObjectId, HitCooldown, delta);
        Fire(hurtboxCenter);
        // Hard reset, not += — fireTimer can drift arbitrarily negative while no target is in the forward
        // ray (the common case for a thin raycast), and accumulating the cooldown on top of that debt
        // instead of clearing it would cause a rapid-fire burst to pay it off once a target reappears.
        state.fireTimer = Mathf.RoundToInt(1f / Mathf.Max(0.01f, _stats.AttackSpeed * _stats.FireRate) / delta);
    }

    // ── Targeting ─────────────────────────────────────────────────────────────

    // Pulls the ray origin back 1 unit and pads the range by 1 to match — same as old WeaponBow — so an
    // enemy pressed right up against the weapon's own origin still gets hit instead of the ray starting
    // inside/past it. Enemies currently on hitCooldowns are skipped so the ray "sees past" them to
    // whatever's next in line, same reason the old WeaponBow tracked _initialHitCooldowns.
    //
    // Enemy layer only — this weapon never aims at an ally (see this file's own top-of-file comment for
    // why); an ally standing in the ray's path is simply invisible to target SELECTION, the same as any
    // other collider outside the mask.
    private bool FindTarget(Vector3 from, Vector3 forward, DisposableList<HitCooldownEntry> hitCooldowns,
        out IEnemyPD enemy, out Vector3 hurtboxCenter)
    {
        Vector3 origin = from - forward * 1f;
        float   range  = _stats.WeaponRange + 1f;

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

    // Auto-aim targeting for ArcherFocusFireAbilityPD's own aim direction (see CharacterStatsPD.
    // FocusFireAimAngle's own doc comment) — finds the closest living enemy within `range` of `from` via a
    // fixed-radius physics query, same deterministic/replay-safe reasoning WeaponRangedProjectilePD.
    // FindRicochetTarget's own doc comment gives for why this isn't a FindObjectsByType scan. Static, not
    // instance-scoped: Focus Fire's aim angle is ONE shared value for the whole character, not per-weapon,
    // so the ability calls this once rather than needing to pick a specific weapon instance out of however
    // many ranged weapons this character currently owns (WeaponCount stacking). 128-slot buffer (not 16/32)
    // — OverlapSphereNonAlloc does NOT return colliders in distance order and truncates silently, so a
    // "closest one in a wide radius" query needs a generous buffer or a dense swarm could hide the true
    // closest enemy behind a wall of farther ones that happened to fill the buffer first (see
    // PhantomBladeSummonHelper/AreaEffectCastHelper's own identical comment on this exact gotcha).
    private static readonly Collider[] _autoAimResults = new Collider[128];

    public static bool TryFindAutoAimDirection(Vector3 from, float range, out Vector3 direction)
    {
        int   n        = Physics.OverlapSphereNonAlloc(from, range, _autoAimResults, CombatLayerMasks.EnemyHurtboxMask);
        float bestDist = float.MaxValue;
        direction = default;
        bool found = false;

        for (int i = 0; i < n; i++)
        {
            var enemy = _autoAimResults[i].GetComponent<IEnemyPD>();
            if (enemy == null || enemy._isDead) continue;

            float dist = Vector3.Distance(from, enemy.transform.position);
            if (dist >= bestDist) continue;

            bestDist  = dist;
            direction = (enemy.transform.position - from).normalized;
            found     = true;
        }
        return found;
    }

    // ── Fire ──────────────────────────────────────────────────────────────────

    // Scratch buffer for MultishotMath.ComputeAngles — static, not instance, same "shared, immediately
    // drained within one synchronous call" convention as every other scratch buffer in this codebase.
    private static readonly List<float> _multishotAngles = new();

    // Ricochet/multishot fractional-roll mechanic ported from the old WeaponBow.Fire() — the integer part
    // of each stat is guaranteed, the fractional part is a probability roll for one more (e.g. Ricochet =
    // 1.5 gives 1 bounce always, 2 bounces half the time). GameRandom, never UnityEngine.Random — this
    // runs from LateSimulate(), a deterministic/replay-safe context. Angle fan-out uses the shared
    // MultishotMath formula (see its own doc comment) — same behavior as ShooterGloomPD's own multishot.
    private void Fire(Vector3 targetPosition)
    {
        if (projectilePrefab == null) return;

        float rawRicochet = _stats.Ricochet;
        int ricochet = (int)rawRicochet;
        if (GameRandom.instance.NextFloat() < rawRicochet - ricochet) ricochet++;

        float rawMultishot = _stats.Multishot;
        int bonusShots = (int)rawMultishot;
        if (GameRandom.instance.NextFloat() < rawMultishot - bonusShots) bonusShots++;

        Vector3 origin = transform.position;
        Vector3 baseDir = (targetPosition - origin).normalized;
        MultishotMath.ComputeAngles(bonusShots, _stats.MultishotSpreadAngle, _multishotAngles);

        foreach (float angle in _multishotAngles)
        {
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * baseDir;
            var id = hierarchy.Create(projectilePrefab, origin, Quaternion.LookRotation(dir), owner);
            if (!id.HasValue) continue;

            var go = hierarchy.GetGameObject(id.Value);
            if (go != null && go.TryGetComponent(out WeaponRangedProjectilePD projectile))
                projectile.Launch(_stats, dir, _stats.ProjectileSpeed, ricochet);
        }
    }
}
