using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class WeaponMeleePD : WeaponBasePD<WeaponMeleePD.State>
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

    [SerializeField] private Vector3 meleeHalfExtents = new(0.1f, 0.1f, 0.5f);

    private bool _initialized;

    private readonly RaycastHit[]                _sweepResults  = new RaycastHit[32];
    private readonly Collider[]                  _overlapResults = new Collider[32];
    private readonly HashSet<IEnemyPD>                               _touchedThisTick  = new();

    // Ally-strike detection (OnStrikeHealAllyAugment, real friendly fire) — only ever populated/queried
    // when _stats.allyStrikeHitEnabled or _stats.allyStrikeDamageEnabled is true, so this costs nothing
    // for weapons without either. _sweepResults/_overlapResults are reused for the ally queries too (safe
    // — each call's results are fully consumed into a HashSet before the next Physics call overwrites the
    // shared buffer).
    private readonly HashSet<CharacterStatsPD>           _touchedAlliesThisTick = new();

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

    // Destroyed(), not OnDestroy() — same reasoning as CharacterStatsPD.Destroyed()/EnemyBasePD.Destroyed():
    // this is the hook PurrNet guarantees fires on every logical removal (including a pooled "delete" that
    // never actually calls Unity's own OnDestroy), whereas OnDestroy() left this weapon un-unregistered on
    // pooled removal — WeaponManagerPD kept a stale entry in _weapons/_outerWeapons pointing at a
    // deactivated-but-still-existing weapon GameObject, which TryGetFarthestWeaponReach then happily kept
    // reading (its now-frozen position making the range indicator balloon as the player walked away from it).
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

    // Runs after the framework's DoPhysicsPass() for this tick (Simulate() on every identity happens
    // first, then physics integrates, then LateSimulate() on every identity) — so _stats.transform.position
    // here already reflects the player's post-physics, current-tick position. Reading it in Simulate()
    // instead (which runs BEFORE the physics pass) is always one tick stale: invisible at normal speed,
    // but glaring during a dash or high move speed, where a single tick covers real distance.
    protected override void LateSimulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Tick cooldowns here (deterministic, tick-based delta) rather than Time.deltaTime in UpdateView()
        // — see HitCooldownEntry's own doc comment for why the old approach was unsafe under resimulation.
        HitCooldownMath.Tick(state.hitCooldowns);
        HitCooldownMath.Tick(state.allyHitCooldowns);

        // Compute orbital position and apply to transform.
        // PredictedTransform records these tick-positions and interpolates the Visuals child for rendering.
        // Outer-track weapons (OuterTrackAugment) spin the opposite direction from the shared angle: the
        // main track's WeaponOrbitAngle monotonically decreases each tick, so negating it produces a value
        // that monotonically increases at the same rate — a pure, deterministic mirror requiring no extra
        // predicted state of its own. angleSign threads that same flip through the "previous tick's angle"
        // reconstruction below (startAngle), which otherwise assumes the angle is decreasing.
        float   angleSign   = IsOuterTrack ? -1f : 1f;
        float   currentAngle = angleSign * _stats.WeaponOrbitAngle;
        Vector3 origin = _stats.transform.position;
        Vector3 orbitPos = OrbitalPosition(currentAngle, origin);
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

        // Extends the box's far edge to match hitboxTip's own visual reach (GetOuterReach uses the exact
        // same GetHitboxTipExtraReach() value, so the real hitbox and the cosmetic range indicator can
        // never drift apart). Applied as a forward-only center shift, not added straight into half.z, so
        // the NEAR edge (toward the handle) stays fixed — half the extra reach shifts center forward, half
        // grows half.z, netting a far edge that's exactly tipExtra further out and a near edge that's
        // unchanged. 0 when no hitboxTip is assigned, so this is a no-op for every other weapon.
        float tipExtra   = GetHitboxTipExtraReach();
        float centerShift = tipExtra * 0.5f;
        half.z += centerShift;
        // hb's rotation at this exact point (before any SetPositionAndRotation calls below run this tick)
        // is still last tick's — correct for shifting prevCenter, which is itself a previous-tick position.
        prevCenter += (state.prevSet ? hb.rotation : Quaternion.LookRotation(outward)) * Vector3.forward * centerShift;

        _touchedThisTick.Clear();
        _touchedAlliesThisTick.Clear();
        bool detectAllies = _stats.allyStrikeHitEnabled || _stats.allyStrikeDamageEnabled;
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
            // angle before this tick's movement — main track's angle decreases (so previous = current +
            // delta); outer track's mirrored angle increases instead (so previous = current - delta). Same
            // angleSign flip as currentAngle above.
            float startAngle = currentAngle + angleSign * angleDelta;
            int   subArcs    = ComputeSubArcCount(angleDelta);

            Vector3    segStart  = prevCenter;
            Vector3    segCenter = prevCenter;
            Quaternion segRot    = hb.rotation;

            for (int step = 1; step <= subArcs; step++)
            {
                bool    isLastStep = step == subArcs;
                Vector3 stepPos = isLastStep
                    ? orbitPos
                    : OrbitalPosition(Mathf.Lerp(startAngle, currentAngle, step / (float)subArcs), origin);
                Vector3 stepOutward = stepPos - origin;
                if (stepOutward == Vector3.zero) continue;

                transform.SetPositionAndRotation(stepPos, Quaternion.LookRotation(stepOutward));
                hb.GetPositionAndRotation(out segCenter, out segRot);
                segCenter += segRot * Vector3.forward * centerShift;

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

                    if (detectAllies)
                    {
                        int an = Physics.BoxCastNonAlloc(segStart, half, sweep / dist,
                            _sweepResults, segRot, dist, CombatLayerMasks.PlayerHurtboxMask);
                        for (int i = 0; i < an; i++)
                        {
                            var ally = _sweepResults[i].collider.GetComponent<CharacterStatsPD>();
                            if (ally != null) _touchedAlliesThisTick.Add(ally);
                        }
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
            center += hitboxRot * Vector3.forward * centerShift;
        }

        // Overlap at current position
        int oc = Physics.OverlapBoxNonAlloc(center, half, _overlapResults, hitboxRot, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < oc; i++)
        {
            var enemy = _overlapResults[i].GetComponent<IEnemyPD>();
            if (enemy != null) _touchedThisTick.Add(enemy);
        }

        if (detectAllies)
        {
            int ao = Physics.OverlapBoxNonAlloc(center, half, _overlapResults, hitboxRot, CombatLayerMasks.PlayerHurtboxMask);
            for (int i = 0; i < ao; i++)
            {
                var ally = _overlapResults[i].GetComponent<CharacterStatsPD>();
                if (ally != null) _touchedAlliesThisTick.Add(ally);
            }
        }

        // Pure cooldown gate — an enemy standing continuously inside the hitbox gets rehit as soon as its
        // cooldown clears, no need to actually separate first. HitCooldown is a fraction of a full weapon
        // rotation, which is enough on its own to handle continuous-overlap cases sanely.
        foreach (var enemy in _touchedThisTick)
        {
            if (HitCooldownMath.TryStart(state.hitCooldowns, enemy.ObjectId, HitCooldown, delta))
                Hit(enemy);
        }

        if (detectAllies)
        {
            foreach (var ally in _touchedAlliesThisTick)
            {
                if (HitCooldownMath.TryStart(state.allyHitCooldowns, ally.owner ?? default, HitCooldown, delta))
                    HitAlly(ally);
            }
        }

        state.prevSet = true;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!_initialized) TryInit();
    }

    // ── Hit ───────────────────────────────────────────────────────────────────

    private void Hit(IEnemyPD enemy)
    {
        if (enemy._isDead) return;
        float damage = RollDamage(_stats.Strength, out bool isHighroll);
        if (isHighroll) damage *= 1f + _stats.HighrollBonus;
        bool  isCrit = GameRandom.instance.NextFloat() < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage;
        if (isHighroll) _stats.NotifyHighroll(damage);
        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = _stats.Strength, onHitPower = _stats.OnHitPower, isCrit = isCrit };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
        Vector3 knockDir = (enemy.transform.position - _stats.transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback * hitData.onHitPower);
        NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = damage, isCrit = isCrit, isHighroll = isHighroll, attacker = _stats, isPriority = true });
    }

    // allyStrikeHitEnabled and allyStrikeDamageEnabled are independent — a weapon can heal-touch without
    // damaging, damage without the touch notification, or (once something actually turns damage on) both
    // off the same detected touch. Real friendly fire rolls luck variance same as every other damage
    // source, but never crits/knocks back — CharacterStatsPD.TakeDamage doesn't support either for players.
    private void HitAlly(CharacterStatsPD ally)
    {
        // Self-exclusion lives here, not just in NotifyAllyStrike — the direct TakeDamage call below
        // bypasses that guard entirely, so without this a weapon whose own overlap query happens to catch
        // its wielder's own collider (plausible for anything spawning/orbiting close to the caster) could
        // deal real friendly-fire damage to itself.
        if (ally == _stats || ally.IsDead) return;

        if (_stats.allyStrikeDamageEnabled)
        {
            float damage = RollDamage(_stats.Strength, out bool isHighroll);
            if (isHighroll) damage *= 1f + _stats.HighrollBonus;
            ally.TakeDamage(damage);
        }

        if (_stats.allyStrikeHitEnabled)
            _stats.NotifyAllyStrike(new AllyStrikeData { ally = ally, onHitPower = _stats.OnHitPower });
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
        // Same forward-only extension as LateSimulate's real hitbox — see its own comment for why this is
        // a center shift plus half.z growth rather than a plain half.z increase.
        float centerShift = GetHitboxTipExtraReach() * 0.5f;
        half.z += centerShift;
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.4f);
        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(hb.position, hb.rotation, Vector3.one);
        Gizmos.DrawWireCube(new Vector3(0f, 0f, centerShift), half * 2f);
        Gizmos.matrix = prev;
    }
}
