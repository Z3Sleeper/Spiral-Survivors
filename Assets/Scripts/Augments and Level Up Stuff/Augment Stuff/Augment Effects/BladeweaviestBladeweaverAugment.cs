using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/BladeweaviestBladeweaverAugment.cs. Used to hand-tune a
// damage/on-hit-power PENALTY to offset the extra weapons this grants, then (once WeaponStrength/
// WeaponOnHitPower auto-divided by WeaponCount themselves) a compensating BONUS instead — removed entirely
// now (2026-08-19, same reasoning as WeaponCountAugment/MultiShotAugment): that compensation only ever
// needed to offset the weapon-strike divide, but since Strength/OnHitPower split into a separate undivided
// general stat (see CharacterStatsPD.WeaponStrength's own doc comment), granting it as a Total-percent
// bonus was silently over-buffing every non-weapon-strike consumer too. The divide living only on
// WeaponStrength/WeaponOnHitPower is itself the whole counterbalance now.
public class BladeweaviestBladeweaverAugment : AugmentEffectPD<BladeweaviestBladeweaverAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private int bonusWeaponCountFlat = 3;

    protected override void OnApply()
    {
        Stats.AddWeaponCountFlat(bonusWeaponCountFlat);
    }
}
