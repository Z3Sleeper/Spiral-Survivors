using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/GremlinAugment.cs. Old system manually scaled
// transform.localScale, compensated a hardcoded list of unscaled children (ReviveZone/WorldspaceUI/Minimap
// Icon), and supported un-applying via OnDestroy. CharacterStatsPD.AddSizePercent already does all of that
// (including the exact same unscaled-children list) — PD augments are one-time permanent applications with
// no un-apply concept, matching every other AugmentEffectPD so far.
public class GremlinAugment : AugmentEffectPD<GremlinAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float sizePercent        = -0.15f;
    [SerializeField] private float attackSpeedPercent = 0.15f;
    [SerializeField] private float moveSpeedPercent   = 0.15f;

    protected override void OnApply()
    {
        Stats.AddSizePercent(sizePercent);
        Stats.AddAttackSpeedTotalPercent(attackSpeedPercent);
        Stats.AddMoveSpeedPercent(moveSpeedPercent);
    }
}
