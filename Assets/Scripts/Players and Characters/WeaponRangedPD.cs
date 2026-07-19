using System.Collections.Generic;
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

    private bool _initialized;

    // Same per-enemy hit cooldown as WeaponMeleePD — an enemy currently on cooldown is skipped when
    // picking a target, but if firing at a different eligible target happens to also hit a cooling-down
    // enemy along the way (e.g. it was standing behind the intended target), that's fine; the cooldown
    // only gates target SELECTION, not the projectile's own hit detection.
    private float HitCooldown => _stats != null ? 135f / _stats.WeaponSpeed : 0.25f;
    private readonly Dictionary<IEnemyPD, float> _hitCooldowns = new();
    private readonly List<IEnemyPD> _cooldownBuffer = new();

    // Ally targeting (HealAllyOnStrikeAugment, real friendly fire, future "hit ally, do something"
    // augments) — only ever populated/queried when _stats.allyStrikeHitEnabled or
    // _stats.allyStrikeDamageEnabled is true. Separate from _hitCooldowns since an ally is a
    // CharacterStatsPD, not an IEnemyPD; same per-target cooldown-gates-SELECTION-only reasoning as the
    // enemy dictionary above. This weapon only ever picks a target and fires toward it — the actual
    // heal/damage happens once the launched WeaponRangedProjectilePD physically reaches the target.
    private readonly Dictionary<CharacterStatsPD, float> _allyHitCooldowns = new();
    private readonly List<CharacterStatsPD> _allyCooldownBuffer = new();

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

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_manager != null) _manager.UnregisterWeapon(this);
    }

    protected override State GetInitialState() => default;

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!_initialized) TryInit();

        // Tick cooldowns at render rate — not in Simulate, so resims can't expire them early. Same
        // pattern as WeaponMeleePD.UpdateView().
        _cooldownBuffer.Clear();
        _cooldownBuffer.AddRange(_hitCooldowns.Keys);
        foreach (var e in _cooldownBuffer)
        {
            _hitCooldowns[e] -= Time.deltaTime;
            if (_hitCooldowns[e] <= 0f) _hitCooldowns.Remove(e);
        }

        _allyCooldownBuffer.Clear();
        _allyCooldownBuffer.AddRange(_allyHitCooldowns.Keys);
        foreach (var a in _allyCooldownBuffer)
        {
            _allyHitCooldowns[a] -= Time.deltaTime;
            if (_allyHitCooldowns[a] <= 0f) _allyHitCooldowns.Remove(a);
        }
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

        // Outer-track weapons (OuterTrackAugment) spin the opposite direction — see
        // WeaponMeleePD.LateSimulate's own angleSign comment for the full reasoning. No sub-arc sweep here
        // (ranged just fires at whatever's in front of it), so no startAngle reconstruction needed.
        float   currentAngle = (IsOuterTrack ? -1f : 1f) * _stats.WeaponOrbitAngle;
        Vector3 origin = _stats.transform.position;
        Vector3 orbitPos = OrbitalPosition(currentAngle, origin);
        Vector3 outward  = orbitPos - origin;
        if (outward == Vector3.zero) return;

        transform.SetPositionAndRotation(orbitPos, Quaternion.LookRotation(outward));

        state.fireTimer -= delta;
        if (state.fireTimer > 0f) return;

        if (!FindTarget(transform.position, transform.forward, out IEnemyPD target, out CharacterStatsPD allyTarget, out Vector3 hurtboxCenter)) return;

        if (target != null) _hitCooldowns[target] = HitCooldown;
        else                 _allyHitCooldowns[allyTarget] = HitCooldown;
        Fire(hurtboxCenter);
        // Hard reset, not += — fireTimer can drift arbitrarily negative while no target is in the forward
        // ray (the common case for a thin raycast), and accumulating the cooldown on top of that debt
        // instead of clearing it would cause a rapid-fire burst to pay it off once a target reappears.
        state.fireTimer = 1f / Mathf.Max(0.01f, _stats.AttackSpeed * fireRateMultiplier);
    }

    // ── Targeting ─────────────────────────────────────────────────────────────

    // Pulls the ray origin back 1 unit and pads the range by 1 to match — same as old WeaponBow — so an
    // enemy pressed right up against the weapon's own origin still gets hit instead of the ray starting
    // inside/past it. Enemies currently on _hitCooldowns are skipped so the ray "sees past" them to
    // whatever's next in line, same reason the old WeaponBow tracked _initialHitCooldowns.
    //
    // Single combined raycast against Enemy + (when detectAllies) Player layers, so an ally competes
    // for "closest valid target" on exactly the same footing as an enemy — the exact same targeting logic,
    // just with a wider mask, rather than a separate ally-only pass. enemy/ally outputs are mutually
    // exclusive (whichever the closest valid hit turned out to be); the caller branches cooldown
    // bookkeeping off which one came back non-null.
    private bool FindTarget(Vector3 from, Vector3 forward, out IEnemyPD enemy, out CharacterStatsPD ally, out Vector3 hurtboxCenter)
    {
        Vector3 origin = from - forward * 1f;
        float   range  = _stats.WeaponRange + 1f;
        bool    detectAllies = _stats.allyStrikeHitEnabled || _stats.allyStrikeDamageEnabled;
        int     mask = detectAllies
            ? CombatLayerMasks.EnemyHurtboxMask | CombatLayerMasks.PlayerHurtboxMask
            : CombatLayerMasks.EnemyHurtboxMask;

        int   n        = Physics.RaycastNonAlloc(origin, forward, _rayResults, range, mask);
        float bestDist = float.MaxValue;
        enemy = null;
        ally  = null;
        hurtboxCenter = default;

        for (int i = 0; i < n; i++)
        {
            if (_rayResults[i].distance >= bestDist) continue;
            var collider = _rayResults[i].collider;

            var e = collider.GetComponent<IEnemyPD>();
            if (e != null)
            {
                if (e._isDead || _hitCooldowns.ContainsKey(e)) continue;
                bestDist      = _rayResults[i].distance;
                enemy         = e;
                ally          = null;
                hurtboxCenter = collider.bounds.center;
                continue;
            }

            if (!detectAllies) continue;
            var a = collider.GetComponent<CharacterStatsPD>();
            if (a == null || a == _stats || a.IsDead || _allyHitCooldowns.ContainsKey(a)) continue;
            bestDist      = _rayResults[i].distance;
            enemy         = null;
            ally          = a;
            hurtboxCenter = collider.bounds.center;
        }
        return enemy != null || ally != null;
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
        float startAngle = multishot > 1 ? -_stats.MultishotSpreadAngle / 2f : 0f;
        float angleStep = multishot > 1 ? _stats.MultishotSpreadAngle / (multishot - 1) : 0f;

        for (int i = 0; i < multishot; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, startAngle + i * angleStep, 0f) * baseDir;
            var id = hierarchy.Create(projectilePrefab, origin, Quaternion.LookRotation(dir), owner);
            if (!id.HasValue) continue;

            var go = hierarchy.GetGameObject(id.Value);
            if (go != null && go.TryGetComponent(out WeaponRangedProjectilePD projectile))
                projectile.Launch(this, _stats, dir, projectileSpeed, ricochet);
        }
    }
}
