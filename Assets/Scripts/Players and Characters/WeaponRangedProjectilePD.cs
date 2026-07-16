using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// A short-lived, self-contained flying projectile — spawned by WeaponRangedPD.Fire(), not a child of the
// weapon that fired it (an already-loosed shot keeps flying regardless of whether its firer is currently
// alive). Deliberately NOT a WeaponBasePD<TState> subclass — none of that shared orbit/range-scaling
// visual machinery (hitboxTarget, rangeScaleTarget, rangeFollowers, etc.) applies to a fixed-size flying
// object, matching the old system where WeaponArrow was a plain NetworkIdentity, not a WeaponBase
// subclass. Box-sweep hit detection, ricochet retargeting, and multiplicative per-hit damage/on-hit-power
// dropoff port directly from the old WeaponArrow.
public class WeaponRangedProjectilePD : PredictedIdentity<WeaponRangedProjectilePD.State>
{
    public struct State : IPredictedData<State>
    {
        public Vector3 direction;
        public float   distanceTravelled;
        public int     ricochetsRemaining;
        public float   currentDamage;
        public float   currentOnHitPower;
        // Set once max range is reached; the actual hierarchy.Delete() is deferred to UpdateView() off
        // verified state, same pattern as EnemyBasePD's death handling — deleting directly from
        // LateSimulate() would be acting on a not-yet-confirmed tick.
        public bool    expired;
        public void Dispose() { }
    }

    [SerializeField] private Vector3 arrowHalfExtents = new(0.1f, 0.1f, 0.25f);

    private CharacterStatsPD _stats;
    // The WeaponRangedPD that fired this shot — this class deliberately isn't a WeaponBasePD<TState> (no
    // orbit/range-scaling visuals apply to a projectile), so it rolls its own crit but has no _onCritStrike
    // of its own to invoke. Relaying through the launcher (which does have one) keeps every weapon type's
    // crit flowing through the same weapon-level event WeaponManagerPD/CharacterStatsPD already subscribe
    // to, rather than reaching CharacterStatsPD directly as a special case.
    private WeaponRangedPD _launcher;
    private Vector3 _baseScale;
    private float _speed;
    private float _maxRange;
    private float _critChance;
    private float _critDamage;
    private float _knockback;
    private float _damageDropoff;
    private float _onHitDropoff;

