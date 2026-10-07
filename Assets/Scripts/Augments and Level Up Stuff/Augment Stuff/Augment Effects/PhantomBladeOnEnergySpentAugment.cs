using PurrNet.Prediction;
using UnityEngine;

// Restored (2026-08-30 — previously removed, brought back per explicit request). Same shape as every other
// Phantom Blade family member (PhantomBladeOnDashAugment, PhantomBladeOnCritAugment, etc.): 3 blades, 60%
// damage, +10% permanent Phantom Blade damage/size bonus. Trigger is "every 50 Energy Spent" — accumulates
// every CharacterStatsPD._onEnergySpent event (see EnergySpentData's own doc comment — Surge's own
// activation cost AND its ongoing per-tick drain both route through this one event) since the last summon,
// and fires once per full energySpentPerSummon chunk actually crossed. A while loop, not a single if, so a
// single spend that happens to cross more than one chunk at once (not possible with today's spend sources,
// but not assumed away either) still summons once per chunk, not just once.
public class PhantomBladeOnEnergySpentAugment : AugmentEffectPD<PhantomBladeOnEnergySpentAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Running total of energy spent since the last summon — see class doc comment for the chunking
        // logic. Starts at 0 (no free first summon, unlike SurgeInterruptAoEAugment's own analogous
        // accumulator) — the player must actually spend a full chunk before the very first summon too, same
        // as every summon after it.
        public float energySpentSinceLastSummon;
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    public int bladeCount = 3;
    public float strengthMultiplierPercent = 0.6f;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;
    [SerializeField] private float energySpentPerSummon = 50f;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token — see
    // PhantomBladeSummonHelper.PreviewDamage's own doc comment for the full shared-formula convention.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, strengthMultiplierPercent))} "
            : "";

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
        if (Stats == null || Stats.IsDead) return;

        currentState.energySpentSinceLastSummon += data.amount;
        while (currentState.energySpentSinceLastSummon >= energySpentPerSummon)
        {
            currentState.energySpentSinceLastSummon -= energySpentPerSummon;
            PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
                Stats.transform.position, bladeCount, strengthMultiplierPercent);
        }
    }
}
