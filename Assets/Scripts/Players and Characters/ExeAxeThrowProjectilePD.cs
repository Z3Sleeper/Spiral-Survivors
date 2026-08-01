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
//   (WeaponRangedProjectilePD's own thing) still doesn't apply here at all — this always keeps its original
//   heading regardless.
// - Crits instead deal critDamageMultiplier-times (2x by default) the normal crit damage, on top of the
//   already-always-piercing behavior above — the payoff moved from "keep going" to "hit harder", tuned via
//   Strength/strengthPercent being set lower in the Inspector to compensate for the raised crit ceiling.
public class ExeAxeThrowProjectilePD : PredictedIdentity<ExeAxeThrowProjectilePD.State>
{
    public struct State : IPredictedData<State>
    {
        public Vector3 direction;
        public float   distanceTravelled;
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
    // radius the spin covers" and has no orientation to drift in the first place. Fixed, prefab-authored
    // radius at scale 1 (was boxHalfExtents' own x/z, tuned in Inspector to 0.5/0.5 before this conversion),
    // no WeaponRange-driven sizing (same reasoning boxHalfExtents' own doc comment used to cover — see
    // rangeScaleTarget instead for the purely cosmetic version of that). The sphere-cast query multiplies
    // this by transform.lossyScale.x (see LateSimulate()), which only ever carries the caster's own
    // bonusSizePercent (see Launch()) uniformly on every axis, so any single axis works as the scale factor.
    [SerializeField] private float sphereRadius = 0.5f;
    // Own crit-chance formula (see ProcessHit) rather than a straight _stats.CritChance read — piercing (the
    // whole point of a crit here, see class doc comment) is meant to be somewhat likely from the very start
    // of a run, not gated behind crit-chance upgrades first. baseCritChance is a flat floor regardless of
    // the player's own crit investment; critChanceFromPlayer scales how much of _stats.CritChance still adds
    // on top (0.5 = only half as effective here as it is on the player's actual weapon strikes).
    [Header("Crit")]
    [SerializeField] private float baseCritChance = 0.5f;
    [SerializeField] private float critChanceFromPlayer = 0.5f;
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
    // Optional — a dedicated child (e.g. this prefab's "Model") whose own X/Z scale is set to
    // WeaponRange * rangeScaleMultiplier at Launch(), same "the visible model grows with WeaponRange"
    // effect the equipped weapon gets for free from WeaponManagerPD.ScaleModel(), which a standalone thrown
    // projectile has no equivalent of otherwise. Y is deliberately left at whatever it's authored to
    // (cached once in Awake(), never touched here) — same "stretching Y makes it thicker, not bigger"
    // reasoning boxHalfExtents/GetOuterReach's own doc comments already cover elsewhere; only X/Z (spread
    // and forward reach) are meant to visually grow. Leave unassigned and this projectile's model simply
    // never grows with WeaponRange, no error — purely cosmetic, entirely decoupled from boxHalfExtents
    // (the actual hitbox), which stays fixed regardless (see its own doc comment for why that was
    // deliberately scrapped).
    [SerializeField] private Transform rangeScaleTarget;
    [SerializeField] private float rangeScaleMultiplier = 1f;
    // Optional — same convention as WeaponBasePD's own yScaleFollowers: pieces whose LENGTH happens to run
    // along their own local Y axis (e.g. a handle/pole modeled standing "upright" before being assembled
    // into the weapon), so growing rangeScaleTarget's X/Z alone doesn't stretch them — they need their own
    // Y scaled instead. Only these listed transforms' own Y moves; everything else under rangeScaleTarget
    // (e.g. the axehead) is untouched by this, so a piece that shouldn't stretch at all just stays off the
    // list. Each follower's X/Z stays exactly as authored — only Y changes.
    [SerializeField] private Transform[] yScaleFollowers;
    // Tumbles the ROOT transform itself around its own up axis while traveling (see LateSimulate) — purely
    // visual now that hit detection is a SphereCastNonAlloc (see sphereRadius), which has no orientation to
    // drift in the first place.
    [SerializeField] private float spinSpeed = 720f; // degrees/sec — 720 = 2 full spins per second.

    private CharacterStatsPD _stats;
    private Vector3 _baseScale;
    private float[] _baseYScales;
    private float _baseRangeScaleTargetY;
    private float _speed;
    private float _maxRange;
    private float _strengthPercent;
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
        if (rangeScaleTarget != null) _baseRangeScaleTargetY = rangeScaleTarget.localScale.y;

        if (yScaleFollowers != null)
        {
            _baseYScales = new float[yScaleFollowers.Length];
            for (int i = 0; i < yScaleFollowers.Length; i++)
                if (yScaleFollowers[i] != null)
                    _baseYScales[i] = yScaleFollowers[i].localScale.y;
        }

