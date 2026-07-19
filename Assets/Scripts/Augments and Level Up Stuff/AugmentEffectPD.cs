using PurrNet.Prediction;

// Generic base for augment effect prefabs — same generic-base-class pattern as
// EnemyBasePD<TState>/WeaponBasePD<TState>. Must be spawned via PredictedHierarchy.Create() (never
// plain Instantiate()) from Simulate()-context (see AugmentManagerPD.SubmitPick()) — Create() is
// deterministic and idempotent under replay (resolves to the same physical instance if that tick ever
// gets re-simulated), which a raw Instantiate() call is not. Spawned with the picking player as owner,
// so `owner`/Stats resolve automatically.
//
// Ongoing/periodic effects can safely track their own timer in TState via Simulate() (same as
// CharacterStatsPD's health/shield regen timers) — but the actual one-shot side effect (a flat stat
// grant, etc.) must fire off verified state, never directly from Simulate(): a discrete one-time += is
// not safe to call from Simulate(), since it risks double-firing during the client's routine in-place
// reconciliation pass (same reasoning as OnLevelUp()/AwardXP).
public abstract class AugmentEffectPD<TState> : PredictedIdentity<TState> where TState : struct, IPredictedData<TState>
{
    protected CharacterStatsPD Stats { get; private set; }

    protected override void LateAwake()
    {
        if (owner.HasValue && CharacterStatsPD.All.TryGetValue(owner.Value, out var stats))
            Stats = stats;
    }

    private bool _applied;

    protected override void UpdateView(TState viewState, TState? verified)
    {
        if (Stats == null || !verified.HasValue || _applied) return;
        _applied = true;
        OnApply();
        // Every augment effect reports here once its own OnApply() has actually run — see
        // CharacterStatsPD.NotifyAugmentApplied's own doc comment for what this gates (class passives).
        Stats.NotifyAugmentApplied();
        // Also reports to the manager, which uses this for the analogous level-up-pick gate — see
        // AugmentManagerPD.NotifyAugmentEffectApplied's own doc comment.
        if (AugmentManagerPD.instance != null) AugmentManagerPD.instance.NotifyAugmentEffectApplied();
    }

    // Called exactly once, off verified state — override for one-shot grants (flat stat bonuses, etc.).
    protected virtual void OnApply() { }
}
