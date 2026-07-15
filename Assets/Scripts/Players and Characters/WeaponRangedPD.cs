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
    // Shots/sec = _stats.AttackSpeed * fireRateMultiplier — ported from the old WeaponBow, which used 12
    // here (AttackSpeed 0.5 -> 6 shots/sec).
    [SerializeField] private float fireRateMultiplier = 12f;

    private int  _enemyHurtboxMask;
    private bool _initialized;

    private readonly RaycastHit[] _rayResults = new RaycastHit[16];

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

        if (!FindTarget(transform.position, transform.forward, out Vector3 hurtboxCenter)) return;

        Fire(hurtboxCenter);
        state.fireTimer += 1f / Mathf.Max(0.01f, _stats.AttackSpeed * fireRateMultiplier);
    }

    // ── Targeting ─────────────────────────────────────────────────────────────

    // Pulls the ray origin back 1 unit and pads the range by 1 to match — same as old WeaponBow — so an
    // enemy pressed right up against the weapon's own origin still gets hit instead of the ray starting
    // inside/past it.
    private bool FindTarget(Vector3 from, Vector3 forward, out Vector3 hurtboxCenter)
    {
        Vector3 origin = from - forward * 1f;
        float   range  = _stats.WeaponRange + 1f;

        int   n        = Physics.RaycastNonAlloc(origin, forward, _rayResults, range, _enemyHurtboxMask);
        float bestDist = float.MaxValue;
        hurtboxCenter = default;
        bool found = false;

        for (int i = 0; i < n; i++)
        {
            var enemy = _rayResults[i].collider.GetComponentInParent<IEnemyPD>();
            if (enemy == null || enemy._isDead) continue;
            if (_rayResults[i].distance >= bestDist) continue;

            bestDist      = _rayResults[i].distance;
            hurtboxCenter = _rayResults[i].collider.bounds.center;
            found = true;
        }
        return found;
    }

    // ── Fire ──────────────────────────────────────────────────────────────────

    // Ricochet/multishot fractional-roll mechanic ported from the old WeaponBow.Fire() — the integer part
    // of each stat is guaranteed, the fractional part is a probability roll for one more (e.g. Ricochet =
    // 1.5 gives 1 bounce always, 2 bounces half the time). GameRandom, never UnityEngine.Random — this
    // runs from LateSimulate(), a deterministic/replay-safe context.
    private void Fire(Vector3 targetPosition)
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
        Vector3 baseDir = (targetPosition - origin).normalized;
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
