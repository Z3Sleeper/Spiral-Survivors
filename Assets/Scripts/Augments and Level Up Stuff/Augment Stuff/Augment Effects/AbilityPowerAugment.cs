using PurrNet.Prediction;
using UnityEngine;

// +30% Ability Power, additive against CharacterStatsPD.abilityPowerMultiplier's 1f baseline (AbilityPower
// = abilityPowerMultiplier * abilityPowerPenalty) — same "additive against 1f" convention as
// AddStackPowerPercent. See CharacterStatsPD.AbilityPower's own doc comment for how each current ability
// (RogueSpinAbilityPD/RogueVanishAbilityPD) interprets this.
public class AbilityPowerAugment : AugmentEffectPD<AbilityPowerAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusAbilityPowerPercent = 0.3f;

    protected override void OnApply()
    {
        Stats.AddAbilityPowerPercent(bonusAbilityPowerPercent);
    }
}
