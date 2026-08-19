using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/MultiShotAugment.cs (the x2 variant is deliberately not
// ported). Used to hand-tune a damage/on-hit-power PENALTY to offset the extra shots this grants, then
// (once WeaponStrength/WeaponOnHitPower auto-divided by (1f + Multishot) themselves) a compensating +20%
// Strength/+20% OnHitPower BONUS instead — removed entirely now (2026-08-19): that compensation only ever
// needed to offset the weapon-strike divide, but since Strength/OnHitPower split into a separate undivided
// general stat (see CharacterStatsPD.WeaponStrength's own doc comment), granting it as a Total-percent
// bonus was silently over-buffing every non-weapon-strike consumer too (Phantom Blade, AoE Effects, on-hit
// procs). The divide living only on WeaponStrength/WeaponOnHitPower is itself the whole counterbalance now.
public class MultiShotAugment : AugmentEffectPD<MultiShotAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusMultishotFlat = 2f;

    protected override void OnApply()
    {
        Stats.AddMultishotFlat(bonusMultishotFlat);
    }
}
