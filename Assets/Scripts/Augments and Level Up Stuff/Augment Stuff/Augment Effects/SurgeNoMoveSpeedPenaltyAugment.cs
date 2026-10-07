using PurrNet.Prediction;

// New tech — Surge normally reduces move speed by 50% (CharacterStatsPD.surgeMoveSpeedPenalty defaults to
// 0.5f, the reduction multiplier itself). This removes that penalty entirely, i.e. 1f (full move speed
// while surging).
public class SurgeNoMoveSpeedPenaltyAugment : AugmentEffectPD<SurgeNoMoveSpeedPenaltyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Generic +10 Max Energy side-bonus (2026-10-07), same flat grant most other energy/Surge-themed
    // augments in this codebase carry.
    public float bonusMaxEnergyFlat = 10f;

    protected override void OnApply()
    {
        Stats.SetSurgeMoveSpeedPenalty(1f);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }
}
