using PurrNet.Prediction;
using UnityEngine;

// Bladeweaver's class-passive "Unique Behavior" starting augment — extra orbiting blades traded for reach:
// more weapons and a bigger orbit radius, and a much shorter range so the class stays a close-range
// brawler. Used to also grant a compensating +30% Strength for the extra weapon count's own auto-divide
// (2026-08-19 removed, same reasoning as WeaponCountAugment/MultiShotAugment) — that's covered structurally
// by WeaponStrength's own per-weapon divide now (see CharacterStatsPD.WeaponStrength's own doc comment), no
// bonus needed on top.
//
// weaponRangeScalingPenaltyPercent, not AddWeaponRangeTotalPercent (2026-09-14 fix) — the old -50% wrote
// directly into the shared WeaponRange stat, which every augment that scales off Range
// (PhantomBladeRangeAugment, OnHitDamageDistanceAugment) also reads as its own scaling input, silently making
// those augments half as good on Bladeweaver specifically — "bait" options that read fine in the card but
// were quietly worse for this one class. AddWeaponRangeScalingPenaltyPercent instead scales ONLY the weapon's
// own actual physical reach (model size, hitbox depth, orbit offset — see
// CharacterStatsPD.EffectiveWeaponRange's own doc comment), leaving the WeaponRange stat itself — and
// everything that reads it — completely untouched. Routed through the multiplicative Penalty tier
// specifically (not the additive Bonus tier ArcherStartingAugment's own boost uses), same plain
// "amount-to-remove" authoring convention OuterTrackAugment's own identical reach-reduction field already
// uses (0.5 = weapon reaches 50% as far) — see AddWeaponRangeScalingPenaltyPercent's own doc comment for why
// a reduction needs to be multiplicative, not additive, with any other reduction source.
public class BladeweaverStartingAugment : AugmentEffectPD<BladeweaverStartingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int   bonusWeaponCountFlat  = 2;
    [SerializeField] private float bonusWeaponRadiusFlat  = 3f;
    [SerializeField] private float weaponRangeScalingPenaltyPercent = 0.5f;

    protected override void OnApply()
    {
        Stats.AddWeaponCountFlat(bonusWeaponCountFlat);
        Stats.AddWeaponRadiusFlat(bonusWeaponRadiusFlat);
        Stats.AddWeaponRangeScalingPenaltyPercent(weaponRangeScalingPenaltyPercent);
    }
}
