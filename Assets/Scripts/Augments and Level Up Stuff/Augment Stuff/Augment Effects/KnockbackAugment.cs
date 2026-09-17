using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. Flat Total-tier percent bump to weapon knockback (Stats.Knockback, the "how hard
// does a weapon hit shove the struck enemy" stat every real weapon-hit site already reads —
// WeaponMeleePD/WeaponAxePD/WeaponRangedProjectilePD/RogueSpinAbilityPD/ExeAxeThrowProjectilePD) — same
// AddKnockbackPercent tier MultiShotSpreadAugment's own +50% already uses, additive against that stat's
// shared 1f baseline (e.g. picking both this and MultiShotSpreadAugment lands at 1 + 2 + 0.5 = 3.5x total,
// not 3x * 1.5x = 4.5x — matches the "bonuses sum additively" convention every other plain stat bonus in
// this codebase already follows).
//
// 2f (2026-09-12) = "+200%" — since knockbackMultiplier starts at its own neutral 1f baseline, +200% here
// means this TRIPLES whatever the player's current knockback already is (1 + 2 = 3), exactly matching the
// "3x" design intent when picked alone.
public class KnockbackAugment : AugmentEffectPD<KnockbackAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 1f = 100% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%). Public, not
    // [SerializeField] private — ends in "Percent," so AugmentDescriptionFormatter's *100 convention lets a
    // description reference {knockbackPercent} directly (prints "200") with no separate alias needed.
    public float knockbackPercent = 2f;

    protected override void OnApply()
    {
        Stats.AddKnockbackPercent(knockbackPercent);
    }
}