    // Plain (not predicted State) — same "soft" tracking pattern as WeaponMeleePD._hitCooldowns: a rare
    // resim-window double-hit is a minor cosmetic hiccup here, not a desync, and this avoids needing a
    // stable cross-machine id for something IEnemyPD doesn't expose one for.
    private readonly HashSet<IEnemyPD> _hitTargets = new();
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];
    private readonly Collider[] _retargetResults = new Collider[16];
    private readonly List<(IEnemyPD enemy, float dmg, bool isCrit, bool isHighroll)> _hitFeedbackQueue = new();

    // ── Launch ────────────────────────────────────────────────────────────────

    // Plain Unity Awake(), NOT PredictedIdentity's LateAwake() override — LateAwake() is PD's own
    // "logical spawn" hook and DOES re-fire on every pooled reuse (confirmed: caching _baseScale there
    // caused it to re-capture an already-WeaponSize-scaled transform.localScale each reuse, compounding
    // indefinitely). Raw Unity Awake() is guaranteed to fire exactly once per real GameObject instance
    // even under SetActive-based pooling, same reason EnemyBasePD's own private Awake() (not its
    // LateAwake() override) is where it caches prefab-level things like _animator/baseHealth once.
    private void Awake()
    {
        _baseScale = transform.localScale;
    }

    // Separate from WeaponBasePD<TState>'s own _onHighroll — this class is deliberately not a subclass of
    // it, so it needs its own PredictedEvent. Constructed in LateAwake() (not Awake()) since a fresh
    // PredictedEvent object per logical spawn is what we want here — unlike _baseScale above, there's no
    // accumulation risk from rebuilding it every pooled reuse (nothing holds a reference to the old one
    // across reuses, unlike an accumulating += subscription would).
    // Private (not protected like WeaponBasePD's) — ProcessHit(), the only invoke site, lives in this same
    // class, not a subclass. Matches CharacterStatsPD._onHeal/PlayerControllerPD._onDashStarted's
    // private-field + public wrapper-method convention.
    private PredictedEvent<float> _onHighroll;

    public void AddHighrollListener(System.Action<float> listener) => _onHighroll.AddListener(listener);
    public void RemoveHighrollListener(System.Action<float> listener) => _onHighroll.RemoveListener(listener);

    protected override void LateAwake()
    {
        base.LateAwake();
        _onHighroll = new PredictedEvent<float>(predictionManager, this);
    }

    // Called by WeaponRangedPD.Fire() immediately after hierarchy.Create() returns — synchronous and
    // deterministic, same "mutate currentState right after Create()" pattern as XPOrbPD.SetValue(). Values
    // are captured once here (not re-read live from stats later), so a level-up mid-flight can't
    // retroactively change an already-loosed shot's damage. WeaponSize scaling and color application port
    // directly from the old WeaponBow.Fire() — but scale is set as an absolute multiple of _baseScale, not
    // multiplied onto the current value, since a pooled instance's current scale is whatever WeaponSize
    // left it at during its PREVIOUS life; `*=` would compound further every reuse instead of resetting.
    // _hitTargets/_hitFeedbackQueue are plain fields too (not part of predicted State, so GetInitialState()
    // can't reset them) — cleared here so a recycled instance doesn't inherit a previous life's hit history.
    public void Launch(WeaponRangedPD launcher, CharacterStatsPD stats, Vector3 direction, float speed, int ricochets)
    {
        _launcher = launcher;
        _stats = stats;
        _speed          = speed;
        _maxRange       = stats.WeaponRange * 1.25f;
        _critChance     = stats.CritChance;
        _critDamage     = stats.CritDamage;
        _knockback      = stats.Knockback;
        _damageDropoff  = stats.ProjectileDamageDropoff;
        _onHitDropoff   = stats.ProjectileOnHitDropoff;

        currentState.direction           = direction;
        currentState.ricochetsRemaining  = ricochets;
        currentState.currentDamage       = stats.WeaponDamage;
        currentState.currentOnHitPower   = stats.OnHitPower;

        transform.rotation    = Quaternion.LookRotation(direction);
        transform.localScale  = _baseScale * stats.WeaponSize;

        _hitTargets.Clear();
        _hitFeedbackQueue.Clear();

        foreach (var a in GetComponentsInChildren<PlayerColorApplicator>(true))
            a.Apply(stats.PlayerColor);
    }

    protected override State GetInitialState() => default;

    // ── Simulation ────────────────────────────────────────────────────────────

    // Intentionally empty beyond what GetInitialState/Launch already set up — movement and hit detection
    // both need the post-physics-pass position of anything they interact with, so everything happens in
    // LateSimulate(), same reasoning as WeaponMeleePD.
    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (_stats == null) { state.expired = true; return; } // shouldn't happen — Launch() always sets it
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        Vector3 prevPos = transform.position;
        Vector3 step    = state.direction * (_speed * delta);
        Vector3 newPos  = prevPos + step;
        state.distanceTravelled += step.magnitude;

        transform.position = newPos;

        float dist = step.magnitude;
        if (dist > 0.0001f)
        {
            Vector3 halfExtents = Vector3.Scale(arrowHalfExtents, transform.lossyScale);
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
            if (hitEnemy != null) ProcessHit(ref state, hitEnemy);
        }

        if (state.distanceTravelled >= _maxRange)
            state.expired = true;
    }

    // ── Hit / ricochet ────────────────────────────────────────────────────────

    private void ProcessHit(ref State state, IEnemyPD enemy)
    {
        _hitTargets.Add(enemy);

        float damage = WeaponDamageMath.RollDamage(state.currentDamage, _stats.LuckChance, _stats.LuckFloor, _stats.LuckCeiling, _stats.Highroll, out bool isHighroll);
        if (isHighroll) damage *= 1f + _stats.HighrollBonus;
        bool  isCrit = GameRandom.instance.NextFloat() < _critChance;
        if (isCrit) damage *= _critDamage;
        if (isHighroll) _onHighroll.Invoke(damage);
        if (isCrit && _launcher != null) _launcher.NotifyCritStrike(enemy.ObjectId);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
        Vector3 knockDir = (enemy.transform.position - transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _knockback);
        _hitFeedbackQueue.Add((enemy, damage, isCrit, isHighroll));

        // Per-hit multiplicative decay (not per-distance) — confirmed from the old WeaponArrow.ProcessHit.
        state.currentDamage     *= 1f - _damageDropoff;
        state.currentOnHitPower *= 1f - _onHitDropoff;

        if (state.ricochetsRemaining > 0)
        {
            var target = FindRicochetTarget(transform.position);
            if (target != null)
            {
                state.direction = (target.transform.position - transform.position).normalized;
                transform.rotation = Quaternion.LookRotation(state.direction);
                state.distanceTravelled = 0f; // fresh flight budget for this leg
                state.ricochetsRemaining--;
            }
            // No valid target found: keeps flying straight on its current heading until range runs out.
        }
    }

    // Deliberately a fixed-radius physics query, not the old system's FindObjectsByType<EnemyBase> scan —
    // that isn't deterministic/replay-safe (iteration order isn't guaranteed, and it wouldn't roll back
    // correctly), a physics query against a synced physics world is. Radius matches _maxRange (the same
    // WeaponRange * 1.25 the projectile flies) since distanceTravelled resets to 0 on every ricochet leg —
    // each bounce gets the same full flight budget to reach its new target as the initial shot did.
    private IEnemyPD FindRicochetTarget(Vector3 from)
    {
        int n = Physics.OverlapSphereNonAlloc(from, _maxRange, _retargetResults, CombatLayerMasks.EnemyHurtboxMask);
        IEnemyPD best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var enemy = _retargetResults[i].GetComponent<IEnemyPD>();
            if (enemy == null || enemy._isDead || _hitTargets.Contains(enemy)) continue;
            float dist = Vector3.Distance(from, enemy.transform.position);
            if (dist < bestDist) { bestDist = dist; best = enemy; }
        }
        return best;
    }

    // ── View ──────────────────────────────────────────────────────────────────

    protected override void UpdateView(State viewState, State? verified)
    {
        // State-critical — only act on server-confirmed expiry, same reasoning as EnemyBasePD's death
        // handling (an in-flight misprediction correction could otherwise delete/undelete this repeatedly).
        if (verified.HasValue && verified.Value.expired)
        {
            hierarchy.Delete(this);
            return;
        }

        if (_stats == null) return;

        // Fire hit feedback immediately at render rate — bypasses state propagation delay, same split as
        // WeaponMeleePD.
        foreach (var (enemy, dmg, isCrit, isHighroll) in _hitFeedbackQueue)
            enemy.TriggerHitFeedback(dmg, isCrit, isHighroll, _stats);
        _hitFeedbackQueue.Clear();
    }
}
