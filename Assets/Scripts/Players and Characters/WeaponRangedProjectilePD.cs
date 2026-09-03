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
        // Set once max range is reached — gameplay ceases (LateSimulate stops moving/hit-testing) and
        // renderers hide the same tick (see UpdateView), but the object itself stays alive a bit longer —
        // see expiredTimer/readyToDelete below for why.
        public bool    expired;
        // Counts down (simulated delta, ticked in LateSimulate — NOT real time) from postExpireLingerDuration
        // once expired goes true. Exists purely so the cosmetic trail particle system (its own authored
        // lifetime is roughly this same duration) has time to finish drawing itself out to wherever the arrow
        // actually vanished, instead of abruptly cutting off mid-trail the instant the arrow itself disappears.
        public float   expiredTimer;
        // Set once expiredTimer reaches 0 — the actual hierarchy.Delete() is deferred to UpdateView() off
        // verified state (same pattern as EnemyBasePD's death handling — deleting directly from
        // LateSimulate() would be acting on a not-yet-confirmed tick), gated on THIS instead of `expired`
        // directly so the object survives the extra linger window before actually being deleted/pooled.
        public bool    readyToDelete;
        public void Dispose() { }
    }

    [SerializeField] private Vector3 arrowHalfExtents = new(0.1f, 0.1f, 0.25f);
    [SerializeField] private Renderer[] _renderers;
    // How much longer this projectile stays alive (simulated time) after expiring/hiding before actually
    // deleting — see State.expiredTimer's own comment. Tunable, not a hardcoded const, since it's meant to
    // roughly match the trail particle system's own authored "Start Lifetime" — retune together if that
    // ever changes.
    [SerializeField] private float postExpireLingerDuration = 0.5f;
    // Cosmetic-only trail particle system(s) — paused/resumed with PausePD in UpdateView() below, same
    // shape as ExeAxeThrowProjectilePD's own UpdateParticlePause (duplicated here rather than shared, same
    // "this class isn't a WeaponBasePD subclass" reasoning). No death/renderer linkage — unlike
    // ExeAxeThrowProjectilePD, this one's whole reason for existing is to keep drawing itself out for a
    // while AFTER the arrow itself hides, so it must not be Stopped alongside SetRenderersEnabled(false).
    [SerializeField] private ParticleSystem[] particleSystems;
    // Visual-only target for the mid-height lift below — a dedicated child (e.g. this prefab's "Model", the
    // parent of every visible mesh piece), NOT this projectile's own root transform (that's the real,
    // hit-detection-relevant flight position LateSimulate() moves every tick). Optional: leave unassigned
    // and this projectile simply never lifts, no error.
    [SerializeField] private Transform visualHeightTarget;

    private CharacterStatsPD _stats;
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

    // Ally-strike detection (OnStrikeHealAllyAugment and future "hit ally, do something" augments) — always
    // runs now as a pure byproduct of the shot's own flight path toward its real (enemy) target; there is
    // deliberately no way to aim AT an ally anymore (see WeaponRangedPD.FindTarget's own doc comment, and
    // FindRicochetTarget below, which no longer considers allies either) — an ally only ever gets struck by
    // "happening to be standing in the way." Unlike _hitTargets (one closest enemy per tick, gates further
    // damage to that same enemy), every ally the sweep touches gets processed the same tick — no
    // "closest only" restriction, since a cluster of allies this shot happens to cross all reacting isn't a
    // balance concern the way multi-hitting enemies would be. Still gated per-ally for the whole flight so
    // a slow/wide shot lingering on one ally doesn't reprocess it every tick while still overlapping.
    private readonly HashSet<IFriendlyPD> _struckAllies = new();

    // ── Launch ────────────────────────────────────────────────────────────────

    // Plain Unity Awake(), NOT PredictedIdentity's LateAwake() override — LateAwake() is PD's own
    // "logical spawn" hook and DOES re-fire on every pooled reuse (confirmed: caching _baseScale there
    // caused it to re-capture an already-Size-scaled transform.localScale each reuse, compounding
    // indefinitely). Raw Unity Awake() is guaranteed to fire exactly once per real GameObject instance
    // even under SetActive-based pooling, same reason EnemyBasePD's own private Awake() (not its
    // LateAwake() override) is where it caches prefab-level things like _animator/baseHealth once.
    private void Awake()
    {
        _baseScale = transform.localScale;

        // Same "correct for the player's default 1x1x1-unit sphere" constant WeaponBasePD.
        // ApplyVisualAdjustments() lifts orbiting weapon models by — this projectile's own root spawns at
        // the firing weapon's un-lifted, ground-level position (WeaponRangedPD.Fire()'s `transform.position`
        // reads the weapon root, not its own visually-lifted model), so the visual mesh needs the same
        // compensating offset to render at the same mid-body height the bow model appears to fire from. No
        // explicit * bonusSizePercent here (deliberately — "no size mods", matching ApplyVisualAdjustments'
        // own reasoning): this is a LOCAL offset, and Launch() below already scales this projectile's own
        // root by stats.bonusSizePercent, so Unity's ordinary parent-child scale inheritance applies that
        // multiply for free. Set once here, not in Launch() — this value never changes per-launch (no
        // per-shot state it could depend on), and Awake() is guaranteed to fire exactly once per real
        // GameObject instance even under this projectile's own SetActive-based pooling (see this method's
        // own comment above), so a pooled reuse can never re-run this and drift — same reasoning already
        // established for _baseScale right above. The assignment itself is also absolute (not relative,
        // unlike a hypothetical `+=`), so even a repeat call couldn't compound it regardless.
        if (visualHeightTarget != null)
        {
            var p = visualHeightTarget.localPosition;
            visualHeightTarget.localPosition = new Vector3(p.x, WeaponVisualConstants.BaseMidHeightLift, p.z);
        }
    }

    // Called by WeaponRangedPD.Fire() immediately after hierarchy.Create() returns — synchronous and
    // deterministic, same "mutate currentState right after Create()" pattern as XPOrbPD.SetValue(). Values
    // are captured once here (not re-read live from stats later), so a level-up mid-flight can't
    // retroactively change an already-loosed shot's damage. Size scaling and color application port
    // directly from the old WeaponBow.Fire() — but scale is set as an absolute multiple of _baseScale, not
    // multiplied onto the current value, since a pooled instance's current scale is whatever bonusSizePercent
    // left it at during its PREVIOUS life; `*=` would compound further every reuse instead of resetting.
    // _hitTargets is a plain field too (not part of predicted State, so GetInitialState() can't reset it) —
    // cleared here so a recycled instance doesn't inherit a previous life's hit history.
    // effectiveWeaponRange is WeaponRangedPD.EffectiveWeaponRange (Stats.WeaponRange, scaled by the firing
    // weapon's own isRanged/rangeMultiplier if it's set) — passed in rather than re-read here so this
    // projectile's own max travel distance can never drift out of sync with the raycast/aim distance
    // WeaponRangedPD itself just used to find this target.
    public void Launch(CharacterStatsPD stats, Vector3 direction, float speed, int ricochets, float effectiveWeaponRange)
    {
        _stats = stats;
        _speed          = speed;
        _maxRange       = effectiveWeaponRange * 1.25f;
        _critChance     = stats.CritChance;
        _critDamage     = stats.CritDamage;
        _knockback      = stats.Knockback;
        _damageDropoff  = stats.ProjectileDamageDropoff;
        _onHitDropoff   = stats.ProjectileOnHitDropoff;

        currentState.direction           = direction;
        currentState.ricochetsRemaining  = ricochets;
        currentState.currentDamage       = stats.WeaponStrength;
        currentState.currentOnHitPower   = stats.WeaponOnHitPower;

        transform.rotation    = Quaternion.LookRotation(direction);
        transform.localScale  = _baseScale * stats.bonusSizePercent;

        _hitTargets.Clear();
        _struckAllies.Clear();

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
        if (state.readyToDelete) return;
        // shouldn't happen — Launch() always sets it — but still needs to go through the same linger window
        // rather than skipping straight to deletion, in case anything ever reads state mid-linger.
        if (_stats == null) { state.expired = true; state.expiredTimer = postExpireLingerDuration; return; }
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        if (state.expired)
        {
            // Gameplay already ceased and renderers already hidden (see UpdateView) — just count down the
            // trail's own catch-up window (simulated delta, so this respects pause the same as everything
            // else here) before actually deleting.
            state.expiredTimer -= delta;
            if (state.expiredTimer <= 0f) state.readyToDelete = true;
            return;
        }

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

            int an = Physics.BoxCastNonAlloc(prevPos, halfExtents, step / dist, _sweepResults, transform.rotation, dist, CombatLayerMasks.FriendlyHurtboxMask);
            for (int i = 0; i < an; i++)
            {
                var ally = _sweepResults[i].collider.GetComponent<IFriendlyPD>();
                if (ally == null || ReferenceEquals(ally, _stats) || _struckAllies.Contains(ally)) continue;
                _struckAllies.Add(ally);
                HitAlly(ref state, ally);
            }
        }

        if (state.distanceTravelled >= _maxRange)
        {
            state.expired = true;
            state.expiredTimer = postExpireLingerDuration;
        }
    }

    // ── Hit / ricochet ────────────────────────────────────────────────────────

    private void ProcessHit(ref State state, IEnemyPD enemy)
    {
        _hitTargets.Add(enemy);

        float damage = WeaponDamageMath.RollDamage(state.currentDamage, _stats.LuckChance, _stats.LuckFloor, _stats.LuckCeiling, _stats.Highroll, out bool isHighroll);
        bool  isCrit = GameRandom.instance.NextFloat() < _critChance;
        damage = WeaponDamageMath.ApplyCritAndHighroll(damage, isHighroll, isCrit, _stats.HighrollBonus, _critDamage);
        if (isHighroll) _stats.NotifyStrikeHighroll(damage, state.currentOnHitPower);
        // state.currentDamage/currentOnHitPower are this shot's already ricochet-decayed values BEFORE this
        // hit's own decay below is applied — downstream on-hit effects see exactly what this specific hit
        // (e.g. the 3rd bounce) actually represented, not the shot's original full-power baseline.
        var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = state.currentDamage, onHitPower = state.currentOnHitPower, isCrit = isCrit };
        _stats.NotifyHit(hitData);
        _stats.NotifyStrike(hitData);
        if (isCrit) _stats.NotifyCritStrike(hitData);
        enemy.TakeDamage(damage, isCrit, _stats.owner ?? default);
        Vector3 knockDir = (enemy.transform.position - transform.position).normalized;
        enemy.AddKnockbackVelocity(knockDir * _knockback * state.currentOnHitPower);
        _stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = damage, isCrit = isCrit, isHighroll = isHighroll, attacker = _stats, isPriority = true });

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

    // A touched ally only ever fires the _onAllyStrike notification now — real friendly fire has been
    // removed from the project entirely (see CharacterStatsPD.NotifyAllyStrike's own doc comment). Still
    // applies ApplyHitDropoffAndRicochet — a touch still counts as "a hit" for the shot's own
    // dropoff/ricochet bookkeeping, same as it did before.
    private void HitAlly(ref State state, IFriendlyPD ally)
    {
        if (ally.IsDead) return;
        _stats.NotifyAllyStrike(new AllyStrikeData { ally = ally, onHitPower = state.currentOnHitPower });
        ApplyHitDropoffAndRicochet(ref state);
    }

    // Deliberately a fixed-radius physics query, not the old system's FindObjectsByType<EnemyBase> scan —
    // that isn't deterministic/replay-safe (iteration order isn't guaranteed, and it wouldn't roll back
    // correctly), a physics query against a synced physics world is. Radius matches _maxRange (the same
    // effectiveWeaponRange * 1.25 the projectile flies) since distanceTravelled resets to 0 on every ricochet leg —
    // each bounce gets the same full flight budget to reach its new target as the initial shot did.
    //
    // Enemies only — a ricochet is a deliberate re-aim, not a "happy accident" graze, so it follows the
    // same rule WeaponRangedPD.FindTarget's initial aim does now: no seeking allies out. An ally can still
    // get struck mid-flight (see LateSimulate's own box-sweep above), just never by the shot actively
    // redirecting toward one. Returns a plain position (not IEnemyPD) since the caller only ever needs
    // where to aim next.
    private bool FindRicochetTarget(Vector3 from, out Vector3 targetPosition)
    {
        int n = Physics.OverlapSphereNonAlloc(from, _maxRange, _retargetResults, CombatLayerMasks.EnemyHurtboxMask);
        float bestDist = float.MaxValue;
        targetPosition = default;
        bool found = false;

        for (int i = 0; i < n; i++)
        {
            var enemy = _retargetResults[i].GetComponent<IEnemyPD>();
            if (enemy == null || enemy._isDead || _hitTargets.Contains(enemy)) continue;
            float dist = Vector3.Distance(from, enemy.transform.position);
            if (dist < bestDist) { bestDist = dist; targetPosition = enemy.transform.position; found = true; }
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

        UpdateParticlePause();

        // State-critical — only act on server-confirmed readiness, same reasoning as EnemyBasePD's death
        // handling (an in-flight misprediction correction could otherwise delete/undelete this repeatedly).
        // Gated on readyToDelete, not expired — see State.readyToDelete's own comment for the linger window
        // this waits out first.
        if (verified.HasValue && verified.Value.readyToDelete)
        {
            hierarchy.Delete(this);
        }
    }

    // Every real frame (not gated to verified sim ticks — purely cosmetic, same reasoning
    // WeaponBasePD.UpdateWeaponParticlePause's own doc comment gives). Pause()/Play() only, no Stop — see
    // particleSystems' own field comment for why this must NOT be linked to SetRenderersEnabled here.
    private void UpdateParticlePause()
    {
        if (particleSystems == null) return;
        bool paused = PausePD.instance != null && PausePD.instance.IsPaused;
        for (int i = 0; i < particleSystems.Length; i++)
        {
            var ps = particleSystems[i];
            if (ps == null) continue;
            if (paused && ps.isPlaying) ps.Pause();
            else if (!paused && ps.isPaused) ps.Play();
        }
    }
}
