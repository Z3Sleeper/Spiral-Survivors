using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Players and Characters/PhantomBlade.cs. Same category as
// WeaponRangedProjectilePD ("standalone spawned effect, not IEnemyPD, not a weapon") — flies straight in
// its launch facing direction and damages whatever it passes through. The target enemy passed to whatever
// spawns this is only used to compute spawn position/rotation once, before Launch() — old's own `target`
// field was otherwise unused dead weight and isn't carried over (no continuous homing).
public class PhantomBladePD : PredictedIdentity<PhantomBladePD.State>
{
    public struct State : IPredictedData<State>
    {
        public float distanceTravelled;
        // Set once max range is reached; the actual hierarchy.Delete() is deferred to UpdateView() off
        // verified state, same pattern as WeaponRangedProjectilePD/EnemyBasePD's death handling.
        public bool  expired;
        public void Dispose() { }
    }

    private const float BaseSpeed    = 10f;
    private const float BaseMaxRange = 6f;

    [SerializeField] private Vector3 bladeHalfExtents = new(0.1f, 0.1f, 0.5f);

    private CharacterStatsPD _owner;
    private Vector3 _baseScale;
    private float _damage;
    private float _speed;
    private float _maxRange;

    // Plain (not predicted State) — same "soft" tracking pattern as WeaponRangedProjectilePD._hitTargets.
    private readonly HashSet<IEnemyPD> _hitTargets = new();
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];
    private readonly List<(IEnemyPD enemy, float dmg)> _hitFeedbackQueue = new();

    // Plain Unity Awake(), NOT LateAwake() — LateAwake() re-fires on every pooled reuse, which would
    // re-capture an already-scaled transform.localScale each reuse and compound indefinitely. Same
    // reasoning as WeaponRangedProjectilePD._baseScale.
    private void Awake()
    {
        _baseScale = transform.localScale;
    }

    // Called by whatever spawns this (e.g. PhantomBladeOnCritAugment) immediately after hierarchy.Create()
    // returns — synchronous and deterministic, same "mutate currentState right after Create()" pattern as
    // WeaponRangedProjectilePD.Launch(). damage/sizeMultiplier are captured once here, not re-read live
    // later, matching old's Init()/ApplySize() baking size into speed/range once at spawn.
    public void Launch(CharacterStatsPD owner, float damage, float sizeMultiplier)
    {
        _owner = owner;
        _damage = damage;
        _speed = BaseSpeed * sizeMultiplier;
        _maxRange = BaseMaxRange * sizeMultiplier;

        transform.localScale = _baseScale * sizeMultiplier;

        currentState.distanceTravelled = 0f;
        currentState.expired = false;

        _hitTargets.Clear();
        _hitFeedbackQueue.Clear();
    }

    protected override State GetInitialState() => default;

    // ── Simulation ────────────────────────────────────────────────────────────

    // Intentionally empty beyond what GetInitialState/Launch already set up — movement and hit detection
    // both need the post-physics-pass position of anything they interact with, so everything happens in
    // LateSimulate(), same reasoning as WeaponRangedProjectilePD.
    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (_owner == null) { state.expired = true; return; } // shouldn't happen — Launch() always sets it
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        Vector3 prevPos = transform.position;
        Vector3 step = transform.forward * (_speed * delta);
        transform.position = prevPos + step;
        state.distanceTravelled += step.magnitude;

        float dist = step.magnitude;
        if (dist > 0.0001f)
        {
            Vector3 halfExtents = Vector3.Scale(bladeHalfExtents, transform.lossyScale);
            int n = Physics.BoxCastNonAlloc(prevPos, halfExtents, step / dist, _sweepResults, transform.rotation, dist, CombatLayerMasks.EnemyHurtboxMask);
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
            if (hitEnemy != null) ProcessHit(hitEnemy);
        }

        if (state.distanceTravelled >= _maxRange)
            state.expired = true;
    }

    private void ProcessHit(IEnemyPD enemy)
    {
        _hitTargets.Add(enemy);

        // Blades don't crit themselves — matches old's plain enemy.TakeDamageRpc(damage) call with no
        // crit involved at all.
        enemy.TakeDamage(_damage, isCrit: false, _owner.owner ?? default);
        _hitFeedbackQueue.Add((enemy, _damage));
    }

    // ── View ──────────────────────────────────────────────────────────────────

    protected override void UpdateView(State viewState, State? verified)
    {
        // State-critical — only act on server-confirmed expiry, same reasoning as
        // WeaponRangedProjectilePD/EnemyBasePD's death handling.
        if (verified.HasValue && verified.Value.expired)
        {
            hierarchy.Delete(this);
            return;
        }

        if (_owner == null) return;

        // Fire hit feedback immediately at render rate — bypasses state propagation delay, same split as
        // WeaponRangedProjectilePD.
        foreach (var (enemy, dmg) in _hitFeedbackQueue)
            enemy.TriggerHitFeedback(dmg, isCrit: false, isHighroll: false, _owner);
        _hitFeedbackQueue.Clear();
    }
}
