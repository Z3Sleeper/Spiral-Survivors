using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// The projectile ExeAxeThrowAbilityPD throws — a short-lived, self-contained flying box hitbox, same
// "spawned by the caster, not a child of anything, keeps flying regardless of the caster's own state after"
// shape as WeaponRangedProjectilePD, but with its own distinct rules rather than reusing that class:
// - Damage is a flat 200%(-ish, see strengthPercent) Unpenalized Strength every hit, no per-hit dropoff.
// - Knockback is UNPENALIZED — CharacterStatsPD.Knockback alone, deliberately not multiplied by OnHitPower
//   the way WeaponRangedProjectilePD's own knockback is (per the user's own call).
// - ALWAYS pierces (keeps flying straight until it runs out of range, never stops on a hit) — originally
//   only pierced on a crit, but that felt bad in testing: an abrupt stop reads as a bug, and crit chance
//   alone wasn't a rewarding-enough payoff until it was pushed to literal 100%. Ricochet-retargeting
//   (WeaponRangedProjectilePD's own thing) still doesn't apply here — the OUTBOUND leg always keeps its
//   original throw heading, no retargeting at all (the return leg is a deliberate exception — see next point).
// - On reaching the end of its outward flight (distanceTravelled >= _maxRange), instead of expiring it flips
//   into a return leg that continuously re-aims at the caster's LIVE position every tick (see LateSimulate's
//   own top-of-tick re-aim) — real homing, not a one-time snap, specifically so it can't miss a caster who
//   keeps moving after the turnaround. _hitTargets is cleared at the moment it flips into the return leg, so
//   anything hit on the way out can be hit again on the way back. Expires once it comes within _returnRadius
//   of the caster's LIVE position — a plain radius check around wherever the caster currently is, not
//   "reached this exact point." _returnRadius reuses the exact same spawnOffset distance
//   ExeAxeThrowAbilityPD.Activate() originally spawned this at (see Launch()'s own doc comment) — the axe
//   settles back at the same orbit-ish distance it left from, not by touching the caster's exact center. No
//   distance/range cap on the return leg — with continuous homing it's guaranteed to eventually close the
//   gap rather than needing a fallback cutoff the outbound-leg-turned-fixed-direction version once needed.
// - Crits instead deal critDamageMultiplier-times (2x by default) the normal crit damage, on top of the
//   already-always-piercing behavior above — the payoff moved from "keep going" to "hit harder", tuned via
//   Strength/strengthPercent being set lower in the Inspector to compensate for the raised crit ceiling.
public class ExeAxeThrowProjectilePD : PredictedIdentity<ExeAxeThrowProjectilePD.State>
{
    public struct State : IPredictedData<State>
    {
        public Vector3 direction;
        public float   distanceTravelled;
        // False for the outbound leg, true once the one-time return snap has happened — see class doc
        // comment.
        public bool    returning;
        // Accumulated tumble angle in degrees, deterministic (spinSpeed * delta per tick, see LateSimulate) —
        // drives the root transform's own rotation every tick (see LateSimulate), same "lives in State so
        // resimulation replays it identically" reasoning as distanceTravelled.
        public float   spinAngle;
        // Deferred hierarchy.Delete() to UpdateView() off verified state, same pattern as
        // WeaponRangedProjectilePD/EnemyProjectilePD's own death handling.
        public bool    expired;
        public void Dispose() { }
    }

