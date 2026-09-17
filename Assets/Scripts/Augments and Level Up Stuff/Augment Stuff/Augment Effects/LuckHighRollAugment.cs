using PurrNet.Prediction;
using UnityEngine;

// Adds highrollChanceBonus to bonusHighrollPercent (Stats.AddBonusHighrollPercent — 2026-09-13, switched from
// AddHighrollPenalty, see that method's own doc comment for why: this is a plain stat buff, not a genuine
// tradeoff, so it belongs on the Bonus tier, not Penalty), making the highroll threshold that much easier to
// clear.
public class LuckHighRollAugment : AugmentEffectPD<LuckHighRollAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.05 matches the actual value already tuned on this prefab (the old highrollPenalty field's own
    // serialized value) — kept identical across the rename so this doesn't silently change, even though the
    // code's own PREVIOUS default (0.1f) never actually reflected what was live on the prefab.
    [SerializeField] private float highrollChanceBonus = 0.05f;

    protected override void OnApply()
    {
        Stats.AddBonusHighrollPercent(highrollChanceBonus);
    }
}
