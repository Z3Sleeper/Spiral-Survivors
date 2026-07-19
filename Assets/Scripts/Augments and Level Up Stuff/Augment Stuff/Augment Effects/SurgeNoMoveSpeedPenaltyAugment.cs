using PurrNet.Prediction;

// New tech — Surge normally reduces move speed by 50% (CharacterStatsPD.surgeMoveSpeedPenalty defaults to
// 0.5f, the reduction multiplier itself). This removes that penalty entirely, i.e. 1f (full move speed
// while surging). A pair with SurgeHalfMoveSpeedPenaltyAugment — intended to be mutually exclusive (wire an
// incompatibleAugments entry on the data assets), but SetSurgeMoveSpeedPenalty's direct-override shape
// means picking both would just resolve to whichever applied last, not a broken compounded value.
public class SurgeNoMoveSpeedPenaltyAugment : AugmentEffectPD<SurgeNoMoveSpeedPenaltyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    protected override void OnApply()
    {
        Stats.SetSurgeMoveSpeedPenalty(1f);
    }
}
