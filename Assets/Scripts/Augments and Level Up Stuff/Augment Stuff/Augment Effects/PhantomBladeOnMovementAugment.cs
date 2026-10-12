using PurrNet.Prediction;
using UnityEngine;

// Same family shape as PhantomBladeOnCritAugment (see its own doc comment for the full architecture
// reasoning) — summons at the closest enemies for unpenalized Strength damage, grants the usual +Phantom
// Blade Damage/Size pair. Triggered off actual world distance traveled (normal move-speed movement,
// knockback, grabbed/pulled, etc. all count — this just watches the transform, same as
// DashThroughInterruptAugment's own sweep), accumulated toward a flat distanceNeeded threshold. Dash/special
// movement is EXCLUDED on purpose (2026-08-21, explicit design — this should only reward ordinary movement,
// not a free ride off however far a dash/leap/teleport happens to cover) — gated off the same
// PlayerControllerPD DashMovementStarted/DashMovementEnded pair DashThroughInterruptAugment reacts to
// (see its own doc comment for exactly which sources open that window: a real dash, RogueSpinAbilityPD's
// leap, BWBlinkAbilityPD's teleport), just inverted: accumulation is SKIPPED while isDashing is true instead
// of a sweep being ACTIVE while it's true.
//
// Unlike PhantomBladeOnCritAugment/PhantomBladeOnHealAugment, this can't use a
// plain (non-State) accumulator field — there's no discrete PredictedEvent firing "you moved," so this reads
// Stats.transform.position directly every tick, which means both distanceAccumulator and prevPos have to be
// real predicted State to roll back correctly under resimulation (a plain field would double/triple-count on
// every resimulated tick instead of just once per real tick). Tracked in LateSimulate(), not Simulate() — same
// reasoning as DashThroughInterruptAugment's own sweepPrevPos: physics (DoPhysicsPass) resolves the player's
// actual movement for this tick AFTER Simulate() runs, so reading transform.position in Simulate() would
// still see last tick's position, one tick stale.
//
// prevPos itself still updates EVERY tick regardless of isDashing (only the accumulator increment is
// gated) — so the dash's own (excluded) distance never gets silently double-counted in one lump the instant
// DashMovementEnded closes the window; it's simply dropped, exactly as intended.
public class PhantomBladeOnMovementAugment : AugmentEffectPD<PhantomBladeOnMovementAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float distanceAccumulator;
        public Vector3 prevPos;
        // First-tick guard — prevPos defaults to Vector3.zero, which would otherwise register as one huge
        // (and entirely fake) jump from the world origin the instant this augment is picked.
        public bool initialized;
        // True between DashMovementStarted and DashMovementEnded — see class doc comment. Starts false even
        // if this augment happens to be picked mid-dash (matches DashThroughInterruptAugment's own isSweeping
        // — same accepted edge case, not specifically handled).
        public bool isDashing;
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;
    public float distanceNeeded = 50f;
    public int bladeCount = 3;
    public float strengthMultiplierPercent = 0.6f;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, strengthMultiplierPercent))} "
            : "";

    private PlayerControllerPD _controller;

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction). Only PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);

        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null)
        {
            _controller.AddDashMovementStartedListener(OnMovementStarted);
            _controller.AddDashMovementEndedListener(OnMovementEnded);
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null)
        {
            _controller.RemoveDashMovementStartedListener(OnMovementStarted);
            _controller.RemoveDashMovementEndedListener(OnMovementEnded);
        }
    }

    private void OnMovementStarted() => currentState.isDashing = true;
    // interrupted (2026-10-10, PlayerControllerPD._onDashMovementEnded payload addition) — unused here,
    // this only cares that the window closed, not why, same as before that payload existed.
    private void OnMovementEnded(bool interrupted) => currentState.isDashing = false;

    protected override void LateSimulate(ref State state, float delta)
    {
        if (Stats == null || Stats.IsDead) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        Vector3 currentPos = Stats.transform.position;
        if (!state.initialized)
        {
            state.prevPos = currentPos;
            state.initialized = true;
            return;
        }

        float traveled = Vector3.Distance(state.prevPos, currentPos);
        state.prevPos = currentPos;
        if (!state.isDashing) state.distanceAccumulator += traveled;

        // While, not if (2026-08-21) — low risk in practice (ordinary per-tick movement rarely covers more
        // than one distanceNeeded's worth in a single tick), but a single tick's traveled distance (a big
        // knockback, etc.) COULD clear it more than once; an `if` would only summon once and strand the rest
        // of that overshoot until the next tick's movement happened to push it over again, instead of
        // correctly summoning immediately for every threshold this tick actually crossed. Same guard as
        // PhantomBladeOnHealAugment.Accumulate's own identical fix — see its doc comment.
        while (state.distanceAccumulator >= distanceNeeded)
        {
            state.distanceAccumulator -= distanceNeeded;
            if (phantomBladePrefab == null) continue;

            PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
                Stats.transform.position, bladeCount, strengthMultiplierPercent);
        }
    }
}
