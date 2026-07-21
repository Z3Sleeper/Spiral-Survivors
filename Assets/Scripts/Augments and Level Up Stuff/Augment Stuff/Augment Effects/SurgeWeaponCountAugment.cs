using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — this AugmentData can be offered as a normal pickable augment AND/OR dropped
// directly into Rogue's ClassPassiveData.passiveAugments (AugmentManagerPD registers it as a real pick
// either way — see that field's own doc comment). While Surging, doubles weapon count (via
// MultiplyWeaponCountMultiplier/DivideWeaponCountMultiplier — bonusWeaponCountMultiplier's own multiplicative
// wrapper, not AddWeaponCountFlat, since this is a temporary x2 rather than a permanent +N) and grants
// +30% damage — applied on Surge start, then the exact inverse on Surge end, rather than introducing
// dedicated surge-only stat fields. The extra weapons' damage cost is already covered by WeaponDamage's own
// auto-divide-by-WeaponCount, same as BladeweaviestBladeweaverAugment/MultiShotAugment — the +30% is the
// compensating bonus on top of that, not in addition to a separate penalty.
public class SurgeWeaponCountAugment : AugmentEffectPD<SurgeWeaponCountAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int   weaponCountMultiplier = 2;
    [SerializeField] private float damageBonus     = 0.3f;
    // Separate tunable, not a reuse of damageBonus — OnHitPower auto-divides by WeaponCount the same way
    // WeaponDamage does, so it needs its own compensating bonus, but kept independently adjustable in case
    // onhit-triggered effects need different tuning than raw weapon damage.
    [SerializeField] private float onHitPowerBonus = 0.3f;

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
        Stats.MultiplyWeaponCountMultiplier(weaponCountMultiplier);
        Stats.AddWeaponDamagePercent(damageBonus);
        // MultiplyOnHitPowerMultiplier, not AddWeaponDamagePercent's additive-into-1f pattern —
        // bonusOnHitPowerMultiplier is a straight multiplicative stack, exactly inverted on Surge end below.
        Stats.MultiplyOnHitPowerMultiplier(1f + onHitPowerBonus);
    }

    private void OnSurgeEnded()
    {
        Stats.DivideWeaponCountMultiplier(weaponCountMultiplier);
        Stats.AddWeaponDamagePercent(-damageBonus);
        Stats.MultiplyOnHitPowerMultiplier(1f / (1f + onHitPowerBonus));
    }
}
