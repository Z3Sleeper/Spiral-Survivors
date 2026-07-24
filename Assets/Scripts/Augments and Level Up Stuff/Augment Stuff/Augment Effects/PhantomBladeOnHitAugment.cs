using PurrNet.Prediction;
using UnityEngine;

// Not a port — turns on PhantomBladePD's OnHit firing (off by default, see
// CharacterStatsPD.phantomBladeOnHitEnabled) at 50% OnHitPower, so Phantom Blades themselves become a
// trigger source for onhit effects, not just weapon strikes. Also grants its own +10% Phantom Blade damage
// (same scaffold every Phantom Blade augment contributes to).
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
    [SerializeField] private float phantomBladeOnHitPowerPercent = 0.5f;

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        Stats.EnablePhantomBladeOnHit();
        Stats.AddPhantomBladeOnHitPowerPercent(phantomBladeOnHitPowerPercent);
    }
}
