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

    // 0.1f = 10% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    [SerializeField] private float onHitHealPercent = 0.1f;

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
        // Anything that deals/restores an amount rolls luck variance — no exceptions. Heal() itself rolls it
        // now, so this just passes the raw amount through.
        Stats.Heal(1f + (Stats.HealthRegen * onHitHealPercent * data.onHitPower));
    }
}
