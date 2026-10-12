using PurrNet.Prediction;
using UnityEngine;

// +10% Luck Ceiling, directly additive against the ceiling value itself (AddLuckCeilingFlat) — at base
// stats, 1.5 -> 1.6. NOT AddLuckCeilingPercent, which would instead scale the 1.5 baseline multiplicatively.
//
// 2026-10-12: folded in LuckHighRollAugment's own effect (that augment removed entirely) rather than
// leaving the two as separate picks — both were small, same-flavor "Luck variance" buffs (this ability's
// own displayName/icon already matched LuckHighRollAugment's before this merge), and with 140+ augments
// already in the pool plus the new tighter level-up option count (see
// [[project_levelup_option_count_diversity_design]]), two near-duplicate Luck picks competing for the same
// slot was pure pool bloat. Adds highrollChanceBonusPercent to bonusHighrollPercent
// (Stats.AddBonusHighrollPercent — a plain stat buff, not a tradeoff, so it belongs on the Bonus tier, not
// Penalty), making the highroll threshold that much easier to clear, same formula LuckHighRollAugment used.
//
// Both fields public, not [SerializeField] private (2026-10-12 — were private before this rework) — so
// AugmentDescriptionFormatter's reflection can resolve them directly as description tokens; only public
// fields/properties/methods are checked. Both renamed to end in the literal "Percent" suffix so the
// auto-*100 formatting fires (same "PerUnit vs Percent" EndsWith trap noted elsewhere in this codebase) —
// luckCeilingBonusPercent's default set to 0.1f directly (matching what was actually live on the prefab
// under its old name, bonusLuckCeilingFlat — renaming a serialized field loses any prefab-saved override,
// so the new default has to BE that old live value, not the old field's own stale 0.15f code default).
public class LuckCeilingAugment : AugmentEffectPD<LuckCeilingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float luckCeilingBonusPercent = 0.1f;
    public float highrollChanceBonusPercent = 0.05f;

    protected override void OnApply()
    {
        Stats.AddLuckCeilingFlat(luckCeilingBonusPercent);
        Stats.AddBonusHighrollPercent(highrollChanceBonusPercent);
    }
}
