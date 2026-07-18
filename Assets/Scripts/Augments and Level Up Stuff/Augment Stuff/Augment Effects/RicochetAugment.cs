using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/RicochetPlus1Augment.cs (the x2 variant is deliberately
// not ported), minus the dropoff-reduction half — projectileDamageDropoffReduction/
// projectileOnHitDropoffReduction are left untouched. WeaponRangedProjectilePD already reads
// Stats.Ricochet directly, so this is a pure stat grant with no other wiring needed.
public class RicochetAugment : AugmentEffectPD<RicochetAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusRicochet = 1f;

    protected override void OnApply()
    {
        Stats.AddRicochetFlat(bonusRicochet);
    }
}