    // Sphere, not box — since this projectile now spins (see spinSpeed), a box's own hit-shape rotates right
    // along with it, so an oriented box no longer means anything consistent; a sphere reads as "the swept
    // radius the spin covers" and has no orientation to drift in the first place. Base, prefab-authored
    // radius at scale 1 (was boxHalfExtents' own x/z, tuned in Inspector to 0.5/0.5 before this conversion).
    // The sphere-cast query (see LateSimulate()) multiplies this by transform.lossyScale.x — the whole root
    // scales uniformly by WeaponRange * rangeScaleMultiplier at Launch() (this was ALWAYS meant to be a
    // Range-driven grow, never a Size-driven one — an earlier version mistakenly scaled the root by the
    // caster's own bonusSizePercent instead, which meant the hitbox never actually grew with Range at all,
    // only a separate cosmetic-only child did — see rangeScaleMultiplier's own doc comment).
    [SerializeField] private float sphereRadius = 0.5f;
    [Header("Crit")]
    // Multiplies CritDamage itself (not additive) — see class doc comment for why this replaced the old
    // "pierce only on crit" payoff once piercing became unconditional.
    [SerializeField] private float critDamageMultiplier = 2f;
    [SerializeField] private Renderer[] _renderers;
    // Visual-only target for the mid-height lift below — a dedicated child (e.g. this prefab's "Model", the
    // parent of every visible mesh piece), NOT this projectile's own root transform (that's the real,
    // hit-detection-relevant flight position LateSimulate() moves every tick). Optional: leave unassigned
    // and this projectile simply never lifts, no error. Same field/reasoning as WeaponRangedProjectilePD's
    // own visualHeightTarget.
    [SerializeField] private Transform visualHeightTarget;
    // Drives the WHOLE projectile's uniform scale (root transform.localScale, at Launch()) — WeaponRange *
    // this multiplier, so both the hit-detection sphere (sphereRadius * transform.lossyScale.x) and the
    // visible model grow together, in lockstep, with no separate cosmetic-only layer to drift out of sync.
    // Tune this if the default 1:1 growth rate feels too aggressive/subtle in practice.
    [SerializeField] private float rangeScaleMultiplier = 1f;
    // Optional — pieces whose LENGTH happens to run along their own local Y axis (e.g. a handle/pole modeled
    // standing "upright" before being assembled into the axe), so the root's own uniform Range-driven growth
    // (see rangeScaleMultiplier's own doc comment) would stretch them taller/shorter along their length
    // instead of just scaling their footprint like everything else. Each listed transform's own local Y is
    // counter-scaled at Launch() — divided by the same ratio the root just grew by — so it renders at its
    // originally-authored length regardless of WeaponRange, same "unscaledSizeChildren" counter-scale shape
    // CharacterStatsPD.AddSizePercent already uses for Size (see project_unscaled_size_children_mechanic
    // memory), just isolated to Y instead of all three axes. X/Z are left untouched — those still inherit the
    // root's growth normally via ordinary Unity parent-child scale inheritance. A piece that should stretch
    // along with everything else (e.g. the axehead) just stays off this list.
    [SerializeField] private Transform[] yScaleFollowers;
    // Tumbles the ROOT transform itself around its own up axis while traveling (see LateSimulate) — purely
    // visual now that hit detection is a SphereCastNonAlloc (see sphereRadius), which has no orientation to
    // drift in the first place.
    [SerializeField] private float spinSpeed = 720f; // degrees/sec — 720 = 2 full spins per second.

    private CharacterStatsPD _stats;
    private Vector3 _baseScale;
    private float[] _baseYScales;
    // Prefab-authored x/z for visualHeightTarget, captured once in Awake() — see its own comment. Y is
    // overwritten fresh every Launch() (counter-scaled against that throw's rangeScale), so this is only
    // ever read for its x/z; whatever Y this snapshot happens to hold is never used.
    private Vector3 _baseVisualHeightTargetLocal;
    private float _speed;
    private float _maxRange;
    private float _strengthPercent;
    // Arrival radius for the return trip — see class doc comment/Launch()'s own doc comment for why this is
    // the same value as the original spawnOffset, not a fixed constant.
    private float _returnRadius;
    // Plain view-layer bookkeeping, not predicted state — same instant-predicted-hide pattern as
    // WeaponRangedProjectilePD/EnemyProjectilePD.
    private bool _hiddenOnExpire;

