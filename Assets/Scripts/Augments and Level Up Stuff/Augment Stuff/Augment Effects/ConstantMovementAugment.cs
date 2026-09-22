using PurrNet.Prediction;
using UnityEngine;

// Passive: +20% Bonus-tier Move Speed (AddMoveSpeedPercent, 2026-09-19 — was a flat +4 via AddMoveSpeedFlat,
// converted project-wide alongside every other flat-Move-Speed augment now that Move Speed no longer comes
// off Stat augments, per explicit design), and the character is ALWAYS attempting to move — see
// PlayerControllerPD.SetForceMovement's own doc comment for the actual mechanism (falls back to the last real
// movement direction whenever there's no current input, instead of standing still, always at full speed —
// see that same comment for why no extra normalization is needed). "You can't stand still." No OnDestroy()
// override needed — same permanent-for-the-whole-match shape as every other simple stat-granting augment,
// never explicitly reverted.
public class ConstantMovementAugment : AugmentEffectPD<ConstantMovementAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {moveSpeedPercent} directly as a description token; only public fields/properties/methods are checked.
    // Still Inspector-serializable either way.
    public float moveSpeedPercent = 0.20f;

    protected override void OnApply()
    {
        Stats.AddMoveSpeedPercent(moveSpeedPercent);
        var controller = Stats.GetComponent<PlayerControllerPD>();
        if (controller != null) controller.SetForceMovement(true);
    }
}
