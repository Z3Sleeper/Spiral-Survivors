using PurrNet.Prediction;
using UnityEngine;

// +50% Stack Power (stackPowerPercent, additive against its 1f baseline — see
// CharacterStatsPD.AddStackPowerPercent's own doc comment). Not used by anything yet — this is groundwork
// for future stacking-effect augments that read Stats.StackPower.
public class StackPowerAugment : AugmentEffectPD<StackPowerAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float stackPowerPercent = 0.5f;

    protected override void OnApply()
    {
        Stats.AddStackPowerPercent(stackPowerPercent);
    }
}
