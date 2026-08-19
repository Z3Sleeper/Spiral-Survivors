using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/MultiShotSpreadAugment.cs. Old system mutated
// multishotSpreadAngle in place (*= on Start, /= on OnDestroy) since augments there could be removed —
// this system's augments are permanent for the run, so the same effect is expressed as a proper
// multiplicative bonus layer instead (bonusMultishotSpreadPercent, read by
// CharacterStatsPD.MultishotSpreadAngle, consumed by WeaponRangedPD.Fire()). knockbackBonus default
// changed from old's +100% (knockbackMultiplier += 1f) to +50% per current spec.
public class MultiShotSpreadAugment : AugmentEffectPD<MultiShotSpreadAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusMultishotSpreadPercent = 0.8f;
    [SerializeField] private float knockbackMultiplier  = 1f;

    protected override void OnApply()
    {
        Stats.MultiplyMultishotSpreadPercent(1f - bonusMultishotSpreadPercent);
        Stats.AddKnockbackPercent(knockbackMultiplier);
    }
}
