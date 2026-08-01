using System;
using PurrNet.Prediction;
using UnityEngine;

// The one generic multi-cast chain for the whole "AoE Effects" family — every caller (DashExplosionAugment,
// DashHealAugment, ShieldBreakBurstAugment, KnightAreaStunHealAbilityPD) spawns this same class and just
// passes different Launch() parameters (targetAllies/dealDamage/onEnemyHit/onAllyHit/onCastComplete — see
// AreaEffectCastHelper.Cast's own doc comment for the full breakdown) rather than each needing its own
// near-duplicate chain type. Adds Stats.AreaEffectCount > 1 support (a full retriggerRate-apart multi-cast
// sequence) to whatever single AreaEffectCastHelper.Cast() call the owning augment/ability would otherwise
// have to fire directly. One instance per trigger, each fully self-contained (own position, own countdown),
// so multiple overlapping triggers (e.g. back-to-back dashes) each get their own independent chain instead of
// sharing one overwritable slot. This exists instead of a List<T> inside a single shared State struct because
// IPredictedData<TState> structs are snapshotted BY VALUE for rollback — a List<T> field would only copy the
// reference, so every snapshot would alias the same underlying list and corrupt each other under
// resimulation (the same class of bug [[feedback_manual_queue_drain_pattern_unsafe]] already covers).
// Spawning one of these per trigger instead keeps each chain's data — and its resimulation-safety — fully
// self-contained, the same way any other pooled spawned effect in this codebase is.
public class AreaEffectPD : PredictedIdentity<AreaEffectPD.State>
{
    public struct State : IPredictedData<State>
    {
        public int remainingCasts;
        public float retriggerTimer;
        // Set one tick AFTER remainingCasts reaches 0, not the same tick — the final _onRecast.Invoke() only
        // actually fires its listener once THIS tick is verified, and UpdateView's deletion must not race
        // ahead of that. Deleting off remainingCasts<=0 directly would risk pooling/deleting the object
        // before the last cast's deferred event listener ever runs, silently eating the final retrigger.
        public bool  finished;
        public void Dispose() { }
    }

    // Plain fields, not State — set once by Launch() (only ever called from a verified-only context,
    // immediately after hierarchy.Create()) and never mutated again, same "mutate currentState right after
    // Create()" pattern as RadiusIndicatorVFXPD.Launch()/XPOrbPD.SetValue(). Storing the raw CharacterStatsPD
    // owner reference directly (not via PredictedObjectID) matches PhantomBladeSummonHelper's own precedent
    // for a short-lived spawned effect referencing its owner.
    private CharacterStatsPD _owner;
    private Vector3 _castPosition;
    private float _radius;
    private float _retriggerRate;
    private GameObject _radiusVFXPrefab;
    // Passed straight through to every cast in the chain (not just the first) — see AreaEffectCastHelper.Cast's
    // own doc comment for what each means. Defaults match Cast()'s own enemy-targeting defaults, so a caller
    // that doesn't care about the ally-only or callback-only ones (e.g. DashExplosionAugment) can just omit
    // them entirely.
    private bool _targetAllies;
    private bool _dealDamage = true;
    private float _damagePercent;
    private float _onHitPowerPercent;
    private bool _notifyOnHit = true;
    private bool _hasKnockback;
    private float? _knockback;
    private bool _interrupt;
    // 1f (neutral) default — see AreaEffectCastHelper.Cast's own doc comment on the identical parameter.
    // Only a real IAbilityPD implementer (e.g. ArcherArrowRainAbilityPD) ever passes something else in.
    private float _abilityPower = 1f;
    private Action<IEnemyPD> _onEnemyHit;
    private Action<CharacterStatsPD> _onAllyHit;
    // Fired once per cast with the total enemies/allies hit that cast — e.g. KnightAreaStunHealAbilityPD
    // healing itself based on how many enemies got stunned. null (default) for callers that don't need it.
    private Action<int> _onCastComplete;

