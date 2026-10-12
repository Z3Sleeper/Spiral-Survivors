using PurrNet.Prediction;
using UnityEngine;

// Reworked 2026-10-10 — originally THE Range augment, long since overshadowed by other sources. Leaning
// harder into its own "big and tanky" identity instead: -25% Total Attack Speed (a real downside now,
// routed through AddAttackSpeedPenaltyPercent's genuine multiplicative Penalty tier), in exchange for
// +25% Size, +25% Total Health, and +25% Total Range — all three the additive Bonus/Total tier every
// generic buff of theirs already uses. Buffs into the additive bucket, nerfs into the multiplicative one,
// per [[feedback_buff_debuff_bucket_convention]]. Health specifically moved from AddMaxHealthPercent
// (Bonus tier) to AddMaxHealthTotalPercent (Total tier, 2026-10-10 fix) — HealthAugment itself already made
// this same switch (see AddMaxHealthPercent's own doc comment), and "Total Health" is explicitly what was
// asked for here too.
public class TitanAugment : AugmentEffectPD<TitanAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve these
    // directly as description tokens; only public fields/properties/methods are checked. All four already
    // end in the literal "Percent" suffix so the auto-*100 formatting fires.
    public float bonusSizePercent = 0.25f;
    public float weaponRangeTotalPercent = 0.25f;
    public float maxHealthTotalPercent = 0.25f;
    public float attackSpeedPenaltyPercent = 0.25f;

    protected override void OnApply()
    {
        // AddSizePercent handles the transform scale (and the unscaled-children adjustment) itself,
        // additively against CharacterStatsPD's shared bonusSizePercent — so a future size augment
        // (e.g. Gargantuan) stacks its own delta on top instead of compounding multiplicatively with
        // this one. Every weapon's own root localScale already reads bonusSizePercent directly (see
        // WeaponManagerPD's own RegisterWeapon/LateUpdate), so its hitbox/visual scale grows with the
        // character without a separate call here. WeaponRadius (orbit distance) deliberately doesn't
        // scale with size — see CharacterStatsPD.WeaponRadius for why.
        Stats.AddSizePercent(bonusSizePercent);
        Stats.AddWeaponRangeTotalPercent(weaponRangeTotalPercent);
        Stats.AddMaxHealthTotalPercent(maxHealthTotalPercent);
        Stats.AddAttackSpeedPenaltyPercent(attackSpeedPenaltyPercent);
    }
}
