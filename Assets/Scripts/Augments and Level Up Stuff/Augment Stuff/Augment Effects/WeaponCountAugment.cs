using PurrNet.Prediction;
using UnityEngine;

// +1 Weapon Count (Stats.AddWeaponCountFlat). Used to also grant a compensating +20% Strength/+20%
// OnHitPower (2026-08-19 removed) — that compensation only ever offset WeaponStrength/WeaponOnHitPower's
// own auto-divide-by-WeaponCount, but since Strength/OnHitPower now split into a separate undivided
// general stat (see CharacterStatsPD.WeaponStrength's own doc comment), granting it as a Total-percent
// bonus was silently over-buffing every non-weapon-strike consumer of Strength/OnHitPower too (Phantom
// Blade, AoE Effects, on-hit procs), none of which were ever divided by WeaponCount in the first place. The
// divide living only on WeaponStrength/WeaponOnHitPower is itself the whole counterbalance now — no bonus
// needed on top.
public class WeaponCountAugment : AugmentEffectPD<WeaponCountAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private int bonusWeaponCountFlat = 1;

    protected override void OnApply()
    {
        Stats.AddWeaponCountFlat(bonusWeaponCountFlat);
    }
}
