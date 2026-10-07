using PurrNet.Prediction;
using UnityEngine;

// Executioner's class-passive "Unique Behavior" starting augment (renamed 2026-10-07 from
// CritExecuteAugment, pure rename — see [[project_class_passive_convention]] — Stuart had already been
// using this as Executioner's own starting augment, just hadn't renamed it to say so; now exclusively
// Executioner's, matching RogueStartingAugment/ArcherStartingAugment/BladeweaverStartingAugment's own
// naming). Mechanic/description text below is unchanged.
//
// "Your Crits execute enemies below 25% health. +25% Crit Chance." Adds to Stats.critExecuteThreshold
// (checked generically inside EnemyBasePD.TakeDamage — the single choke point every damage source funnels
// through, so this covers crits from any weapon/effect uniformly with no per-call-site hook needed) —
// CharacterStatsPD.Execute gates purely on that threshold being above 0, no separate enable flag. Crit
// Chance bonus is a flat addition (Stats.AddCritChanceFlat), same convention as every other flat stat
// grant — swapped from Crit Damage so the augment also makes its own execute threshold more likely to
// actually trigger, not just hit harder when it does. Bosses resist this by 80% (see
// CharacterStatsPD.BossCritExecuteThreshold) — a 25% threshold becomes 5% against a boss, not fully
// immune, but far harder to trigger.
public class ExecutionerStartingAugment : AugmentEffectPD<ExecutionerStartingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float critExecuteThreshold = 0.25f;
    [SerializeField] private float bonusCritChanceFlat   = 0.1f;

    protected override void OnApply()
    {
        Stats.AddCritExecuteThresholdPercent(critExecuteThreshold);
        Stats.AddCritChanceFlat(bonusCritChanceFlat);
    }
}
