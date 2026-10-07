using PurrNet.Prediction;
using UnityEngine;

// Same family shape as PhantomBladeOnCritAugment (see its own doc comment for the full architecture
// reasoning) — summons at the closest enemies for unpenalized Strength damage, grants the usual +Phantom
// Blade Damage/Size pair. Triggers directly off CharacterStatsPD._onDodge (a plain payload-free
// PredictedEvent — see OnDodgeHealHealthRegenAugment's own doc comment for the exact "first dodge" == "a
// dodge" contract) — no timer, no cooldown, no stacking; every real dodge summons immediately, full stop.
public class PhantomBladeOnDodgeAugment : AugmentEffectPD<PhantomBladeOnDodgeAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private GameObject phantomBladePrefab;
    // "New authored" baseline (2026-09-26) — every summoning Phantom Blade augment grants this now, down
    // from the older 10% still seen as the stale script default on some sibling files.
    [SerializeField] private float phantomBladeDamageBonus = 0.05f;

    public int bladeCount = 9;
    public float strengthMultiplierPercent = 1f;

    // For AugmentDescriptionFormatter's {LiveBladeDamageRatio} token. See
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
        Stats.AddDodgeListener(OnDodge);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDodgeListener(OnDodge);
    }

    private void OnDodge()
    {
        if (Stats == null || Stats.IsDead) return;
        if (phantomBladePrefab == null) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, bladeCount, strengthMultiplierPercent);
    }
}
