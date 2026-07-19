using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/MultiShotAugment.cs (the x2 variant is deliberately not
// ported). Used to hand-tune a damage/on-hit-power PENALTY to offset the extra shots this grants — now
// that WeaponDamage/OnHitPower both auto-divide by (1f + Multishot) themselves, that offset is structural,
// so this instead grants a straight BONUS on top (default +20% each).
public class MultiShotAugment : AugmentEffectPD<MultiShotAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusShots      = 2f;
    [SerializeField] private float damageBonus     = 0.2f;
    [SerializeField] private float onHitPowerBonus = 0.2f;

    protected override void OnApply()
    {
        Stats.AddMultishotFlat(bonusShots);
        Stats.AddWeaponDamagePercent(damageBonus);
        Stats.MultiplyOnHitPowerMultiplier(1f + onHitPowerBonus);
    }
}
