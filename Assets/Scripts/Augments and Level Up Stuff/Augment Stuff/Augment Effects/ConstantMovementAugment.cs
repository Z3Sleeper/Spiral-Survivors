using PurrNet.Prediction;
using UnityEngine;

// Passive: +5 flat Move Speed, and the character is ALWAYS attempting to move — see
// PlayerControllerPD.SetForceMovement's own doc comment for the actual mechanism (falls back to the last real
// movement direction whenever there's no current input, instead of standing still, always at full speed —
// see that same comment for why no extra normalization is needed). "You can't stand still." No OnDestroy()
// override needed — same permanent-for-the-whole-match shape as every other simple stat-granting augment
// (e.g. MoveSpeedAugment), never explicitly reverted.
public class ConstantMovementAugment : AugmentEffectPD<ConstantMovementAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float moveSpeedFlat = 4f;

    protected override void OnApply()
    {
        Stats.AddMoveSpeedFlat(moveSpeedFlat);
        var controller = Stats.GetComponent<PlayerControllerPD>();
        if (controller != null) controller.SetForceMovement(true);
    }
}
