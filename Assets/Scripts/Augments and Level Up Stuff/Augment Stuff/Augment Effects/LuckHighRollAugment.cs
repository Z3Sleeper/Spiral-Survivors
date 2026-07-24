using PurrNet.Prediction;
using UnityEngine;

// Subtracts 0.05 from highrollPenalty (Stats.AddHighrollPenalty — see that method's own doc comment for
// the inverted-sign reasoning), making the highroll threshold 5% easier to clear.
public class LuckHighRollAugment : AugmentEffectPD<LuckHighRollAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float highrollPenalty = 0.05f;

    protected override void OnApply()
    {
        Stats.AddHighrollPenalty(highrollPenalty);
    }
}
