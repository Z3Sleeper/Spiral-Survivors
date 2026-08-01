using PurrNet.Prediction;
using UnityEngine;

// "Your Crits execute enemies below 25% health. +10% Crit Damage." Adds to Stats.critExecuteThreshold
// (checked generically inside EnemyBasePD.TakeDamage — the single choke point every damage source funnels
// through, so this covers crits from any weapon/effect uniformly with no per-call-site hook needed) —
// CharacterStatsPD.Execute gates purely on that threshold being above 0, no separate enable flag. Crit
// Damage bonus is a flat addition (Stats.AddCritDamageFlat), same convention as every other flat stat
// grant. Bosses resist this by 80% (see CharacterStatsPD.BossCritExecuteThreshold) — a 25% threshold
// becomes 5% against a boss, not fully immune, but far harder to trigger.
public class CritExecuteAugment : AugmentEffectPD<CritExecuteAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float critExecuteThreshold = 0.25f;
    [SerializeField] private float bonusCritDamageFlat   = 0.15f;

    protected override void OnApply()
    {
        Stats.AddCritExecuteThresholdPercent(critExecuteThreshold);
        Stats.AddCritDamageFlat(bonusCritDamageFlat);
    }
}
