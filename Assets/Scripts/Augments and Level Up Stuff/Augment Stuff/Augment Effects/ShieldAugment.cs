using PurrNet.Prediction;
using UnityEngine;

public class ShieldAugment : AugmentEffectPD<ShieldAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // Percent of Max Health, not a flat shield number — see CharacterStatsPD.AddMaxShieldPercent's own doc
    // comment (same units as baseMaxShieldPercent itself).
    [SerializeField] private float bonusMaxShieldPercent = 0.2f;

    protected override void OnApply()
    {
        Stats.AddMaxShieldPercent(bonusMaxShieldPercent);
    }
}
