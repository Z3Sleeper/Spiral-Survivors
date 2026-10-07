using PurrNet.Prediction;
using UnityEngine;

// Not a port — the user's current intended design supersedes old's OnDamaged/OnSummon stat-bonus behavior
// entirely. Purely a trigger: when this player takes damage, summon a Phantom Blade at the closest enemy
// (not the enemy that dealt the damage — same "closest enemies" targeting convention as
// PhantomBladePassiveAugment/PhantomBladeOnDashAugment) for 100%* unpenalized damage, plus the same +10%
// Phantom Blade damage bonus every other family member grants. Subscribes to CharacterStatsPD._onDamaged
// (fires from TakeDamage on any positive damage taken, excluding a killing blow) — per-owner-instance, same
// cross-player-safety reasoning as every other Phantom Blade trigger in this family. Scales off
// DamageTakenData.preMitigationDamage deliberately, not postMitigationDamage — the retaliation should
// reflect how hard the player was actually hit, not get diluted by their own shield/DamageReduction.
public class PhantomBladeOnDamagedAugment : AugmentEffectPD<PhantomBladeOnDamagedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    public int bladeCount = 3;
    public float strengthMultiplierPercent = 0.06f;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;

    // Damage per 1 point of pre-mitigation damage taken (the trigger's own per-event input varies hit-to-
    // hit, so isn't baked in — same "baseline coefficient" convention as OnHitDamageHealthAugment.
    // LiveBonusHealthRatio's own doc comment) for AugmentDescriptionFormatter's {LiveBladeDamageRatio} token.
    public string LiveBladeDamageRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(PhantomBladeSummonHelper.PreviewDamage(stats, strengthMultiplierPercent))} "
            : "";

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction — this used to be half the damage bonus, bumped to match it exactly). Only
        // PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
        Stats.AddDamagedListener(OnDamaged);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDamagedListener(OnDamaged);
    }

    private void OnDamaged(DamageTakenData data)
    {
        if (Stats == null || Stats.IsDead) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, (strengthMultiplierPercent * data.preMitigationDamage));
    }
}
