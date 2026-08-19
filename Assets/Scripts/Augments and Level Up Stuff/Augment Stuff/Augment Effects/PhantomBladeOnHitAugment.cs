using PurrNet.Prediction;
using UnityEngine;

// Not a port — turns on PhantomBladePD's OnHit firing (off by default, see
// CharacterStatsPD.phantomBladeOnHitEnabled) at 50% OnHitPower, so Phantom Blades themselves become a
// trigger source for onhit effects, not just weapon strikes. Also grants its own +10% Phantom Blade damage
// AND +10% Phantom Blade size (same scaffold every Phantom Blade augment contributes to, 1:1).
//
// Used to also deal +5% weapon damage as its own onhit-triggered bonus damage — removed per user's call,
// no longer part of this augment.
public class PhantomBladeOnHitAugment : AugmentEffectPD<PhantomBladeOnHitAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float phantomBladeDamageBonus       = 0.1f;
    public float phantomBladeOnHitPowerPercent = 0.3f;

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        // Same additive amount as the damage bonus above — every Phantom Blade augment granting "+X%
        // Phantom Blade Damage" grants the identical "+X% Phantom Blade Size" too (1:1, not a derived
        // fraction). Only PhantomBladeSizeAugment itself touches the separate multiplicative Percent tier.
        Stats.AddPhantomBladeSizeBonus(phantomBladeDamageBonus);
        Stats.EnablePhantomBladeOnHit();
        Stats.AddPhantomBladeOnHitPowerPercent(phantomBladeOnHitPowerPercent);
    }
}
