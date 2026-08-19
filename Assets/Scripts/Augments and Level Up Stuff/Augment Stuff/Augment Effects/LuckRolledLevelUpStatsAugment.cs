using PurrNet.Prediction;
using UnityEngine;

// Flags this player (CharacterStatsPD.luckRolledLevelUpStats) so their own future level-up stat-pick cards
// (the "+X Stat & +Y Stat" cards, not augment cards) roll BOTH stats on the card together against ONE Luck
// variance + Highroll roll, same shape as any other luck-influenced roll — see
// AugmentCardUI.StatAugmentSetup/WeaponDamageMath.RollLuckMultiplier for the actual roll (client-local,
// cached at display time, applied unchanged on pick). One-shot permanent flag, no active window/State
// needed, same shape as any other permanent flat-stat augment (e.g. LuckAugment) — just flips a bool
// instead of adding to a stat.
//
// highrollBonusScale (2026-08-19): this is OUTSIDE the "damage" scope the 2026-08-19 Highroll/Crit rework
// applies to — Stats.HighrollBonus is now a direct 300%-at-base replacement multiplier for a DAMAGE roll
// (see WeaponDamageMath.ApplyCritAndHighroll), way too strong to hand a level-up stat pick unscaled (a
// highrolled pick would be worth 3x-4x a normal one). Passed into RollLuckMultiplier as a SCALED-DOWN
// highrollBonus argument instead (Stats.HighrollBonus * highrollBonusScale) — RollLuckMultiplier itself still
// applies it as "+X on top of 1" (its own generic shape, untouched), so at base values a highrolled stat pick
// gets exactly +30% (Stats.HighrollBonus 3f * 0.1 scale = 0.3, i.e. a x1.3 multiplier), not the full damage-
// scope 300%.
public class LuckRolledLevelUpStatsAugment : AugmentEffectPD<LuckRolledLevelUpStatsAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float highrollBonusScale = 0.1f;

    protected override void OnApply() => Stats.EnableLuckRolledLevelUpStats(highrollBonusScale);
}
