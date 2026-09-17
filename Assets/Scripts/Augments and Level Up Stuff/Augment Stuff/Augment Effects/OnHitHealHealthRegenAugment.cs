using PurrNet.Prediction;
using UnityEngine;

// Heals self (not damage) on every OnHit firing — same trigger chain as OnHitDamageAugment/
// OnHitDamageHealthRegenAugment, so CritTriggerOnHitAugment's "crit triggers another onhit" mechanic also
// triggers an additional heal here, same as it does an additional hit for those. No flat HealthRegen grant,
// unlike its damage-dealing sibling OnHitDamageHealthRegenAugment. Doesn't scale with data.isCrit — matches
// OnStrikeHealAllyAugment's own precedent (a heal triggered off a hit event doesn't crit-scale), unlike the
// damage-dealing OnHit augments which do inherit isCrit.
public class OnHitHealHealthRegenAugment : AugmentEffectPD<OnHitHealHealthRegenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.1f = 10% (this codebase's percent fields store the fraction). Scales off Stats.BonusHealthRegen
    // (2026-09-14, was Stats.HealthRegen, the full/total stat) — see that property's own doc comment. Renamed
    // from onHitHealPercent to bonusHealthRegenPercent for family-wide naming consistency with every other
    // "percent of Bonus HealthRegen" healing augment; default corrected 0.05 -> 0.1 to match the live
    // Inspector value already set on this augment's own prefab.
    public float bonusHealthRegenPercent = 0.1f;
    // Flat floor added to the scaled portion below (2026-08-12, playtesting: this augment did literally
    // nothing for a build with zero HealthRegen investment) — added BEFORE the onHitPower multiply (see
    // OnHit()), so it still scales with onHitPower same as the rest of the heal, just guarantees a non-zero
    // base even at zero HealthRegen investment.
    public float baseHeal = 0.2f;

    // Baseline heal at 100% OnHitPower (the per-hit multiplier itself isn't included since it varies
    // hit-to-hit) for AugmentDescriptionFormatter's {LiveHealthRegenRatio} token — see
    // OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the full convention. Includes
    // baseHeal so the preview matches what OnHit actually heals.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseHeal + stats.BonusHealthRegen * bonusHealthRegenPercent)} "
            : "";

    protected override void OnApply()
    {
        Stats.AddHitListener(OnHit);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveHitListener(OnHit);
    }

    private void OnHit(WeaponHitData data)
    {
        // No luck roll — heals don't roll luck at all anymore (see CharacterStatsPD.Heal's own doc comment),
        // so this just passes the raw amount through. baseHeal joins the scaled portion INSIDE the
        // onHitPower multiply — see its own field comment.
        Stats.Heal((baseHeal + Stats.BonusHealthRegen * bonusHealthRegenPercent) * data.onHitPower);
    }
}
