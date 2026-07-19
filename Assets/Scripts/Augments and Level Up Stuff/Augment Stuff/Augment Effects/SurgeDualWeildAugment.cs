using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — this AugmentData can be offered as a normal pickable augment AND/OR dropped
// directly into Rogue's ClassPassiveData.passiveAugments (AugmentManagerPD registers it as a real pick
// either way — see that field's own doc comment). While Surging,
// grants +1 weapon and +30% damage — applied directly to the normal bonusWeaponCountFlat/
// bonusWeaponDamagePercent pools (AddWeaponCountFlat/AddWeaponDamagePercent, the same wrappers any
// permanent +weapon/+damage% augment uses) on Surge start, then the exact inverse on Surge end, rather than
// introducing dedicated surge-only stat fields. The +1 weapon's damage cost is already covered by
// WeaponDamage's own auto-divide-by-WeaponCount, same as BladeweaviestBladeweaverAugment/MultiShotAugment —
// the +30% is the compensating bonus on top of that, not in addition to a separate penalty.
public class SurgeDualWeildAugment : AugmentEffectPD<SurgeDualWeildAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int   bonusWeapons = 1;
    [SerializeField] private float damageBonus  = 0.3f;

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
        Stats.AddWeaponCountFlat(bonusWeapons);
        Stats.AddWeaponDamagePercent(damageBonus);
    }

    private void OnSurgeEnded()
    {
        Stats.AddWeaponCountFlat(-bonusWeapons);
        Stats.AddWeaponDamagePercent(-damageBonus);
    }
}
