using PurrNet.Prediction;
using UnityEngine;

public class TitanAugment : AugmentEffectPD<TitanAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusSizePercent = 0.2f;
    [SerializeField] private float totalWeaponRangePercent = 0.1f;
    [SerializeField] private float totalMaxHealthPercent = 0.1f;

    protected override void OnApply()
    {
        // AddSizePercent handles the transform scale (and the unscaled-children adjustment) itself,
        // additively against CharacterStatsPD's shared bonusSizePercent — so a future size augment
        // (e.g. Gargantuan) stacks its own delta on top instead of compounding multiplicatively with
        // this one. WeaponSize already reads bonusSizePercent directly, so the weapon's hitbox/visual
        // scale grows with the character without a separate call here. WeaponRadius (orbit distance)
        // deliberately doesn't scale with size — see CharacterStatsPD.WeaponRadius for why.
        Stats.AddSizePercent(bonusSizePercent);
        Stats.AddWeaponRangeTotalPercent(totalWeaponRangePercent);
        Stats.AddMaxHealthTotalPercent(totalMaxHealthPercent);
    }
}
