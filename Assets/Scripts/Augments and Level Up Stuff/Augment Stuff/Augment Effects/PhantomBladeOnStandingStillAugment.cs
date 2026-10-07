using PurrNet.Prediction;
using UnityEngine;

// Same family shape as PhantomBladeOnCritAugment/PhantomBladeOnMovementAugment (see PhantomBladeOnCritAugment's
// own doc comment for the full architecture reasoning) — summons at the closest enemies for unpenalized
// Strength damage, grants the usual +Phantom Blade Damage/Size pair. Triggers every currentCooldownDuration
// seconds while genuinely standing still (world position hasn't moved more than stillnessThreshold since last
// tick — same LateSimulate() position-delta detection PhantomBladeOnMovementAugment already uses, just
// inverted: this WANTS near-zero movement, not distance traveled).
//
// Summon count/strength are FIXED (bladeCount/strengthMultiplierPercent, explicit design — "will NOT scale
// with stack power") — only the COOLDOWN shrinks: every consecutive trigger while still standing still
// reduces currentCooldownDuration by cooldownReductionPerTrigger, down to a minCooldownDuration floor, so
// staying still longer summons faster and faster. currentCooldownDuration doubles as its own "stack counter"
// here — no separate int needed, since duration is the only thing the streak actually needs to affect. The
// instant real movement is detected, BOTH the timer AND currentCooldownDuration reset back to
// baseCooldownDuration — moving even a little fully resets the streak, it doesn't just pause it.
//
// Tracked in LateSimulate(), not Simulate() — same reasoning PhantomBladeOnMovementAugment's own prevPos
// tracking gives: physics (DoPhysicsPass) resolves the player's actual movement for this tick AFTER
// Simulate() runs, so reading transform.position in Simulate() would still see last tick's position, one
// tick stale. prevPos/initialized/currentCooldownDuration/timer are all real predicted State (not plain
// fields) since none of this can be driven off a discrete PredictedEvent — same "no event exists for this,
// must read transform.position directly every tick, so it has to be State to roll back correctly under
// resimulation" reasoning PhantomBladeOnMovementAugment's own doc comment gives.
public class PhantomBladeOnStandingStillAugment : AugmentEffectPD<PhantomBladeOnStandingStillAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float timer;
        // Shrinks by cooldownReductionPerTrigger every consecutive trigger, floored at minCooldownDuration —
        // this IS the "stack counter," expressed directly as the duration itself rather than a separate int,
        // since the duration is the only thing it actually needs to affect (see class doc comment).
        public float currentCooldownDuration;
        public Vector3 prevPos;
        // First-tick guard — prevPos defaults to Vector3.zero, which would otherwise register as one huge
        // (and entirely fake) jump from the world origin the instant this augment is picked.
        public bool initialized;
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    // "New authored" baseline (2026-09-26) — every summoning Phantom Blade augment grants this now, down
    // from the older 10% still seen as the stale script default on some sibling files.
    [SerializeField] private float phantomBladeDamageBonus = 0.05f;

    public float baseCooldownDuration = 2f;
    public float cooldownReductionPerTrigger = 0.5f;
    public float minCooldownDuration = 0.5f;
    // Below this much world-distance moved in one tick counts as "still" rather than "moved" — real physics/
    // floating-point noise means two consecutive positions are essentially never bit-for-bit identical even
    // while genuinely stationary.
    [SerializeField] private float stillnessThreshold = 0.01f;

    public int bladeCount = 3;
    public float strengthMultiplierPercent = 1f;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, strengthMultiplierPercent))} "
            : "";

    protected override State GetInitialState() => new() { timer = baseCooldownDuration, currentCooldownDuration = baseCooldownDuration };

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction). Only PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
    }

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

        if (traveled > stillnessThreshold)
        {
            // Moved — full reset, not a pause. See class doc comment.
            state.timer = baseCooldownDuration;
            state.currentCooldownDuration = baseCooldownDuration;
            return;
        }

        state.timer -= delta;
        if (state.timer > 0f) return;

        // Shrink BEFORE reassigning timer, so the just-fired trigger's own (still-unshrunk) duration isn't
        // what gets reused for the next cycle — the next cycle should already reflect this trigger's own
        // reduction (2.0 -> summon -> next timer 1.5 -> summon -> next timer 1.0 -> ... -> floor 0.5).
        state.currentCooldownDuration = Mathf.Max(minCooldownDuration, state.currentCooldownDuration - cooldownReductionPerTrigger);
        state.timer = state.currentCooldownDuration;

        if (phantomBladePrefab == null) return;
        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, strengthMultiplierPercent);
    }
}
