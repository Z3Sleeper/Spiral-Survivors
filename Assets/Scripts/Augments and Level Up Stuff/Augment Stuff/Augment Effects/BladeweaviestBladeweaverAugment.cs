using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/BladeweaviestBladeweaverAugment.cs. Used to hand-tune a
// damage/on-hit-power PENALTY to offset the extra weapons this grants — now that Strength/OnHitPower
// both auto-divide by WeaponCount themselves, that offset is structural, so this instead grants a straight
// BONUS on top (default +20% each).
public class BladeweaviestBladeweaverAugment : AugmentEffectPD<BladeweaviestBladeweaverAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private int   bonusWeaponCountFlat       = 3;
    [SerializeField] private float totalStrengthPercent       = 0.2f;
    [SerializeField] private float onHitPowerTotalPercent     = 0.2f;

    protected override void OnApply()
    {
        Stats.AddWeaponCountFlat(bonusWeaponCountFlat);
        Stats.AddStrengthTotalPercent(totalStrengthPercent);
        Stats.AddOnHitPowerTotalPercent(onHitPowerTotalPercent);
    }
}
