using PurrNet.Prediction;
using UnityEngine;

// Generic 20-point stat augment: +25% attack speed (5pts, via attackSpeedFlat — added directly to base,
// same as the progression system's own AttackSpeed stat conversion) + 15% total attack speed (15pts, via
// attackSpeedTotalPercent — the genuine multiplier).
public class AttackSpeedAugment : AugmentEffectPD<AttackSpeedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // Renamed to exactly match the CharacterStatsPD fields these feed (attackSpeedFlat/attackSpeedTotalPercent
    // have no "bonus" prefix on CharacterStatsPD itself — see that class for why).
    [SerializeField] private float attackSpeedFlat         = 0.25f;
    [SerializeField] private float attackSpeedTotalPercent = 0.15f;

    protected override void OnApply()
    {
        Stats.AddAttackSpeedFlat(attackSpeedFlat);
        Stats.AddAttackSpeedTotalPercent(attackSpeedTotalPercent);
    }
}
