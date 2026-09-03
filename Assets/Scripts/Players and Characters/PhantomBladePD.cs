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

    // Flat base spawn distance from the target — every Phantom Blade summoning augment scales this by its
    // own sizeMultiplier (Summon() does the same internally to derive maxRange), so this single constant
    // is the one source of truth for "how far away from the target" rather than each augment tracking its
    // own copy.
    public const float BaseSpawnRadius = 5f;

    // Every blade travels for exactly this long, regardless of size — larger blades cover more distance
    // (radius scales with sizeMultiplier, see Summon()) so their speed scales up to match, rather than
    // taking longer to arrive.
    [SerializeField] private float travelTime = 0.5f;

    [SerializeField] private Vector3 bladeHalfExtents = new(0.1f, 0.1f, 0.5f);
    [SerializeField] private Renderer[] _renderers;
    // Whichever shader property actually drives this model's own base/tint color — URP Lit uses "_BaseColor",
    // Built-in/most Unlit shaders use "_Color". Left Inspector-configurable rather than hardcoded since this
    // script has no visibility into which shader the blade's own material actually uses — verify against the
    // real material and retune here if the tint doesn't visibly apply.
    [SerializeField] private string colorPropertyName = "_BaseColor";

    // Reused across every SetColor call (both this instance's own repeated Summon()s via pooling, and every
    // other PhantomBladePD instance) — MaterialPropertyBlock is a plain reusable buffer object, not
    // per-material state, so a single static instance is safe and avoids a per-summon allocation. Applied via
    // Renderer.SetPropertyBlock rather than Renderer.material (which would instantiate a unique material
    // copy per blade — real GC/draw-call cost at swarm scale, see feedback_multiplayer_scale_reminder).
    private static MaterialPropertyBlock _mpb;

    private CharacterStatsPD _owner;
    private Vector3 _baseScale;
    private float _damage;
    private bool  _isCrit;
    private float _speed;
    private float _maxRange;
    // Baked in at Summon() time from CharacterStatsPD.phantomBladeOnHitEnabled, same "one-shot bonus baked
    // in at spawn" pattern as _damage/size — false unless PhantomBladeOnHitAugment (or a future family
    // member) has granted it, in which case this blade fires OnHit for every enemy it strikes. Kept as a
    // separate bool rather than gating off _onHitPower > 0 (see CharacterStatsPD.phantomBladeOnHitEnabled's
    // own doc comment for why).
    private bool  _onHitEnabled;
    // Fully resolved once at Summon() time (see its own comment) — (penalized-or-unpenalized OnHitPower) *
    // baseDamageMultiplier * phantomBladeOnHitPowerPercent, reused for every enemy this blade hits rather
    // than recomputed per hit, same "baked in once, reused for the whole flight" convention as _damage/
    // _isCrit above.
    private float _onHitPower;
    // Plain view-layer bookkeeping, not predicted state — guards SetRenderersEnabled(false) from running
    // every single frame once already hidden (harmless either way, just avoids redundant work).
    private bool _hiddenOnExpire;

    // Plain (not predicted State) — same "soft" tracking pattern as WeaponRangedProjectilePD._hitTargets.
    private readonly HashSet<IEnemyPD> _hitTargets = new();
    private readonly RaycastHit[] _sweepResults = new RaycastHit[16];

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
    // later, matching old's Init()/ApplySize() baking size into speed/range once at spawn. color is purely
    // cosmetic (ApplyColor) — lets each summoning augment tint its own blades distinctly, see
    // PhantomBladeSummonHelper's own doc comment for where that per-augment color actually comes from.
    //
    // BaseSpawnRadius * sizeMultiplier is the same distance the caller used (or should use, via
    // BaseSpawnRadius directly) to place the spawn point away from the target, along the straight line the
    // blade flies — the blade travels exactly 2x that (radius to reach the target, then the same distance
    // again to end up radius past it on the opposite side). Speed is derived from that distance and the
    // fixed travelTime, rather than travelTime being derived from a fixed speed, so every blade takes the
    // same time to finish its flight regardless of size.
    public void Summon(CharacterStatsPD owner, float damage, float sizeMultiplier, bool isCrit, float baseDamageMultiplier, Color color)
    {
        float radius = BaseSpawnRadius * sizeMultiplier;

        ApplyColor(color);

        _owner = owner;
        _damage = damage;
        _isCrit = isCrit;
        _maxRange = radius * 2f;
        _speed = _maxRange / travelTime;
        _onHitEnabled = owner.phantomBladeOnHitEnabled;
        // Reads owner.OnHitPower directly (2026-08-19 — was a usePenalizedOnHitPower ternary against the
        // since-removed divided OnHitPower; see PhantomBladeSummonHelper.PreviewDamage's own doc comment for
        // the full reasoning) — every blade, regardless of which augment summoned it, uses the same
        // undivided OnHitPower now. baseDamageMultiplier is the exact same ratio (e.g. 6f for "600%
        // Strength") that augment used to roll this blade's own damage, so a blade given a bigger/smaller
        // cut of Strength scaling gets a proportionally bigger/smaller cut of OnHitPower too, instead of
        // every blade reporting the same flat onHitPower regardless of how strong that particular summon
        // was tuned to be.
        _onHitPower = owner.OnHitPower * baseDamageMultiplier * owner.phantomBladeOnHitPowerPercent;

        transform.localScale = _baseScale * sizeMultiplier;

        currentState.distanceTravelled = 0f;
        currentState.expired = false;

        // A pooled instance's previous life ends hidden (see UpdateView()'s predicted-hide below) — a
        // fresh summon needs to be visible again.
        _hiddenOnExpire = false;
        SetRenderersEnabled(true);

        _hitTargets.Clear();
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

        // _isCrit was rolled once at Summon() time (see PhantomBladeSummonHelper.SummonAtTarget) and baked
        // into _damage already — every enemy this blade hits sees the same crit status for its whole
        // flight, not a fresh per-hit roll.
        enemy.TakeDamage(_damage, _isCrit, _owner.owner ?? default);
        _owner.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = _damage, isCrit = _isCrit, isHighroll = false, attacker = _owner, isPriority = true });

        // Only fires if some Phantom Blade augment actually enabled it — most blades never touch the OnHit
        // chain at all. weaponDamage uses this blade's own already-rolled damage (the closest available
        // analog to "this hit's damage" for a non-weapon hit source); onHitPower uses _onHitPower, fully
        // resolved once at Summon() time (see its own doc comment).
        if (_onHitEnabled)
        {
            var hitData = new WeaponHitData { target = enemy.ObjectId, weaponDamage = _damage, onHitPower = _onHitPower, isCrit = _isCrit };
            _owner.NotifyHit(hitData);
        }
    }

    // ── View ──────────────────────────────────────────────────────────────────

    private void SetRenderersEnabled(bool value)
    {
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].enabled = value;
    }

    // Re-applied every Summon() (2026-09-01) — lets each Phantom Blade augment tint its own blades a
    // distinct color (PhantomBladeSummonHelper threads a per-augment Color through). SetPropertyBlock, not
    // Renderer.material, so this never instantiates a unique material copy per blade (real cost at swarm
    // scale — see feedback_multiplayer_scale_reminder) — every blade shares the same material asset, just
    // with a per-renderer property override layered on top, same convention Unity's own per-instance-color
    // examples use.
    private void ApplyColor(Color color)
    {
        if (_renderers == null) return;
        _mpb ??= new MaterialPropertyBlock();
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null) continue;
            _renderers[i].GetPropertyBlock(_mpb);
            _mpb.SetColor(colorPropertyName, color);
            _renderers[i].SetPropertyBlock(_mpb);
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Instant, predicted hide — doesn't wait for server verification, unlike the actual pooled-object
        // deletion below. viewState reflects this machine's own already-simulated prediction (the same
        // deterministic movement/expiry math runs on every machine, owner and non-owners alike), so it's
        // available immediately — verified only becomes populated for non-owner clients after a network
        // round-trip, which was the visible "disappearance is delayed" symptom. Only ever hides
        // (never un-hides on its own) since a resimulation correcting expired back to false mid-flight
        // isn't a real scenario here (distanceTravelled only counts up); Launch() is what re-shows it for
        // a fresh pooled reuse.
        if (viewState.expired && !_hiddenOnExpire)
        {
            _hiddenOnExpire = true;
            SetRenderersEnabled(false);
        }

        // State-critical — only act on server-confirmed expiry, same reasoning as
        // WeaponRangedProjectilePD/EnemyBasePD's death handling. A misprediction here would otherwise risk
        // a delete/undelete oscillation, which the instant hide above deliberately avoids by never
        // touching the actual pooled object.
        if (verified.HasValue && verified.Value.expired)
        {
            hierarchy.Delete(this);
        }
    }
}
