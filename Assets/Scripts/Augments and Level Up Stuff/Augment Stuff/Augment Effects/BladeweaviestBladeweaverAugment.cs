using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/BladeweaviestBladeweaverAugment.cs. Pure one-shot stat
// grant — defaults per user's call: -40% damage / -30% on-hit power (old used 45%/25%), not the old
// system's exact numbers.
public class BladeweaviestBladeweaverAugment : AugmentEffectPD<BladeweaviestBladeweaverAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private int   bonusWeapons      = 3;
    [SerializeField] private float damagePenalty     = 0.4f;
    [SerializeField] private float onHitPowerPenalty = 0.3f;

    protected override void OnApply()
    {
        Stats.AddWeaponCountFlat(bonusWeapons);
        Stats.MultiplyWeaponDamagePenalty(1f - damagePenalty);
        Stats.MultiplyOnHitPowerPenalty(1f - onHitPowerPenalty);
    }
}
