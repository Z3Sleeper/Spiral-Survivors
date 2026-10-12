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
// highrollBonusScalePercent (2026-08-19, renamed+made public 2026-10-12): this is OUTSIDE the "damage" scope
// the 2026-08-19 Highroll/Crit rework applies to — Stats.HighrollBonus is now a direct 300%-at-base
// replacement multiplier for a DAMAGE roll (see WeaponDamageMath.ApplyCritAndHighroll), way too strong to
// hand a level-up stat pick unscaled (a highrolled pick would be worth 3x-4x a normal one). Passed into
// RollLuckMultiplier as a SCALED-DOWN highrollBonus argument instead (Stats.HighrollBonus *
// highrollBonusScalePercent) — RollLuckMultiplier itself still applies it as "+X on top of 1" (its own
// generic shape, untouched), so at base values a highrolled stat pick gets exactly +30% (Stats.HighrollBonus
// 3f * 0.1 scale = 0.3, i.e. a x1.3 multiplier), not the full damage-scope 300%.
//
// effectiveness dampening REMOVED (2026-10-12) — used to halve the whole roll's swing down from LuckFloor/
// LuckCeiling's own range (then 0.5x-1.5x), since that alone read as "far too swingy." LuckFloor/LuckCeiling's
// live baseline is now 0.75/1.25 directly — the dampened range this was already producing — so the separate
// dampening step was redundant with what the base roll produces on its own; see
// CharacterStatsPD.EnableLuckRolledLevelUpStats's own doc comment.
public class LuckRolledLevelUpStatsAugment : AugmentEffectPD<LuckRolledLevelUpStatsAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // Public, not [SerializeField] private (2026-10-12 — was private before this rework), and renamed to end
    // in the literal "Percent" suffix — so AugmentDescriptionFormatter's reflection can resolve this directly
    // as a description token with the auto-*100 formatting (only public fields/properties/methods are
    // checked; the suffix fires on a literal EndsWith("Percent") check).
    // 8% (2026-10-12, up from the old live 5%) — at base LuckCeiling 1.25 this lands a highroll at 1.55x;
    // starting point, with 4% as the planned fallback if 8% tests too strong (unlikely per Stuart — this only
    // ever triggers on an already-5%-chance highroll in the first place).
    public float highrollBonusScalePercent = 0.08f;

    // 2026-10-12: this augment was underperforming on its own (the luck-roll flip alone wasn't enticing
    // enough against everything else in a now-140+-augment pool, see
    // [[project_levelup_option_count_diversity_design]] for why pool competition matters more now), so it
    // also grants the last remaining Level Up Options slot (CharacterStatsPD.AddLevelUpOptions — +1, clamped
    // to 5 same as every other source) plus a flat Highroll Chance bump (AddBonusHighrollPercent, same
    // method LuckCeilingAugment now also uses) as extra incentive. levelUpOptionsBonus stays a plain int, no
    // "Percent" suffix needed for a raw count.
    public int   levelUpOptionsBonus        = 1;
    public float highrollChanceBonusPercent = 0.03f;

    protected override void OnApply()
    {
        Stats.EnableLuckRolledLevelUpStats(highrollBonusScalePercent);
        Stats.AddLevelUpOptions(levelUpOptionsBonus);
        Stats.AddBonusHighrollPercent(highrollChanceBonusPercent);
    }
}
