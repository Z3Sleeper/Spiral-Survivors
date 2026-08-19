using PurrNet.Prediction;
using UnityEngine;

// Increases Surge Cost by +100% (doubles it, alone) and multiplies SurgeAttackSpeedMultiplier by 1.5x — a
// "go big or go home" Surge/Energy tradeoff. One-shot permanent grant, no active window/State needed, same
// shape as any other permanent flat-stat augment (e.g. LuckAugment).
//
// Stacks correctly with SurgeWeaponCountAugment's own Surge Cost reduction grant (or any future Surge
// Cost-changing augment) —
// Stats.AddSurgeCostIncreasePercent routes through its OWN dedicated additive tier (surgeCostIncreasePercent),
// structurally separate from the reduction side's own multiplicative diminishing-returns tier
// (surgeCostReductionPercent), and additive rather than multiplicative so a future second "increase Surge
// Cost" source would stack as +200% (3x total) combined, not +300% (4x total) from multiplying two
// independent x2 factors together. See CharacterStatsPD.AddSurgeCostIncreasePercent's own doc comment for
// the full reasoning (this is currently the only augment writing to that tier).
public class SurgeCostIncreaseAugment : AugmentEffectPD<SurgeCostIncreaseAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float surgeCostIncreasePercent = 1f;
    [SerializeField] private float surgeAttackSpeedMultiplier = 1.5f;

    protected override void OnApply()
    {
        Stats.AddSurgeCostIncreasePercent(surgeCostIncreasePercent);
        Stats.MultiplySurgeAttackSpeedPenalty(surgeAttackSpeedMultiplier);
    }
}
