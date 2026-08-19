using PurrNet.Prediction;
using UnityEngine;

// Generic 20-point stat augment: +1.25 flat move speed (5pts) + 15% move speed (15pts).
public class MoveSpeedAugment : AugmentEffectPD<MoveSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float bonusMoveSpeedFlat    = 3f;
    [SerializeField] private float bonusMoveSpeedPercent = 0f;

    protected override void OnApply()
    {
        Stats.AddMoveSpeedFlat(bonusMoveSpeedFlat);
        Stats.AddMoveSpeedPercent(bonusMoveSpeedPercent);
    }
}
