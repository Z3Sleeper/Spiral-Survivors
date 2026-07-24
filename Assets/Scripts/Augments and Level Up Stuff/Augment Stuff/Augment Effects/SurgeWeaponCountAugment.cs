using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — this AugmentData can be offered as a normal pickable augment AND/OR dropped
// directly into Rogue's ClassPassiveData.passiveAugments (AugmentManagerPD registers it as a real pick
// either way — see that field's own doc comment). While Surging, doubles weapon count (via
// MultiplyWeaponCountMultiplier/DivideWeaponCountMultiplier — bonusWeaponCountMultiplier's own multiplicative
// wrapper, not AddWeaponCountFlat, since this is a temporary x2 rather than a permanent +N) and grants
// +30% damage — applied on Surge start, then the exact inverse on Surge end, rather than introducing
// dedicated surge-only stat fields. The extra weapons' damage cost is already covered by Strength's own
// auto-divide-by-WeaponCount, same as BladeweaviestBladeweaverAugment/MultiShotAugment — the +30% is the
// compensating bonus on top of that, not in addition to a separate penalty.
public class SurgeWeaponCountAugment : AugmentEffectPD<SurgeWeaponCountAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int   bonusWeaponCountMultiplier = 2;
    [SerializeField] private float totalStrengthPercent       = 0.3f;
    // Separate tunable, not a reuse of totalStrengthPercent — OnHitPower auto-divides by WeaponCount the same way
    // Strength does, so it needs its own compensating bonus, but kept independently adjustable in case
    // onhit-triggered effects need different tuning than raw weapon damage.
    [SerializeField] private float totalOnHitPowerPercent = 0.3f;

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
        Stats.AddSurgeEndedListener(OnSurgeEnded);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null)
        {
            Stats.RemoveSurgeStartedListener(OnSurgeStarted);
            Stats.RemoveSurgeEndedListener(OnSurgeEnded);
        }
    }

    private void OnSurgeStarted()
    {
        Stats.MultiplyWeaponCountMultiplier(bonusWeaponCountMultiplier);
        Stats.AddStrengthTotalPercent(totalStrengthPercent);
        // Same additive-into-1f Total-tier pattern as AddStrengthTotalPercent — exactly inverted on Surge
        // end below, same as the Strength grant.
        Stats.AddOnHitPowerTotalPercent(totalOnHitPowerPercent);
    }

    private void OnSurgeEnded()
    {
        Stats.DivideWeaponCountMultiplier(bonusWeaponCountMultiplier);
        Stats.AddStrengthTotalPercent(-totalStrengthPercent);
        Stats.AddOnHitPowerTotalPercent(-totalOnHitPowerPercent);
    }
}