    // Persists for the WHOLE flight (cleared only on a fresh Launch()), not just one tick's sweep — the
    // elongated box can keep overlapping an already-pierced enemy for several subsequent ticks as it
    // continues past them, and this is what stops that from re-hitting the same enemy repeatedly.
    private readonly HashSet<IEnemyPD> _hitTargets = new();
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];

    // Same reasoning as WeaponRangedProjectilePD's own Awake() — plain Unity Awake() (not LateAwake(), which
    // re-fires on every pooled reuse) so this captures the prefab's true authored scale exactly once.
    private void Awake()
    {
        _baseScale = transform.localScale;

        if (yScaleFollowers != null)
        {
            _baseYScales = new float[yScaleFollowers.Length];
            for (int i = 0; i < yScaleFollowers.Length; i++)
                if (yScaleFollowers[i] != null)
                    _baseYScales[i] = yScaleFollowers[i].localScale.y;
        }

        // Base authored local position cached here (not the final offset itself) — the actual height-lift
        // write now happens in Launch() instead, AFTER rangeScale is known, since it needs to counter-scale
        // against it (see Launch()'s own comment for why). Awake() only runs once per real GameObject
        // instance even under this projectile's own SetActive-based pooling, so this is still the right
        // place to capture the true prefab-authored x/z once, same reasoning as _baseScale/_baseYScales.
        if (visualHeightTarget != null) _baseVisualHeightTargetLocal = visualHeightTarget.localPosition;
    }

    // Called by ExeAxeThrowAbilityPD.Activate() immediately after hierarchy.Create() returns — synchronous
    // and deterministic, same "mutate currentState right after Create()" pattern as every other spawned
    // one-shot effect in this codebase. returnRadius is ExeAxeThrowAbilityPD's own spawnOffset value (the
    // distance this projectile was thrown from in front of the caster) — reused here as the return trip's
    // arrival radius, so the axe settles back at roughly the same distance from the caster it left from
    // rather than needing to touch their exact center (see class doc comment).
    public void Launch(CharacterStatsPD stats, Vector3 direction, float speed, float maxRange, float strengthPercent, float returnRadius)
    {
        _stats = stats;
        _speed = speed;
        _maxRange = maxRange;
        _strengthPercent = strengthPercent;
        _returnRadius = returnRadius;

        currentState.direction = direction;
        currentState.distanceTravelled = 0f;
        currentState.returning = false;
        currentState.spinAngle = 0f;
        currentState.expired = false;

        transform.rotation = Quaternion.LookRotation(direction);
        // Uniform — scales with WeaponRange (not the caster's Size/bonusSizePercent — this was always meant
        // to be a Range-driven grow, see sphereRadius's own doc comment) on every axis alike, so the hitbox
        // (sphereRadius * transform.lossyScale.x, see LateSimulate()) and the visible model grow together in
        // lockstep automatically via ordinary Unity parent-child scale inheritance — no separate cosmetic
        // scaling layer needed.
        float rangeScale = stats.WeaponRange * rangeScaleMultiplier;
        transform.localScale = _baseScale * rangeScale;

        // Counter-scale — see yScaleFollowers' own doc comment. Divides back OUT the same rangeScale the
        // root just grew by, on Y only, so these specific pieces render at their originally-authored length
        // regardless of WeaponRange while everything else (their own X/Z, and every other piece) still
        // inherits the root's growth normally.
        if (_baseYScales != null)
        {
            for (int i = 0; i < yScaleFollowers.Length; i++)
            {
                if (yScaleFollowers[i] == null) continue;
                var s = yScaleFollowers[i].localScale;
                yScaleFollowers[i].localScale = new Vector3(s.x, _baseYScales[i] / rangeScale, s.z);
            }
        }

        // Same "correct for the player's default 1x1x1-unit sphere" constant WeaponBasePD.
        // ApplyVisualAdjustments()/WeaponRangedProjectilePD's own Awake() lift orbiting/flying weapon models
        // by — this projectile's own root spawns at the caster's un-lifted, ground-level position
        // (ExeAxeThrowAbilityPD.Activate() reads _stats.transform.position, not a visually-lifted model), so
        // the visual mesh needs the same compensating offset to render at the same mid-body height every
        // other weapon model appears to fire from. Deliberately counter-scaled here (divided by rangeScale,
        // set AFTER transform.localScale above, unlike WeaponRangedProjectilePD's own version of this same
        // constant, which is fine left unscaled since ITS root only ever grows with the much more modest
        // Size/bonusSizePercent) — AxeThrow's root instead grows with WeaponRange, which a dedicated Range
        // build can push far higher than Size ever would, and letting the height lift compound with that
        // (the naive "set once in Awake(), let ordinary parent-child inheritance apply the multiply for
        // free" approach every other weapon in this codebase uses) visibly launched the axe absurdly high
        // off the ground at high Range — as if the caster were a giant. Same yScaleFollowers-style
        // counter-scale shape, just for this one Y position instead of a handful of child scales.
        if (visualHeightTarget != null)
        {
            var p = _baseVisualHeightTargetLocal;
            visualHeightTarget.localPosition = new Vector3(p.x, WeaponVisualConstants.BaseMidHeightLift / rangeScale, p.z);
        }

        _hitTargets.Clear();

        _hiddenOnExpire = false;
        SetRenderersEnabled(true);

        foreach (var a in GetComponentsInChildren<PlayerColorApplicator>(true))
            a.Apply(stats.PlayerColor);
    }

    protected override State GetInitialState() => default;

    // Intentionally empty — movement/hit detection need the post-physics-pass position of anything they
    // interact with, so everything happens in LateSimulate(), same reasoning as WeaponRangedProjectilePD.
    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (_stats == null) { state.expired = true; return; } // shouldn't happen — Launch() always sets it
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        Vector3 prevPos = transform.position;
        Vector3 startDirection = state.direction;

        // Continuous homing for the return leg only — re-aims at the caster's LIVE position every tick
        // before this tick's own movement below, so it can't miss a caster who keeps moving after the
        // turnaround (see class doc comment). The outbound leg never hits this — direction there stays
        // exactly whatever it was thrown at.
        if (state.returning)
        {
            Vector3 toPlayer = _stats.transform.position - transform.position;
            if (toPlayer != Vector3.zero) state.direction = toPlayer.normalized;
        }

        float totalDist = _speed * delta;

        // A single straight sweep for the whole tick assumes the heading barely changed since last tick —
        // always true for the outbound leg (direction there never changes at all, so angleDelta is always 0
        // below) but not for the return leg's continuous re-aim: the CLOSER the axe gets to a moving caster,
        // the more the bearing to them can swing in one tick (the same lateral caster movement produces a
        // much bigger angular swing at short range than at long range) — exactly the situation near the end
        // of the return leg, right where enemies are most likely to be clustered around the caster. A big
        // enough swing means this tick's straight segment simply never travels through the space the OLD
        // heading was aimed at, silently skipping anything sitting near that pivot — this was the actual
        // cause of "the return trip passes through enemies without dealing damage." Subdividing into
        // sub-steps that gradually slerp from the old heading to the new one closes that gap — same
        // mitigation SweepMath.ComputeSubArcCount already provides for WeaponMeleePD/WeaponAxePD's own orbit
        // sweep, reused here since the underlying problem (a straight chord skipping part of the true path)
        // is the same shape even though the path itself isn't a circular arc.
        float angleDelta = state.returning ? Vector3.Angle(startDirection, state.direction) : 0f;
        int   subSteps   = SweepMath.ComputeSubArcCount(angleDelta);

        Vector3  segStart = prevPos;
        IEnemyPD hitEnemy = null;
        float    radius   = sphereRadius * transform.lossyScale.x;

        for (int i = 1; i <= subSteps; i++)
        {
            Vector3 stepDir = subSteps == 1
                ? state.direction
                : Vector3.Slerp(startDirection, state.direction, i / (float)subSteps);
            Vector3 segEnd = segStart + stepDir * (totalDist / subSteps);

            // Skips the query once a hit's already been found this tick (still finishes advancing segStart
            // through the remaining sub-steps below, unconditionally — always pierces, so movement itself
            // never depends on whether/where a hit landed, see class doc comment) — no reason to keep
            // spending physics queries once this tick's one hit is already locked in.
            if (hitEnemy == null)
            {
                Vector3 sweep    = segEnd - segStart;
                float   segDist  = sweep.magnitude;
                if (segDist > 0.0001f)
                {
                    int n = Physics.SphereCastNonAlloc(segStart, radius, sweep / segDist, _sweepResults, segDist, CombatLayerMasks.EnemyHurtboxMask);
                    float bestHitDist = float.MaxValue;
                    for (int k = 0; k < n; k++)
                    {
                        var enemy = _sweepResults[k].collider.GetComponent<IEnemyPD>();
                        if (enemy == null || enemy._isDead || _hitTargets.Contains(enemy)) continue;
                        if (_sweepResults[k].distance < bestHitDist)
                        {
                            bestHitDist = _sweepResults[k].distance;
                            hitEnemy = enemy;
                        }
                    }
                }
            }

            segStart = segEnd;
        }

        transform.position = segStart;
        state.distanceTravelled += totalDist;
        state.spinAngle = Mathf.Repeat(state.spinAngle + spinSpeed * delta, 360f);
        // Base facing (still tracks the original throw direction, never retargeted) with the tumble layered
        // on top around the local up axis — applied to the actual root transform, so the sweep above sweeps
        // with the spun orientation too (see spinSpeed's own doc comment).
        transform.rotation = Quaternion.LookRotation(state.direction) * Quaternion.AngleAxis(state.spinAngle, Vector3.up);

        // Always pierces — see class doc comment — so a hit never expires the projectile here; only
        // the return-trip logic below does.
        if (hitEnemy != null)
            ProcessHit(hitEnemy);

        if (state.returning)
        {
            // Plain radius check around the caster's LIVE position — see _returnRadius/class doc comment for
            // why this is a radius (matching the original spawnOffset), not a "reached this exact point" or
            // distance-travelled check. No _maxRange fallback here on purpose (see class doc comment).
            float distToPlayer = (_stats.transform.position - transform.position).magnitude;
            if (distToPlayer <= _returnRadius)
                state.expired = true;
        }
        else if (state.distanceTravelled >= _maxRange)
        {
            // End of the outbound leg — flips into the return leg; direction itself gets re-aimed at the
            // top of this method starting next tick (see above), so nothing to set here beyond the flag
            // itself. _hitTargets clears so anything hit on the way out can be hit again on the way back.
            state.distanceTravelled = 0f;
            state.returning = true;
            _hitTargets.Clear();
        }
    }

    // Mirrors WeaponRangedProjectilePD.ProcessHit, minus ricochet/dropoff (neither apply here) and with
    // knockback deliberately left unpenalized (no OnHitPower multiply) — see class doc comment. No longer
    // returns whether the hit crit — always pierces now regardless (see class doc comment), so there's
    // nothing left for a caller to gate on.
    private void ProcessHit(IEnemyPD enemy)
    {
        _hitTargets.Add(enemy);

        // AbilityPower scales damage — read LIVE off _stats here (not snapshotted at throw time), same
        // reasoning as RogueSpinAbilityPD's own BaseDamage(): this is an ongoing in-flight effect that can
        // land several hits over its lifetime (piercing on crit), not a one-shot buff grant, so each hit
        // should reflect AbilityPower at the moment it actually lands.
        //
        // Anything that deals damage rolls luck variance — no exceptions.
        float baseDamage = _stats.UnpenalizedStrength * _strengthPercent * _stats.AbilityPower;
        float damage = WeaponDamageMath.RollDamage(baseDamage, _stats.LuckChance, _stats.LuckFloor, _stats.LuckCeiling, _stats.Highroll, out bool isHighroll);
        if (isHighroll) damage *= 1f + _stats.HighrollBonus;

        bool isCrit = GameRandom.instance.NextFloat() < _stats.CritChance;
        if (isCrit) damage *= _stats.CritDamage * critDamageMultiplier;
        if (isHighroll) _stats.NotifyStrikeHighroll(damage);

        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = damage, onHitPower = _stats.OnHitPower, isCrit = isCrit };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);

        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);

        // Every hit interrupts — Executioner's own way of interacting with enemies beyond raw damage
        // (Knight has interrupt+stun, Bladeweaver has range+dodge, Archer has a slow, Rogue has dodge+
        // invisibility; AxeThrow had nothing until now). Called BEFORE AddKnockbackVelocity below, per
        // IEnemyPD.Interrupt()'s own doc comment ("call this BEFORE a paired Stun()/AddKnockbackVelocity on
        // the same hit, never after"). No-op on an enemy that's already !CanBeInterrupted this tick (e.g.
        // Fire/Leaping/other "already committed" phases) — same as every other Interrupt() caller, nothing
        // extra needed here to detect that.
        enemy.Interrupt();

        // Unpenalized — CharacterStatsPD.Knockback alone, no OnHitPower multiply (unlike
        // WeaponRangedProjectilePD's own `_knockback * onHitPower`), per the user's own call.
        Vector3 knockDir = (enemy.transform.position - transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback);

        _stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = damage, isCrit = isCrit, isHighroll = isHighroll, attacker = _stats, isPriority = true, source = DamageSource.Weapon });
    }

    private void SetRenderersEnabled(bool value)
    {
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].enabled = value;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Instant, predicted hide — same reasoning as WeaponRangedProjectilePD/EnemyProjectilePD's own.
        if (viewState.expired && !_hiddenOnExpire)
        {
            _hiddenOnExpire = true;
            SetRenderersEnabled(false);
        }

        // State-critical — only act on server-confirmed expiry, same reasoning as every other spawned
        // one-shot effect's deletion.
        if (verified.HasValue && verified.Value.expired)
            hierarchy.Delete(this);
    }
}
