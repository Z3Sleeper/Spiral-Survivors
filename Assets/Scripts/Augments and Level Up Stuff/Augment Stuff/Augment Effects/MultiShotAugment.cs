using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/MultiShotAugment.cs (the x2 variant is deliberately not
// ported). Grants extra shots per volley at a damage/on-hit-power tradeoff — WeaponRangedPD.Fire() already
// reads Stats.Multishot/bonusWeaponDamagePenalty/bonusOnHitPowerPenalty directly, so this is a pure stat
// grant with no other wiring needed.
public class MultiShotAugment : AugmentEffectPD<MultiShotAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusShots    = 2f;
    [SerializeField] private float damagePenalty = 0.6f;
    [SerializeField] private float onHitPenalty  = 0.3f;

    protected override void OnApply()
    {
        Stats.AddMultishotFlat(bonusShots);
        Stats.MultiplyWeaponDamagePenalty(1f - damagePenalty);
        Stats.MultiplyOnHitPowerPenalty(1f - onHitPenalty);
    }
}
