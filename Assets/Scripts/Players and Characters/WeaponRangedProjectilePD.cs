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
    [SerializeField] private Renderer[] _renderers;

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
    // Plain view-layer bookkeeping, not predicted state — same instant-predicted-hide pattern as
    // PhantomBladePD, guards SetRenderersEnabled(false) from running every frame once already hidden.
    private bool _hiddenOnExpire;

    // Plain (not predicted State) — same "soft" tracking pattern as WeaponMeleePD._hitCooldowns: a rare
    // resim-window double-hit is a minor cosmetic hiccup here, not a desync, and this avoids needing a
    // stable cross-machine id for something IEnemyPD doesn't expose one for.
    private readonly HashSet<IEnemyPD> _hitTargets = new();
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];
    private readonly Collider[] _retargetResults = new Collider[16];
    private readonly List<(IEnemyPD enemy, float dmg, bool isCrit, bool isHighroll)> _hitFeedbackQueue = new();

    // Ally-strike detection (HealAllyOnStrikeAugment, real friendly fire, future "hit ally, do something"
    // augments) — only queried when _stats.allyStrikeHitEnabled or _stats.allyStrikeDamageEnabled is true.
    // Unlike _hitTargets (one closest enemy per tick, gates further damage to that same enemy), every ally
    // the sweep touches gets processed the same tick — no "closest only" restriction, since a cluster of
    // allies this shot happens to cross all reacting isn't a balance concern the way multi-hitting enemies
    // would be. Still gated per-ally for the whole flight so a slow/wide shot lingering on one ally doesn't
    // reprocess it every tick while still overlapping.
    private readonly HashSet<CharacterStatsPD> _struckAllies = new();

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
        _struckAllies.Clear();
        _hitFeedbackQueue.Clear();

        // A pooled instance's previous life ends hidden (see UpdateView()'s predicted-hide below) — a
        // fresh launch needs to be visible again.
        _hiddenOnExpire = false;
        SetRenderersEnabled(true);

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

            if (_stats.allyStrikeHitEnabled || _stats.allyStrikeDamageEnabled)
            {
                int an = Physics.BoxCastNonAlloc(prevPos, halfExtents, step / dist, _sweepResults, transform.rotation, dist, CombatLayerMasks.PlayerHurtboxMask);
                for (int i = 0; i < an; i++)
                {
                    var ally = _sweepResults[i].collider.GetComponent<CharacterStatsPD>();
                    // Self-exclusion here, not just in NotifyAllyStrike — the direct TakeDamage call in
                    // HitAlly bypasses that guard entirely.
                    if (ally == null || ally == _stats || _struckAllies.Contains(ally)) continue;
                    _struckAllies.Add(ally);
                    HitAlly(ref state, ally);
                }
            }
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
        // state.currentDamage/currentOnHitPower are this shot's already ricochet-decayed values BEFORE this
        // hit's own decay below is applied — downstream on-hit effects see exactly what this specific hit
        // (e.g. the 3rd bounce) actually represented, not the shot's original full-power baseline.
        if (_launcher != null)
        {
            var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = state.currentDamage, onHitPower = state.currentOnHitPower, isCrit = isCrit };
            _launcher.NotifyHit(hitData);
            if (isCrit) _launcher.NotifyCritStrike(hitData);
        }
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
        Vector3 knockDir = (enemy.transform.position - transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _knockback * state.currentOnHitPower);
        _hitFeedbackQueue.Add((enemy, damage, isCrit, isHighroll));

        ApplyHitDropoffAndRicochet(ref state);
    }

    // Shared by every hit this shot lands — enemy (ProcessHit) or ally (HitAlly) — so an ally hit weakens
    // and can consume/redirect the shot's remaining ricochets exactly the same way an enemy hit already
    // does, rather than allies being "free" passes that dodge dropoff/ricochet bookkeeping entirely.
    // FindRicochetTarget itself also considers allies as valid retarget candidates (see its own doc
    // comment) — the closest not-yet-struck target wins regardless of type.
    private void ApplyHitDropoffAndRicochet(ref State state)
    {
        // Per-hit multiplicative decay (not per-distance) — confirmed from the old WeaponArrow.ProcessHit.
        state.currentDamage     *= 1f - _damageDropoff;
        state.currentOnHitPower *= 1f - _onHitDropoff;

        if (state.ricochetsRemaining > 0)
        {
            if (FindRicochetTarget(transform.position, out Vector3 targetPosition))
            {
                state.direction = (targetPosition - transform.position).normalized;
                transform.rotation = Quaternion.LookRotation(state.direction);
                state.distanceTravelled = 0f; // fresh flight budget for this leg
                state.ricochetsRemaining--;
            }
            // No valid target found: keeps flying straight on its current heading until range runs out.
        }
    }

    // allyStrikeHitEnabled and allyStrikeDamageEnabled are independent, same as WeaponMeleePD/WeaponAxePD's
    // own HitAlly. Real friendly fire uses this shot's already ricochet-decayed damage (same baseline
    // ProcessHit's enemy damage roll uses at this point in the flight) but never crits/knocks back —
    // CharacterStatsPD.TakeDamage doesn't support either for players. Always applies
    // ApplyHitDropoffAndRicochet regardless of which (if either) of the two bools is on — a pure heal-only
    // touch still counts as "a hit" for the shot's own dropoff/ricochet bookkeeping.
    private void HitAlly(ref State state, CharacterStatsPD ally)
    {
        if (ally.IsDead) return;

        if (_stats.allyStrikeDamageEnabled)
        {
            float damage = WeaponDamageMath.RollDamage(state.currentDamage, _stats.LuckChance, _stats.LuckFloor, _stats.LuckCeiling, _stats.Highroll, out bool isHighroll);
            if (isHighroll) damage *= 1f + _stats.HighrollBonus;
            ally.TakeDamage(damage);
        }

        if (_stats.allyStrikeHitEnabled)
            _stats.NotifyAllyStrike(new AllyStrikeData { ally = ally, onHitPower = state.currentOnHitPower });

        ApplyHitDropoffAndRicochet(ref state);
    }

    // Deliberately a fixed-radius physics query, not the old system's FindObjectsByType<EnemyBase> scan —
    // that isn't deterministic/replay-safe (iteration order isn't guaranteed, and it wouldn't roll back
    // correctly), a physics query against a synced physics world is. Radius matches _maxRange (the same
    // WeaponRange * 1.25 the projectile flies) since distanceTravelled resets to 0 on every ricochet leg —
    // each bounce gets the same full flight budget to reach its new target as the initial shot did.
    //
    // Allies are equally valid retarget candidates whenever allyStrikeHitEnabled/allyStrikeDamageEnabled is
    // on — the closest not-yet-struck-by-THIS-shot target wins regardless of whether it's an enemy or an
    // ally, same "closest valid candidate" rule FindTarget/WeaponRangedPD already applies for initial
    // target acquisition. Returns a plain position (not IEnemyPD) since the caller only ever needs where to
    // aim next, and the winning candidate could now be either type.
    private bool FindRicochetTarget(Vector3 from, out Vector3 targetPosition)
    {
        bool detectAllies = _stats.allyStrikeHitEnabled || _stats.allyStrikeDamageEnabled;
        int mask = detectAllies
            ? CombatLayerMasks.EnemyHurtboxMask | CombatLayerMasks.PlayerHurtboxMask
            : CombatLayerMasks.EnemyHurtboxMask;

        int n = Physics.OverlapSphereNonAlloc(from, _maxRange, _retargetResults, mask);
        float bestDist = float.MaxValue;
        targetPosition = default;
        bool found = false;

        for (int i = 0; i < n; i++)
        {
            var collider = _retargetResults[i];

            var enemy = collider.GetComponent<IEnemyPD>();
            if (enemy != null)
            {
                if (enemy._isDead || _hitTargets.Contains(enemy)) continue;
                float dist = Vector3.Distance(from, enemy.transform.position);
                if (dist < bestDist) { bestDist = dist; targetPosition = enemy.transform.position; found = true; }
                continue;
            }

            if (!detectAllies) continue;
            var ally = collider.GetComponent<CharacterStatsPD>();
            if (ally == null || ally == _stats || ally.IsDead || _struckAllies.Contains(ally)) continue;
            float allyDist = Vector3.Distance(from, ally.transform.position);
            if (allyDist < bestDist) { bestDist = allyDist; targetPosition = ally.transform.position; found = true; }
        }
        return found;
    }

    // ── View ──────────────────────────────────────────────────────────────────

    private void SetRenderersEnabled(bool value)
    {
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].enabled = value;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Instant, predicted hide — doesn't wait for server verification, unlike the actual pooled-object
        // deletion below. Same fix as PhantomBladePD: viewState is this machine's own already-simulated
        // prediction (identical deterministic movement/expiry math runs on every machine), available
        // immediately, whereas verified only populates for non-owner clients after a network round-trip —
        // that round-trip was the visible "arrow lingers after it should've disappeared" delay.
        if (viewState.expired && !_hiddenOnExpire)
        {
            _hiddenOnExpire = true;
            SetRenderersEnabled(false);
        }

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
            enemy.TriggerHitFeedback(dmg, isCrit, isHighroll, _stats, isPriority: true);
        _hitFeedbackQueue.Clear();
    }
}
