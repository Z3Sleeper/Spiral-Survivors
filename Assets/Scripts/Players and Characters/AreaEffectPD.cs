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
    private Action<IEnemyPD> _onEnemyHit;
    private Action<IFriendlyPD> _onAllyHit;
    // Fired once per cast with the total enemies/allies hit that cast — e.g. KnightAreaStunHealAbilityPD
    // healing itself based on how many enemies got stunned. null (default) for callers that don't need it.
    private Action<int> _onCastComplete;
    // See AreaEffectCastHelper.Cast's own doc comment — passed straight through, only meaningful when
    // _interrupt is true.
    private Action<IEnemyPD> _onSuccessfulInterrupt;
    // See AreaEffectCastHelper.Cast's own doc comment — passed straight through to every cast in the chain,
    // same as every other Launch() parameter above.
    private bool _overrideHitSfx;
    private AudioClip _hitSfxOverride;
    // See Launch()'s own doc comment on the parameter this mirrors.
    private bool _vfxPerCast;

    private PredictedEvent _onRecast;

    protected override State GetInitialState() => default;

    // totalCasts is the FULL count including the immediate cast fired from here (e.g. totalCasts=3 means
    // this cast now, then 2 more retriggers). Pooled — Launch() runs on every reuse, unlike LateAwake()
    // (not guaranteed to re-fire on pooled reuse), so _onRecast's one-time construction lives here instead,
    // guarded by the null check so it only actually happens once per real instance.
    // vfxPerCast: false (default) spawns the radius VFX exactly once for the whole chain regardless of
    // totalCasts — correct for a caller whose totalCasts represents rapid inner ticks of one ongoing effect
    // (ArcherArrowRainAbilityPD's 20-tick DoT: one visible zone, not 20 flashes). true spawns it fresh on
    // EVERY cast including the first, including retriggers — correct for a caller whose totalCasts instead
    // represents Stats.AreaEffectCount recasts of a whole discrete burst spaced retriggerRate apart (e.g.
    // DashHealAugment/DashExplosionAugment/ShieldBreakBurstAugment/KnightAreaStunHealAbilityPD), where each
    // recast is its own distinct, perceptible event that should visibly flash same as the first one did —
    // silently losing the VFX on every recast after the first (the actual bug this flag fixes) happened
    // because those callers share this exact totalCasts/retriggerRate mechanism with ArrowRain's own
    // once-only need, with nothing to tell the two cases apart before now.
    public void Launch(CharacterStatsPD owner, Vector3 position, int totalCasts, float retriggerRate,
        float radius, GameObject radiusVFXPrefab,
        bool targetAllies = false, bool dealDamage = true, float damagePercent = 0f, float onHitPowerPercent = 0f,
        bool notifyOnHit = true,
        bool hasKnockback = false, float? knockback = null, bool interrupt = false,
        Action<IEnemyPD> onEnemyHit = null, Action<IFriendlyPD> onAllyHit = null, Action<int> onCastComplete = null,
        Action<IEnemyPD> onSuccessfulInterrupt = null,
        bool overrideHitSfx = false, AudioClip hitSfxOverride = null, bool vfxPerCast = false)
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
        _onEnemyHit = onEnemyHit;
        _onAllyHit = onAllyHit;
        _onCastComplete = onCastComplete;
        _onSuccessfulInterrupt = onSuccessfulInterrupt;
        _overrideHitSfx = overrideHitSfx;
        _hitSfxOverride = hitSfxOverride;
        _vfxPerCast = vfxPerCast;

        currentState.remainingCasts = Mathf.Max(1, totalCasts) - 1;
        currentState.retriggerTimer = retriggerRate;

        // vfxPerCast: false — spawned exactly once for the whole chain, here, NOT inside Cast() itself,
        // which re-runs on every retrigger (see AreaEffectCastHelper.SpawnRadiusVFX's own comment for why
        // that used to double-spawn it once per retrigger instead). vfxPerCast: true — Cast() spawns it
        // fresh every time instead (see this method's own doc comment), including this first call, so
        // skipped here to avoid a double-spawn on cast #1.
        if (!_vfxPerCast)
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

    private void Cast()
    {
        // See Launch()'s own doc comment — fires fresh on every cast (this one included) when vfxPerCast is
        // true, instead of the single Launch()-time-only spawn used otherwise.
        if (_vfxPerCast)
            AreaEffectCastHelper.SpawnRadiusVFX(hierarchy, _radiusVFXPrefab, _castPosition, _radius);

        AreaEffectCastHelper.Cast(hierarchy, _owner, _castPosition, _radius, _radiusVFXPrefab,
            targetAllies: _targetAllies, dealDamage: _dealDamage, damagePercent: _damagePercent, onHitPowerPercent: _onHitPowerPercent,
            notifyOnHit: _notifyOnHit,
            hasKnockback: _hasKnockback, knockback: _knockback, interrupt: _interrupt,
            onEnemyHit: _onEnemyHit, onAllyHit: _onAllyHit, onCastComplete: _onCastComplete,
            onSuccessfulInterrupt: _onSuccessfulInterrupt,
            overrideHitSfx: _overrideHitSfx, hitSfxOverride: _hitSfxOverride);
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // State-critical — only despawn on server-confirmed completion, same reasoning as every other
        // spawned one-shot effect's deletion (RadiusIndicatorVFXPD/RemoteSpikeBurstPD/PhantomBladePD).
        // Gated on `finished`, not `remainingCasts <= 0` directly — see the State field's own comment.
        if (verified.HasValue && verified.Value.finished)
            hierarchy.Delete(this);
    }
}
