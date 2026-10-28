using PurrNet.Prediction;
using UnityEngine;

// Executioner's class-passive "Unique Behavior" starting augment (renamed 2026-10-07 from
// CritExecuteAugment, pure rename — see [[project_class_passive_convention]] — already in use as
// Executioner's own starting augment, just hadn't been renamed to say so; now exclusively
// Executioner's, matching RogueStartingAugment/ArcherStartingAugment/BladeweaverStartingAugment's own
// naming).
//
// Reworked 2026-10-08 to lean harder into the "slow, heavy-hitting executioner" identity instead of just
// being a flat Crit Chance grant: Crit Chance bonus removed outright, replaced with a real tradeoff —
// -35% Total Attack Speed in exchange for +100% Knockback and +20% EffectiveWeaponRange. The Crit Execute
// mechanic itself is untouched.
//
// "Your Crits execute enemies below 25% health." Adds to Stats.critExecuteThreshold (checked generically
// inside EnemyBasePD.TakeDamage — the single choke point every damage source funnels through, so this
// covers crits from any weapon/effect uniformly with no per-call-site hook needed) —
// CharacterStatsPD.Execute gates purely on that threshold being above 0, no separate enable flag. Bosses
// resist this by 80% (see CharacterStatsPD.BossCritExecuteThreshold) — a 25% threshold becomes 5% against
// a boss, not fully immune, but far harder to trigger.
//
// Attack Speed penalty routes through the dedicated Penalty tier (AddAttackSpeedPenaltyPercent, added
// 2026-10-08 specifically for this augment — multiplicative, *= (1f - amount), authored as a positive
// amount-to-remove) rather than the additive Total tier — nerfs always go into the Penalty bucket, never
// the Bonus/Total one, so stacking multiple attack-speed nerfs compounds toward but never past 0 instead
// of being able to sum below it. See [[feedback_buff_debuff_bucket_convention]]. Knockback routes through
// the new Total tier (AddKnockbackTotalPercent, added 2026-10-08 specifically for this augment — Knockback
// previously only had a Bonus tier, knockbackMultiplier/AddKnockbackPercent) rather than that Bonus tier,
// so this class-defining +100% sits on its own accumulator instead of crowding the generic Bonus tier any
// plain future +Knockback augment would also write into. EffectiveWeaponRange bonus goes through AddWeaponRangeScalingBonusPercent — the same
// class-defining reach-scaling hook Archer's own bow-range boost and Bladeweaver's own reach penalty use —
// rather than a generic WeaponRange stat, since this is meant to scale the WEAPON'S actual geometry, not
// the stat every other range augment reads as its own scaling input.
public class ExecutionerStartingAugment : AugmentEffectPD<ExecutionerStartingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float critExecuteThresholdPercent = 0.25f;
    public float attackSpeedPenaltyPercent = 0.35f;
    public float knockbackBonusPercent = 1f;
    public float effectiveWeaponRangeBonusPercent = 0.2f;

    protected override void OnApply()
    {
        Stats.AddCritExecuteThresholdPercent(critExecuteThresholdPercent);
        Stats.AddAttackSpeedPenaltyPercent(attackSpeedPenaltyPercent);
        Stats.AddKnockbackTotalPercent(knockbackBonusPercent);
        Stats.AddWeaponRangeScalingBonusPercent(effectiveWeaponRangeBonusPercent);
    }
}
