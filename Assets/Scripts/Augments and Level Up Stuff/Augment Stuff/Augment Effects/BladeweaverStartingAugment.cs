using PurrNet.Prediction;
using UnityEngine;

// Bladeweaver's class-passive "Unique Behavior" starting augment — extra orbiting blades traded for reach:
// more weapons and a bigger orbit radius, and a much shorter range so the class stays a close-range
// brawler. Used to also grant a compensating +30% Strength for the extra weapon count's own auto-divide
// (2026-08-19 removed, same reasoning as WeaponCountAugment/MultiShotAugment) — that's covered structurally
// by WeaponStrength's own per-weapon divide now (see CharacterStatsPD.WeaponStrength's own doc comment), no
// bonus needed on top.
public class BladeweaverStartingAugment : AugmentEffectPD<BladeweaverStartingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int   bonusWeaponCountFlat  = 2;
    [SerializeField] private float bonusWeaponRadiusFlat  = 3f;
    [SerializeField] private float totalWeaponRangePercent = -0.5f;

    protected override void OnApply()
    {
        Stats.AddWeaponCountFlat(bonusWeaponCountFlat);
        Stats.AddWeaponRadiusFlat(bonusWeaponRadiusFlat);
        Stats.AddWeaponRangeTotalPercent(totalWeaponRangePercent);
    }
}
