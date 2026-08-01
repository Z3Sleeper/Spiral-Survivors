using PurrNet.Prediction;
using UnityEngine;

// Same shape as DashHasteAugment (flat + percent dual structure, same 20-point budget: +25 flat (5pts) +
// 15% bonus (15pts) = 20pts) — just against CharacterStatsPD.EffectiveAbilityHaste instead of dash's own.
public class AbilityHasteAugment : AugmentEffectPD<AbilityHasteAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float abilityHaste             = 25f;
    [SerializeField] private float bonusAbilityHastePercent = 0.15f;

    protected override void OnApply()
    {
        Stats.AddAbilityHaste(abilityHaste);
        Stats.AddAbilityHastePercent(bonusAbilityHastePercent);
    }
}
