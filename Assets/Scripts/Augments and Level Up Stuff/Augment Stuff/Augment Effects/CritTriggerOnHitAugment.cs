using PurrNet.Prediction;
using UnityEngine;

// Not a port. Two effects:
// 1. On crit, make that hit count as landing twice for onhit purposes, at 50% OnHitPower on the second
//    trigger. The weapon's own hit resolution already fires CharacterStatsPD's OnHit event once for every
//    hit (crit or not, at full OnHitPower) — this augment subscribes to OnCrit and, when it fires, calls
//    Stats.NotifyHit again with the same hit data but onHitPower scaled down, giving a crit two OnHit
//    firings total instead of one: one at full power, one at half.
// 2. +10% crit chance — same AddCritChanceFlat convention as PhantomBladeOnCritAugment.
//
// Used to also deal +5% weapon damage as its own onhit-triggered bonus damage — removed per user's call,
// no longer part of this augment.
public class CritTriggerOnHitAugment : AugmentEffectPD<CritTriggerOnHitAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }
    
    [SerializeField] private float secondHitOnHitPowerPercent = 0.3f;

    protected override void OnApply()
    {
        Stats.AddCritStrikeListener(OnCritStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveCritStrikeListener(OnCritStrike);
    }

    private void OnCritStrike(WeaponHitData data)
    {
        data.onHitPower *= secondHitOnHitPowerPercent;
        Stats.NotifyHit(data);
    }
}
