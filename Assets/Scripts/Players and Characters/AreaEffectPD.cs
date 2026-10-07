using System;
using PurrNet.Prediction;
using UnityEngine;

// The one generic multi-cast chain for the whole "AoE Effects" family — every caller (DashExplosionAugment,
// DashHealAugment, ShieldBreakBurstAugment, KnightBuffStunAbilityPD) spawns this same class and just
// passes different Launch() parameters (targetAllies/dealDamage/onEnemyHit/onAllyHit/onCastComplete — see
// AreaEffectCastHelper.Cast's own doc comment for the full breakdown) rather than each needing its own
// near-duplicate chain type. Supports a totalCasts > 1 sequence (a full retriggerRate-apart multi-cast) on
// top of whatever single AreaEffectCastHelper.Cast() call the owning augment/ability would otherwise have to
// fire directly — every caller currently passes a flat totalCasts of 1 (Stats.AreaEffectCount, the stat that
// used to drive this, was removed entirely 2026-09-13), but the class itself stays generic in case a future
// caller wants a genuine multi-cast sequence again. One instance per trigger, each fully self-contained (own
// position, own countdown),
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

    // Mutates the chain's own gameplay hitbox radius mid-activation — every subsequent Cast() reads
    // _radius fresh, so this takes effect on the very next tick with no other bookkeeping needed. Called
    // externally by ArcherArrowRainAbilityPD's own Simulate() (ArcherArrowRainGrowthAugment) once per tick,
    // NOT routed through the VFX growth path (_radiusVfxGrowthPerSecond) — that's a separate, purely
    // cosmetic constant-rate interpolation on the already-spawned VFX instance, deliberately decoupled from
    // this discrete per-tick gameplay value (see ArcherArrowRainGrowthAugment's own doc comment for why a
    // tiny visual/gameplay mismatch here is intentional and accepted).
    public void SetRadius(float newRadius) => _radius = newRadius;
    private GameObject _radiusVFXPrefab;
    // See RadiusIndicatorVFXPD.TintColor's own doc comment — null for every caller except ArcherArrowRainAbilityPD.
    private Color? _radiusVfxTintColor;
    // See AreaEffectCastHelper.SpawnRadiusVFX's own abilityDuration param doc comment — null for every
    // caller except ArcherArrowRainAbilityPD.
    private float? _radiusVfxAbilityDuration;
    // See AreaEffectCastHelper.SpawnRadiusVFX's own radiusGrowthPerSecond param doc comment — null for
    // every caller except ArcherArrowRainAbilityPD (ArcherArrowRainGrowthAugment).
    private float? _radiusVfxGrowthPerSecond;
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
    // Fired once per cast with the total enemies/allies hit that cast — e.g. KnightBuffStunAbilityPD
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
    // See AreaEffectCastHelper.Cast's own doc comment — passed straight through to every cast in the chain.
    private bool? _forcedCrit;
    private bool? _forcedHighroll;
    // null (default) — every cast in the chain uses _damagePercent uniformly, unchanged behavior for every
    // existing caller. Non-null (2026-10-05, ArcherArrowRainAbilityPD's own duration/tickInterval-derived
    // pro-rated final tick) overrides JUST the LAST cast of the sequence with this percent instead — see
    // Cast()'s own use for how "last cast" is identified off currentState.remainingCasts.
    private float? _finalCastDamagePercentOverride;
    // null (default) — the retrigger TRANSITION into the final cast uses _retriggerRate uniformly, same as
    // every other transition, unchanged behavior for every existing caller. Non-null overrides JUST that
    // one transition's delay — paired with _finalCastDamagePercentOverride above so the final cast can land
    // at an EXACT arbitrary time (e.g. ArcherArrowRainAbilityPD's own duration, when it isn't a clean
    // multiple of tickInterval) rather than on the regular tickInterval-spaced grid every other retrigger
    // uses. See Simulate()'s own use for exactly which transition this affects.
    private float? _finalRetriggerDelayOverride;

    private PredictedEvent _onRecast;

    protected override State GetInitialState() => default;

    // totalCasts/retriggerRate default to a single, non-repeating cast (2026-09-13, moved to the end of the
    // parameter list with defaults once AreaEffectCount — the stat that used to drive this for most callers —
    // was removed entirely) — every caller except ArcherArrowRainAbilityPD just omits them now. ArrowRain is
    // the one remaining genuine use: its own 20-tick DoT needs a fresh, fully independent tick-tracking
    // instance per cast (so an overlapping recast can't corrupt an already-ticking one — see
    // [[project_areaeffectpd_multicast_still_needed]]), which is exactly what spawning one of these per
    // trigger already gives for free; it passes totalCasts/retriggerRate explicitly (as named args, since
    // they're no longer in their old leading positional slots).
    //
    // totalCasts is the FULL count including the immediate cast fired from here (e.g. totalCasts=3 means
    // this cast now, then 2 more retriggers). Pooled — Launch() runs on every reuse, unlike LateAwake()
    // (not guaranteed to re-fire on pooled reuse), so _onRecast's one-time construction lives here instead,
    // guarded by the null check so it only actually happens once per real instance.
    // vfxPerCast: false (default) spawns the radius VFX exactly once for the whole chain regardless of
    // totalCasts — correct for a caller whose totalCasts represents rapid inner ticks of one ongoing effect
    // (ArcherArrowRainAbilityPD's 20-tick DoT: one visible zone, not 20 flashes). true spawns it fresh on
    // EVERY cast including the first — every other caller here still passes this explicitly even though
    // totalCasts is always 1 for them now, since it's harmless (identical single-spawn result either way at
    // totalCasts == 1) and matches how each was already tuned/tested.
    public void Launch(CharacterStatsPD owner, Vector3 position, float radius, GameObject radiusVFXPrefab,
        bool targetAllies = false, bool dealDamage = true, float damagePercent = 0f, float onHitPowerPercent = 0f,
        bool notifyOnHit = true,
        bool hasKnockback = false, float? knockback = null, bool interrupt = false,
        Action<IEnemyPD> onEnemyHit = null, Action<IFriendlyPD> onAllyHit = null, Action<int> onCastComplete = null,
        Action<IEnemyPD> onSuccessfulInterrupt = null,
        bool overrideHitSfx = false, AudioClip hitSfxOverride = null, bool vfxPerCast = false,
        int totalCasts = 1, float retriggerRate = 0f,
        bool? forcedCrit = null, bool? forcedHighroll = null, Color? radiusVfxTintColor = null,
        float? finalCastDamagePercentOverride = null, float? finalRetriggerDelayOverride = null,
        float? radiusVfxAbilityDuration = null, float? radiusVfxGrowthPerSecond = null)
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
        _radiusVfxTintColor = radiusVfxTintColor;
        _radiusVfxAbilityDuration = radiusVfxAbilityDuration;
        _radiusVfxGrowthPerSecond = radiusVfxGrowthPerSecond;
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
        _forcedCrit = forcedCrit;
        _forcedHighroll = forcedHighroll;
        _finalCastDamagePercentOverride = finalCastDamagePercentOverride;
        _finalRetriggerDelayOverride = finalRetriggerDelayOverride;

        currentState.remainingCasts = Mathf.Max(1, totalCasts) - 1;
        currentState.retriggerTimer = retriggerRate;

        // vfxPerCast: false — spawned exactly once for the whole chain, here, NOT inside Cast() itself,
        // which re-runs on every retrigger (see AreaEffectCastHelper.SpawnRadiusVFX's own comment for why
        // that used to double-spawn it once per retrigger instead). vfxPerCast: true — Cast() spawns it
        // fresh every time instead (see this method's own doc comment), including this first call, so
        // skipped here to avoid a double-spawn on cast #1.
        if (!_vfxPerCast)
            AreaEffectCastHelper.SpawnRadiusVFX(hierarchy, _radiusVFXPrefab, _castPosition, _radius, _radiusVfxTintColor, _radiusVfxAbilityDuration, _radiusVfxGrowthPerSecond);

        Cast();
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (state.finished) return;
        if (state.remainingCasts <= 0) { state.finished = true; return; }
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        // Owner death no longer ends the chain (2026-09-27) — once cast, a zone like Arrow Rain's is an
        // already-planted hazard that should keep dealing damage on its own, independent of whether its
        // caster is still alive. _owner staying non-null and readable even after death is safe either way
        // (CharacterStatsPD.IsDead is just a state flag — death never destroys the player GameObject), so the
        // null check here is purely a genuine-destruction/despawn safety net, not a stand-in for IsDead.
        if (_owner == null) { state.remainingCasts = 0; return; }

        state.retriggerTimer -= delta;
        if (state.retriggerTimer > 0f) return;

        state.remainingCasts--;
        _onRecast.Invoke();

        if (state.remainingCasts > 0)
        {
            // remainingCasts == 1 here means the cast we're now arming a delay FOR is the final one in the
            // sequence — see _finalRetriggerDelayOverride's own doc comment for why that transition
            // specifically can use a different delay than every other one.
            state.retriggerTimer = (state.remainingCasts == 1 && _finalRetriggerDelayOverride.HasValue)
                ? _finalRetriggerDelayOverride.Value
                : _retriggerRate;
        }
    }

    private void Cast()
    {
        // See Launch()'s own doc comment — fires fresh on every cast (this one included) when vfxPerCast is
        // true, instead of the single Launch()-time-only spawn used otherwise.
        if (_vfxPerCast)
            AreaEffectCastHelper.SpawnRadiusVFX(hierarchy, _radiusVFXPrefab, _castPosition, _radius, _radiusVfxTintColor, _radiusVfxAbilityDuration, _radiusVfxGrowthPerSecond);

        // currentState.remainingCasts == 0 means THIS is the last cast of the sequence (Simulate() already
        // decremented it before invoking _onRecast for a retrigger; for a single-cast totalCasts==1 chain
        // it's already 0 the one time Cast() ever runs, from Launch() itself) — safe to read directly here
        // since Cast() only ever runs synchronously within the same tick that owns this state, same
        // reasoning KnightBuffStunAbilityPD.OnEnemyInterrupted's own identical currentState read uses.
        float damagePercent = (_finalCastDamagePercentOverride.HasValue && currentState.remainingCasts <= 0)
            ? _finalCastDamagePercentOverride.Value
            : _damagePercent;

        AreaEffectCastHelper.Cast(hierarchy, _owner, _castPosition, _radius, _radiusVFXPrefab,
            targetAllies: _targetAllies, dealDamage: _dealDamage, damagePercent: damagePercent, onHitPowerPercent: _onHitPowerPercent,
            notifyOnHit: _notifyOnHit,
            hasKnockback: _hasKnockback, knockback: _knockback, interrupt: _interrupt,
            onEnemyHit: _onEnemyHit, onAllyHit: _onAllyHit, onCastComplete: _onCastComplete,
            onSuccessfulInterrupt: _onSuccessfulInterrupt,
            overrideHitSfx: _overrideHitSfx, hitSfxOverride: _hitSfxOverride,
            forcedCrit: _forcedCrit, forcedHighroll: _forcedHighroll);
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
