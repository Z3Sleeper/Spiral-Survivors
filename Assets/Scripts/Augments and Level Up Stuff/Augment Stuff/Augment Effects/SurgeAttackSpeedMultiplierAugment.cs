using PurrNet.Prediction;
using UnityEngine;

// Renamed from SurgeCostIncreaseAugment (2026-08-24) — the Surge Cost increase half is gone entirely, this
// is now purely a Surge Attack Speed buff. Grants +surgeAttackSpeedBonusPercent, ADDITIVE against
// CharacterStatsPD.surgeAttackSpeedBonusPercent's own 1f baseline (2026-10-02, was a *= multiply against a
// field that used to be misleadingly named surgeAttackSpeedPenalty — see that property's own doc comment).
// 0.15 default lands at a combined x2.3 Attack Speed during Surge per pick (base surgeAttackSpeedMulti 2x *
// 1.15), matching the original design intent — the old 1.3x multiply actually landed at x2.6, overshooting
// that intent, which is part of why this got fixed alongside the rename. Two picks now correctly land at
// x2.6 (2 * (1 + 0.15 + 0.15)) via straightforward additive stacking, not x3.38 (2 * 1.3 * 1.3) the old
// multiplicative version would have compounded to. One-shot permanent grant, no active window/State needed,
// same shape as any other permanent flat-stat augment (e.g. LuckAugment).
public class SurgeAttackSpeedMultiplierAugment : AugmentEffectPD<SurgeAttackSpeedMultiplierAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float surgeAttackSpeedBonusPercent = 0.15f;
    // Generic +10 Max Energy side-bonus (2026-10-07), same flat grant most other energy/Surge-themed
    // augments in this codebase carry.
    public float bonusMaxEnergyFlat = 10f;

    protected override void OnApply()
    {
        Stats.AddSurgeAttackSpeedBonusPercent(surgeAttackSpeedBonusPercent);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }
}
