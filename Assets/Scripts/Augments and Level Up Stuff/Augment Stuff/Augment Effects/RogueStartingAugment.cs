using PurrNet.Prediction;
using UnityEngine;

// Rogue's class-passive "Unique Behavior" starting augment (2026-10-02) — a real stat trade-off, not just a
// description/augment-delivery vehicle (see [[project_class_passive_convention]]): -25% Effective Weapon
// Range for +50% Total Attack Speed, leaning further into Rogue's fast-attacking, close-range identity.
//
// Routed through AddWeaponRangeScalingPenaltyPercent, not AddWeaponRangeTotalPercent — same reasoning
// BladeweaverStartingAugment's own identical reach-reduction field uses: scales ONLY the weapon's actual
// physical reach (CharacterStatsPD.EffectiveWeaponRange), leaving the WeaponRange stat itself untouched so
// anything that reads Range as a scaling INPUT (PhantomBladeRangeAugment, OnHitDamageDistanceAugment) isn't
// silently worse on Rogue specifically. Multiplicative Penalty tier (0.25 = weapon reaches 75% as far), same
// "amount-to-remove" authoring convention as that augment's own field. Attack Speed uses the plain additive
// Total-tier percent instead (AddAttackSpeedTotalPercent) — a genuine permanent buff, matching the usual
// "buffs stack additively" convention.
public class RogueStartingAugment : AugmentEffectPD<RogueStartingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {weaponRangeReductionPercent}/{attackSpeedBonusPercent} directly as description tokens, and both end
    // in the literal "Percent" suffix so its auto-*100 formatting fires (EndsWith("Percent") check).
    public float weaponRangeReductionPercent = 0.25f;
    public float attackSpeedBonusPercent     = 0.5f;

    protected override void OnApply()
    {
        Stats.AddWeaponRangeScalingPenaltyPercent(weaponRangeReductionPercent);
        Stats.AddAttackSpeedTotalPercent(attackSpeedBonusPercent);
    }
}
