using PurrNet.Prediction;

// New tech — Surge normally reduces move speed by 50% (CharacterStatsPD.surgeMoveSpeedPenalty defaults to
// 0.5f, the reduction multiplier itself). This halves that penalty (50% reduction -> 25% reduction), i.e.
// 0.75f. A pair with SurgeNoMoveSpeedPenatlyAugment — intended to be mutually exclusive (wire an
// incompatibleAugments entry on the data assets), but SetSurgeMoveSpeedPenalty's direct-override shape
// means picking both would just resolve to whichever applied last, not a broken compounded value.
public class SurgeHalfMoveSpeedPenaltyAugment : AugmentEffectPD<SurgeHalfMoveSpeedPenaltyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    protected override void OnApply()
    {
        Stats.SetSurgeMoveSpeedPenalty(0.75f);
    }
}
