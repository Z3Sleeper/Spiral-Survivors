using PurrNet.Prediction;
using UnityEngine;

// Reduces Dash Cooldown by 15% — a "capstone"-style flat percent cut meant to synergize WITH Dash Haste
// (DashHasteAugment), not compete with it: routes through CharacterStatsPD's own dashCooldownReductionPercent
// tier, a SEPARATE multiplier layered on top of the Haste-derived cooldown, not folded into dashHaste's own
// diminishing-returns curve — see DashCooldown's own doc comment for the full reasoning. Multiplicative
// diminishing-returns stacking (same "never reach 0" shape as AddDamageReductionPercent/
// AddSurgeCostReductionPercent) means this plays correctly with any future Dash Cooldown-changing augment,
// increase or decrease, without risking a 0-or-negative cooldown. One-shot flat grant, no active window/State
// needed, same shape as any other permanent flat-stat augment (e.g. LuckAugment).
public class DashCooldownReductionAugment : AugmentEffectPD<DashCooldownReductionAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    public float dashCooldownReductionPercent = 0.15f;

    protected override void OnApply() => Stats.AddDashCooldownReductionPercent(dashCooldownReductionPercent);
}
