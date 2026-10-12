using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/GremlinAugment.cs. Old system manually scaled
// transform.localScale, compensated a hardcoded list of unscaled children (ReviveZone/WorldspaceUI/Minimap
// Icon), and supported un-applying via OnDestroy. CharacterStatsPD.AddSizePercent/AddSizePenaltyPercent
// already do all of that (including the exact same unscaled-children list) — PD augments are one-time
// permanent applications with no un-apply concept, matching every other AugmentEffectPD so far.
//
// Reworked 2026-10-10 — originally THE Move Speed augment, long since overshadowed by other sources.
// Leaning harder into its own "small and fast" identity instead: -25% Size and -25% Total Range (both real
// downsides now, routed through their genuine multiplicative Penalty tiers — AddSizePenaltyPercent is a
// brand new tier, see its own doc comment; AddWeaponRangeTotalPenaltyPercent already existed), in exchange
// for +25% Total Attack Speed and +25% Total Move Speed (both the additive Total tier every generic buff of
// theirs already uses) — buffs into the additive bucket, nerfs into the multiplicative one, per
// [[feedback_buff_debuff_bucket_convention]].
public class GremlinAugment : AugmentEffectPD<GremlinAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve these
    // directly as description tokens; only public fields/properties/methods are checked. All four already
    // end in the literal "Percent" suffix so the auto-*100 formatting fires.
    public float sizePenaltyPercent           = 0.25f;
    public float attackSpeedTotalPercent      = 0.25f;
    public float moveSpeedTotalPercent        = 0.25f;
    public float weaponRangeTotalPenaltyPercent = 0.25f;

    protected override void OnApply()
    {
        Stats.AddSizePenaltyPercent(sizePenaltyPercent);
        Stats.AddAttackSpeedTotalPercent(attackSpeedTotalPercent);
        Stats.AddMoveSpeedTotalPercent(moveSpeedTotalPercent);
        Stats.AddWeaponRangeTotalPenaltyPercent(weaponRangeTotalPenaltyPercent);
    }
}