    private PredictedEvent _onRecast;

    protected override State GetInitialState() => default;

    // totalCasts is the FULL count including the immediate cast fired from here (e.g. totalCasts=3 means
    // this cast now, then 2 more retriggers). Pooled — Launch() runs on every reuse, unlike LateAwake()
    // (not guaranteed to re-fire on pooled reuse), so _onRecast's one-time construction lives here instead,
    // guarded by the null check so it only actually happens once per real instance.
    public void Launch(CharacterStatsPD owner, Vector3 position, int totalCasts, float retriggerRate,
        float radius, GameObject radiusVFXPrefab,
        bool targetAllies = false, bool dealDamage = true, float damagePercent = 0f, float onHitPowerPercent = 0f,
        bool notifyOnHit = true, bool hasKnockback = false, float? knockback = null, bool interrupt = false,
        float abilityPower = 1f,
        Action<IEnemyPD> onEnemyHit = null, Action<CharacterStatsPD> onAllyHit = null, Action<int> onCastComplete = null)
    {
        if (_onRecast == null)
        {
            _onRecast = new PredictedEvent(predictionManager, this);
            _onRecast.AddListener(Cast);
        }

        _owner = owner;
        _castPosition = position;
        _radius = radius;
        _retriggerRate = retriggerRate;
        _radiusVFXPrefab = radiusVFXPrefab;
        _targetAllies = targetAllies;
        _dealDamage = dealDamage;
        _damagePercent = damagePercent;
        _onHitPowerPercent = onHitPowerPercent;
        _notifyOnHit = notifyOnHit;
        _hasKnockback = hasKnockback;
        _knockback = knockback;
        _interrupt = interrupt;
        _abilityPower = abilityPower;
        _onEnemyHit = onEnemyHit;
        _onAllyHit = onAllyHit;
        _onCastComplete = onCastComplete;

        currentState.remainingCasts = Mathf.Max(1, totalCasts) - 1;
        currentState.retriggerTimer = retriggerRate;

        // Spawned exactly once for the whole chain, here — NOT inside Cast() itself, which re-runs on every
        // retrigger (see AreaEffectCastHelper.SpawnRadiusVFX's own comment for why that used to double-spawn
        // it once per retrigger instead).
        AreaEffectCastHelper.SpawnRadiusVFX(hierarchy, _radiusVFXPrefab, _castPosition, _radius);

        Cast();
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (state.finished) return;
        if (state.remainingCasts <= 0) { state.finished = true; return; }
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (_owner == null || _owner.IsDead) { state.remainingCasts = 0; return; }

        state.retriggerTimer -= delta;
        if (state.retriggerTimer > 0f) return;

        state.remainingCasts--;
        _onRecast.Invoke();

        if (state.remainingCasts > 0)
            state.retriggerTimer = _retriggerRate;
    }

    private void Cast() =>
        AreaEffectCastHelper.Cast(hierarchy, _owner, _castPosition, _radius, _radiusVFXPrefab,
            targetAllies: _targetAllies, dealDamage: _dealDamage, damagePercent: _damagePercent, onHitPowerPercent: _onHitPowerPercent,
            notifyOnHit: _notifyOnHit, hasKnockback: _hasKnockback, knockback: _knockback, interrupt: _interrupt,
            abilityPower: _abilityPower,
            onEnemyHit: _onEnemyHit, onAllyHit: _onAllyHit, onCastComplete: _onCastComplete);

    protected override void UpdateView(State viewState, State? verified)
    {
        // State-critical — only despawn on server-confirmed completion, same reasoning as every other
        // spawned one-shot effect's deletion (RadiusIndicatorVFXPD/RemoteSpikeBurstPD/PhantomBladePD).
        // Gated on `finished`, not `remainingCasts <= 0` directly — see the State field's own comment.
        if (verified.HasValue && verified.Value.finished)
            hierarchy.Delete(this);
    }
}
