using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/DashHasteAugment.cs, now generically available (not
// class-exclusive). Tuned to the user's 20-point budget: +25 flat dash haste (5pts) + 15% dash haste
// (15pts) = 20pts — the percent half needed a new CharacterStatsPD.bonusDashHastePercent mechanic added
// alongside this (DashHaste didn't have a flat+percent dual structure like Range/CritChance yet).
public class DashHasteAugment : AugmentEffectPD<DashHasteAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float dashHaste              = 60f;
    [SerializeField] private float bonusDashHastePercent  = 0f;

    protected override void OnApply()
    {
        Stats.AddDashHaste(dashHaste);
        Stats.AddDashHastePercent(bonusDashHastePercent);
    }
}
