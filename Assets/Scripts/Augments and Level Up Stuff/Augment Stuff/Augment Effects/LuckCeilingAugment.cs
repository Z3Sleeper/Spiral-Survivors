using PurrNet.Prediction;
using UnityEngine;

// +0.25 Luck Ceiling (bonusLuckCeilingPercent, additive against its 1f baseline — see
// CharacterStatsPD.AddLuckCeilingPercent's own doc comment). At base stats, 150% -> 175%.
public class LuckCeilingAugment : AugmentEffectPD<LuckCeilingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float luckCeilingBonus = 0.25f;

    protected override void OnApply()
    {
        Stats.AddLuckCeilingPercent(luckCeilingBonus);
    }
}
