using PurrNet.Prediction;
using UnityEngine;

// Orbits its owner exactly like WeaponMeleePD (same shared OrbitalPosition() formula on WeaponBasePD<TState>)
// but never touches anything directly — instead, once per cooldown window, it looks for the nearest enemy
// it has an unobstructed line of sight to from its current orbital position and fires a
// WeaponRangedProjectilePD at it. No target = no fire; the cooldown holds at/below zero and retries every
// tick until one becomes available, so it fires the instant something's in range/LoS rather than on some
// fixed cadence unrelated to target availability.
public class WeaponRangedPD : WeaponBasePD<WeaponRangedPD.State>
{
    public struct State : IPredictedData<State>
    {
        public float angle;
        public float fireTimer;
        public void Dispose() { }
    }

    public override bool IsMelee => false;

    [Header("Ranged")]
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField] private float projectileSpeed = 20f;
    // Raycast against this to confirm a candidate target is actually visible, not behind a wall — a mask
    // of 0 (nothing assigned) means "no obstruction layers configured," so the LoS check is skipped
    // entirely rather than treated as "always blocked."
    [SerializeField] private LayerMask obstacleLayerMask;

    private int  _enemyHurtboxMask;
    private bool _initialized;

    private readonly Collider[] _searchResults = new Collider[32];

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void LateAwake()
    {
        TryInit();
    }

    private void TryInit()
    {
        if (!owner.HasValue) return;
        if (!CharacterStatsPD.All.TryGetValue(owner.Value, out var stats)) return;
        var mgr = stats.GetComponent<WeaponManagerPD>();
        if (mgr == null) return;
        _manager = mgr;
        _enemyHurtboxMask = LayerMask.GetMask("EnemyHurtbox");
        _initialized = true;
        mgr.RegisterWeapon(this);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_manager != null) _manager.UnregisterWeapon(this);
    }

    protected override State GetInitialState() => default;

    // ── Simulation ────────────────────────────────────────────────────────────

    protected override void Simulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        state.angle -= _stats.WeaponSpeed * delta;
    }

    // Same post-physics-pass timing reasoning as WeaponMeleePD.LateSimulate() — _stats.transform.position
    // (and therefore this weapon's own orbital position) needs to reflect the player's current-tick,
    // post-physics position, not a tick-stale one.
    protected override void LateSimulate(ref State state, float delta)
    {
        if (!_initialized || _stats == null || _stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        Vector3 origin = _stats.transform.position;
        Vector3 orbitPos = OrbitalPosition(state.angle, origin);
        Vector3 outward  = orbitPos - origin;
        if (outward == Vector3.zero) return;

        transform.SetPositionAndRotation(orbitPos, Quaternion.LookRotation(outward));

        state.fireTimer -= delta;
        if (state.fireTimer > 0f) return;

        var target = FindTarget(transform.position);
        if (target == null) return;

        Fire(target);
        state.fireTimer += 1f / Mathf.Max(0.01f, _stats.AttackSpeed);
    }

    // ── Targeting ─────────────────────────────────────────────────────────────

    private IEnemyPD FindTarget(Vector3 from)
    {
        int n = Physics.OverlapSphereNonAlloc(from, _stats.WeaponRange, _searchResults, _enemyHurtboxMask);
        IEnemyPD best = null;
        float bestDist = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var enemy = _searchResults[i].GetComponentInParent<IEnemyPD>();
            if (enemy == null || enemy._isDead) continue;

            Vector3 toEnemy = enemy.transform.position - from;
            float dist = toEnemy.magnitude;
            if (dist >= bestDist) continue;

            if (obstacleLayerMask.value != 0 && Physics.Raycast(from, toEnemy.normalized, dist, obstacleLayerMask))
                continue;

            best = enemy;
            bestDist = dist;
        }
        return best;
    }

    // ── Fire ──────────────────────────────────────────────────────────────────

    // Ricochet/multishot fractional-roll mechanic ported from the old WeaponBow.Fire() — the integer part
    // of each stat is guaranteed, the fractional part is a probability roll for one more (e.g. Ricochet =
    // 1.5 gives 1 bounce always, 2 bounces half the time). GameRandom, never UnityEngine.Random — this
    // runs from LateSimulate(), a deterministic/replay-safe context.
    private void Fire(IEnemyPD target)
    {
        if (projectilePrefab == null) return;

        float rawRicochet = _stats.Ricochet;
        int ricochet = (int)rawRicochet;
        if (GameRandom.instance.NextFloat() < rawRicochet - ricochet) ricochet++;

        float rawMultishot = _stats.Multishot;
        int bonusShots = (int)rawMultishot;
        if (GameRandom.instance.NextFloat() < rawMultishot - bonusShots) bonusShots++;
        int multishot = 1 + bonusShots;

        Vector3 origin = transform.position;
        Vector3 baseDir = (target.transform.position - origin).normalized;
        float startAngle = multishot > 1 ? -_stats.multishotSpreadAngle / 2f : 0f;
        float angleStep = multishot > 1 ? _stats.multishotSpreadAngle / (multishot - 1) : 0f;

        for (int i = 0; i < multishot; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, startAngle + i * angleStep, 0f) * baseDir;
            var id = hierarchy.Create(projectilePrefab, origin, Quaternion.LookRotation(dir), owner);
            if (!id.HasValue) continue;

            var go = hierarchy.GetGameObject(id.Value);
            if (go != null && go.TryGetComponent(out WeaponRangedProjectilePD projectile))
                projectile.Launch(_stats, dir, projectileSpeed, ricochet);
        }
    }
}
