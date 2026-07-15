using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// A short-lived, self-contained flying projectile — spawned by WeaponRangedPD.Fire(), not a child of the
// weapon that fired it (an already-loosed shot keeps flying regardless of whether its firer is currently
// alive). Ricochet retargeting and multiplicative per-hit damage/on-hit-power dropoff port directly from
// the old WeaponArrow.ProcessHit.
public class WeaponRangedProjectilePD : WeaponBasePD<WeaponRangedProjectilePD.State>
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

    public override bool IsMelee => false;

    [SerializeField] private float hitRadius = 0.25f;
    // Search radius for a fresh target once the current one's been hit and ricochetsRemaining > 0.
    // Deliberately a fixed-radius physics query, not the old system's FindObjectsByType<EnemyBase> scan —
    // that isn't deterministic/replay-safe (iteration order isn't guaranteed, and it wouldn't roll back
    // correctly), a physics query against a synced physics world is.
    [SerializeField] private float ricochetSearchRadius = 15f;

    private int   _enemyHurtboxMask;
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
    private readonly List<(IEnemyPD enemy, float dmg, bool isCrit)> _hitFeedbackQueue = new();

    // ── Launch ────────────────────────────────────────────────────────────────

    // Called by WeaponRangedPD.Fire() immediately after hierarchy.Create() returns — synchronous and
    // deterministic, same "mutate currentState right after Create()" pattern as XPOrbPD.SetValue(). Values
    // are captured once here (not re-read live from stats later), so a level-up mid-flight can't
    // retroactively change an already-loosed shot's damage.
    public void Launch(CharacterStatsPD stats, Vector3 direction, float speed, int ricochets)
    {
        Init(stats);
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
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

        transform.rotation = Quaternion.LookRotation(direction);
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
            int n = Physics.SphereCastNonAlloc(prevPos, hitRadius, step / dist, _sweepResults, dist, _enemyHurtboxMask);
            IEnemyPD hitEnemy = null;
            float bestHitDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var enemy = _sweepResults[i].collider.GetComponentInParent<IEnemyPD>();
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

        float damage = RollDamage(state.currentDamage);
        bool  isCrit = GameRandom.instance.NextFloat() < _critChance;
        if (isCrit) damage *= _critDamage;
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
        Vector3 knockDir = (enemy.transform.position - transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _knockback);
        _hitFeedbackQueue.Add((enemy, damage, isCrit));

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

    private IEnemyPD FindRicochetTarget(Vector3 from)
    {
        int n = Physics.OverlapSphereNonAlloc(from, ricochetSearchRadius, _retargetResults, _enemyHurtboxMask);
        IEnemyPD best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var enemy = _retargetResults[i].GetComponentInParent<IEnemyPD>();
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
        foreach (var (enemy, dmg, isCrit) in _hitFeedbackQueue)
            enemy.TriggerHitFeedback(dmg, isCrit, _stats);
        _hitFeedbackQueue.Clear();
    }
}
