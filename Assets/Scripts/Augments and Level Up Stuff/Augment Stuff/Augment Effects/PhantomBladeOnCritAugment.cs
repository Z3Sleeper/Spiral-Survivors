using PurrNet.Prediction;
using UnityEngine;

// Reworked (2026-08-19) from an on-real-crit-strike trigger (subscribed to CharacterStatsPD's own
// CritStrike event, counting toward a critsNeeded threshold) to an independent periodic roll — same
// "timer lives in predicted State, ticks in Simulate()" shape PhantomBladePassiveAugment already uses, but
// WITHOUT that augment's own AttackSpeed-scaled acceleration: baseTimer (1s) always ticks at plain real
// time, deliberately not sped up by attack speed. Every time the timer expires, rolls Stats.CritChance —
// same GameRandom.instance.NextFloat() < CritChance check every weapon's own crit roll uses — and only
// summons if that roll succeeds. No longer reacts to actual weapon crits at all, so this is now completely
// decoupled from weapon count/attack speed/Multishot scaling on the TRIGGER side (only the summon's own
// damage formula reads Stats.Strength, same as every other Phantom Blade augment).
public class PhantomBladeOnCritAugment : AugmentEffectPD<PhantomBladeOnCritAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public float timer;
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;
    [SerializeField] private float baseTimer = 1f;
    public int bladeCount = 3;
    public float baseDamageMultiplier = 0.6f;

    // baseDamageMultiplier itself doesn't end in "Percent", so AugmentDescriptionFormatter's *100
    // convention wouldn't apply to it directly — this alias exists purely so a description can reference
    // {BaseDamageMultiplierPercent} and get the correctly-scaled static number, as a fallback for whenever
    // {LiveBladeDamageRatio} itself is blank (out of a live match — see ShowLiveValues's own doc comment).
    public float BaseDamageMultiplierPercent => baseDamageMultiplier;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, baseDamageMultiplier))} "
            : "";

    protected override State GetInitialState() => new() { timer = baseTimer };

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction — this used to be half the damage bonus, bumped to match it exactly). Only
        // PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;

        // Plain delta, no AttackSpeed scaling — see class doc comment for why this deliberately doesn't
        // mirror PhantomBladePassiveAugment's own accelerated timer.
        state.timer -= delta;
        if (state.timer > 0f) return;

        state.timer = baseTimer;

        if (GameRandom.instance.NextFloat() >= Stats.CritChance) return;
        if (phantomBladePrefab == null) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, baseDamageMultiplier);
    }
}
