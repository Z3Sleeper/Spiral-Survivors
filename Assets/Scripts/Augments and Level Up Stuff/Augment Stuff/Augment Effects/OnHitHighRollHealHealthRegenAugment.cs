using PurrNet.Prediction;
using UnityEngine;

// Same _onHit trigger chain as OnHitHealHealthRegenAugment/OnHitDamageAugment (CritTriggerOnHitAugment's own
// crit-triggers-another-onhit re-invocation and PhantomBladeOnHitAugment's own per-strike NotifyHit both feed
// this the exact same way they feed every other onhit-listener augment — a crit that itself highrolls, or a
// Phantom Blade hit whose own damage roll highrolled, both correctly carry data.isHighroll = true by now, see
// WeaponHitData.isHighroll's own doc comment), but this is this codebase's first onhit effect gated ENTIRELY
// on hit TYPE rather than firing on every hit with a bonus/multiplier layered on top — OnHitDamageDistanceAugment's
// own tip-hit doubling still fires (at half value) on a non-tip hit; this fires at ALL only on data.isHighroll,
// a hard gate, not a scaling multiplier. Heals self (not damage) — baseHeal(4) plus bonusHealthRegenPercent
// (100%) of Stats.BonusHealthRegen (HealthRegen minus the character's raw baseHealthRegen — i.e. only the
// portion gained from augments/meta-progression, not innate regen), the whole sum multiplied by data.onHitPower
// (this specific hit's own already-WeaponCount-divided, already-decayed onHitPower, same convention every
// other onhit-listener effect uses). No luck roll — heals don't roll luck at all (see CharacterStatsPD.Heal's
// own doc comment) — and doesn't scale with data.isCrit, matching OnHitHealHealthRegenAugment's own precedent
// that a heal triggered off a hit event doesn't crit-scale.
public class OnHitHighRollHealHealthRegenAugment : AugmentEffectPD<OnHitHighRollHealHealthRegenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float baseHeal = 4f;
    // 1f = 100% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float bonusHealthRegenPercent = 1f;

    // Baseline heal at 100% OnHitPower (the per-hit multiplier itself isn't included since it varies
    // hit-to-hit) for AugmentDescriptionFormatter's {LiveHealthRegenRatio} token — see
    // OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the full convention. Includes
    // baseHeal so the preview matches what OnHit actually heals when it fires.
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
        // The entire gate — see class doc comment. Every non-highroll hit (the overwhelming majority) no-ops
        // here entirely, unlike a scaling multiplier that would still grant a reduced amount.
        if (!data.isHighroll) return;

        // No luck roll — heals don't roll luck at all (see CharacterStatsPD.Heal's own doc comment), so this
        // just passes the raw amount through. baseHeal joins the scaled portion INSIDE the onHitPower
        // multiply, same convention OnHitHealHealthRegenAugment's own identical shape uses.
        Stats.Heal((baseHeal + Stats.BonusHealthRegen * bonusHealthRegenPercent) * data.onHitPower);
    }
}