        // Same "correct for the player's default 1x1x1-unit sphere" constant WeaponBasePD.
        // ApplyVisualAdjustments()/WeaponRangedProjectilePD's own Awake() lift orbiting/flying weapon models
        // by — this projectile's own root spawns at the caster's un-lifted, ground-level position
        // (ExeAxeThrowAbilityPD.Activate() reads _stats.transform.position, not a visually-lifted model), so
        // the visual mesh needs the same compensating offset to render at the same mid-body height every
        // other weapon model appears to fire from. No explicit * bonusSizePercent here (deliberately — this
        // is a LOCAL offset, and Launch() below already scales this projectile's own root by
        // stats.bonusSizePercent, so Unity's ordinary parent-child scale inheritance applies that multiply
        // for free). Set once here, not in Launch() — this value never changes per-throw, and Awake() is
        // guaranteed to fire exactly once per real GameObject instance even under this projectile's own
        // SetActive-based pooling, so a pooled reuse can never re-run this and drift.
        if (visualHeightTarget != null)
        {
            var p = visualHeightTarget.localPosition;
            visualHeightTarget.localPosition = new Vector3(p.x, WeaponVisualConstants.BaseMidHeightLift, p.z);
        }
    }

    // Called by ExeAxeThrowAbilityPD.Activate() immediately after hierarchy.Create() returns — synchronous
    // and deterministic, same "mutate currentState right after Create()" pattern as every other spawned
    // one-shot effect in this codebase.
    public void Launch(CharacterStatsPD stats, Vector3 direction, float speed, float maxRange, float strengthPercent)
    {
        _stats = stats;
        _speed = speed;
        _maxRange = maxRange;
        _strengthPercent = strengthPercent;

        currentState.direction = direction;
        currentState.distanceTravelled = 0f;
        currentState.spinAngle = 0f;
        currentState.expired = false;

        transform.rotation = Quaternion.LookRotation(direction);
        // Uniform — scales with the caster's own Size (bonusSizePercent) on every axis alike, same
        // convention as WeaponRangedProjectilePD's own Launch().
        transform.localScale = _baseScale * stats.bonusSizePercent;

        // See rangeScaleTarget's own doc comment — purely cosmetic, X/Z only, Y left at its cached authored
        // value. LOCAL scale, not multiplied by bonusSizePercent again here — rangeScaleTarget is a
        // descendant of this root, so it already inherits that multiply for free via ordinary Unity
        // parent-child scale inheritance; multiplying it in again here would double-count it.
        if (rangeScaleTarget != null)
        {
            float rangeScale = stats.WeaponRange * rangeScaleMultiplier;
            rangeScaleTarget.localScale = new Vector3(rangeScale, _baseRangeScaleTargetY, rangeScale);

            // Same ratio drives yScaleFollowers — rangeScaleTarget's own baseline X/Z is 1 (an unscaled
            // "design" prefab), so rangeScale doubles as the growth ratio relative to that baseline, same
            // shape as WeaponBasePD.ScaleModel's own _baseYScales[i] * xRatio.
            if (_baseYScales != null)
            {
                for (int i = 0; i < yScaleFollowers.Length; i++)
                {
                    if (yScaleFollowers[i] == null) continue;
                    var s = yScaleFollowers[i].localScale;
                    yScaleFollowers[i].localScale = new Vector3(s.x, _baseYScales[i] * rangeScale, s.z);
                }
            }
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
        Vector3 step    = state.direction * (_speed * delta);
        transform.position = prevPos + step;
        state.distanceTravelled += step.magnitude;
        state.spinAngle = Mathf.Repeat(state.spinAngle + spinSpeed * delta, 360f);
        // Base facing (still tracks the original throw direction, never retargeted) with the tumble layered
        // on top around the local up axis — applied to the actual root transform, so the boxcast below sweeps
        // with the spun orientation too (see spinSpeed's own doc comment).
        transform.rotation = Quaternion.LookRotation(state.direction) * Quaternion.AngleAxis(state.spinAngle, Vector3.up);

        float dist = step.magnitude;
        if (dist > 0.0001f)
        {
            float radius = sphereRadius * transform.lossyScale.x;
            int n = Physics.SphereCastNonAlloc(prevPos, radius, step / dist, _sweepResults, dist, CombatLayerMasks.EnemyHurtboxMask);

            IEnemyPD hitEnemy = null;
            float bestHitDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var enemy = _sweepResults[i].collider.GetComponent<IEnemyPD>();
                if (enemy == null || enemy._isDead || _hitTargets.Contains(enemy)) continue;
                if (_sweepResults[i].distance < bestHitDist)
                {
                    bestHitDist = _sweepResults[i].distance;
                    hitEnemy = enemy;
                }
            }

            // Always pierces — see class doc comment — so a hit never expires the projectile here; only
            // running out of range (below) does.
            if (hitEnemy != null)
                ProcessHit(hitEnemy);
        }

        if (!state.expired && state.distanceTravelled >= _maxRange)
            state.expired = true;
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

        // Own crit-chance formula, not a straight read of _stats.CritChance — see baseCritChance/
        // critChanceFromPlayer's own doc comment for why.
        float effectiveCritChance = baseCritChance + _stats.CritChance * critChanceFromPlayer;
        bool isCrit = GameRandom.instance.NextFloat() < effectiveCritChance;
        if (isCrit) damage *= _stats.CritDamage * critDamageMultiplier;
        if (isHighroll) _stats.NotifyHighroll(damage);

        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = damage, onHitPower = _stats.OnHitPower, isCrit = isCrit };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);

        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);

        // Unpenalized — CharacterStatsPD.Knockback alone, no OnHitPower multiply (unlike
        // WeaponRangedProjectilePD's own `_knockback * onHitPower`), per the user's own call.
        Vector3 knockDir = (enemy.transform.position - transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _stats.Knockback);

        _stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = damage, isCrit = isCrit, isHighroll = isHighroll, attacker = _stats, isPriority = true });
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
