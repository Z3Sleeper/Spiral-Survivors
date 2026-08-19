using PurrNet.Prediction;
using UnityEngine;

// Reworked (2026-08-19) from an on-real-strike-highroll trigger (subscribed to CharacterStatsPD's own
// StrikeHighroll event) to an independent periodic roll — same shape as PhantomBladeOnCritAugment's own
// identical rework, see that file's own doc comment for the full "timer in State, ticks in Simulate(),
// deliberately no AttackSpeed acceleration" reasoning. Every time the timer expires, rolls Luck via
// WeaponDamageMath.RollLuckMultiplier (same helper OnDashStartedRollLuckForReducedCDAugment already uses
// for an identical "just tell me if this roll highrolled" check) and only summons if that roll actually
// highrolled — not just any positive Luck roll, the same rarer Highroll-specific threshold a real weapon
// hit's own isHighroll represents. No longer reacts to actual weapon hits at all. bladeCount (3, up from 1)
// and baseDamageMultiplier (6f/600%, up from 0.6f/60% — 10x PhantomBladeOnCritAugment's own per-blade
// damage) both bumped to match — Highroll is the rarer of the two rolls, so its payout is tuned
// proportionally bigger per explicit design.
public class PhantomBladeOnHighrollAugment : AugmentEffectPD<PhantomBladeOnHighrollAugment.State>
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
    public float baseDamageMultiplier = 6f;

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
        // fraction). Only PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
    }

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (Stats == null || Stats.IsDead) return;

        // Plain delta, no AttackSpeed scaling — see class doc comment.
        state.timer -= delta;
        if (state.timer > 0f) return;

        state.timer = baseTimer;

        // highrollBonus passed as 0f — only used here to decide WHETHER a highroll happened, same
        // "discard the multiplier, keep isHighroll" convention OnDashStartedRollLuckForReducedCDAugment
        // already uses for its own identical roll.
        WeaponDamageMath.RollLuckMultiplier(Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, 0f, GameRandom.instance.NextFloat, out bool isHighroll);
        if (!isHighroll) return;
        if (phantomBladePrefab == null) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, baseDamageMultiplier);
    }
}
