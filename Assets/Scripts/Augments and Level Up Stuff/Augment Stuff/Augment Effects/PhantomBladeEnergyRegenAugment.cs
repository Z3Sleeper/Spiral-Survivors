using PurrNet.Prediction;
using UnityEngine;

// Same family shape as PhantomBladeOnCritAugment (see its own doc comment for the full architecture
// reasoning) — summons at the closest enemies for unpenalized Strength damage, grants the usual +Phantom
// Blade Damage/Size pair. Triggered off CharacterStatsPD._onEnergySpent (currently only Surge's own cost)
// instead of crits, accumulated toward a flat energyNeeded threshold rather than a per-hit count — no
// WeaponCount-divide needed here (unlike the crit version) since energy spend has no relationship to weapon
// count at all. Same "plain field, mutated only inside the PredictedEvent callback" safety as
// PhantomBladeOnCritAugment's own _critCount — _energyAccumulator only ever changes from inside
// OnEnergySpent, which PredictedEvent guarantees fires exactly once per real occurrence on every machine.
public class PhantomBladeEnergyRegenAugment : AugmentEffectPD<PhantomBladeEnergyRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;
    public float energyNeeded = 50f;
    public int bladeCount = 3;
    public float baseDamageMultiplier = 0.6f;

    // baseDamageMultiplier itself doesn't end in "Percent", so AugmentDescriptionFormatter's *100
    // convention wouldn't apply to it directly — this alias exists purely so a description can reference
    // {DamageMultiplierPercent} and get the correctly-scaled static number, as a fallback for whenever
    // {LiveBladeDamageRatio} itself is blank (out of a live match — see ShowLiveValues's own doc comment).
    public float DamageMultiplierPercent => baseDamageMultiplier;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, baseDamageMultiplier))} "
            : "";

    private float _energyAccumulator;

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction). Only PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
        Stats.AddEnergySpentListener(OnEnergySpent);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveEnergySpentListener(OnEnergySpent);
    }

    private void OnEnergySpent(EnergySpentData data)
    {
        _energyAccumulator += data.amount;
        if (_energyAccumulator < energyNeeded) return;
        _energyAccumulator -= energyNeeded;

        if (phantomBladePrefab == null) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, baseDamageMultiplier);
    }
}
