using PurrNet.Prediction;
using UnityEngine;

// Not a port. Turns on onHitCanCrit (off by default, see CharacterStatsPD.onHitCanCrit) — no current
// onhit-damage-dealing augment rolls its own crit yet, this is groundwork for future ones that will check
// this flag before rolling their own crit chance/multiplier. Also grants +10% crit chance, same
// AddCritChanceFlat convention as PhantomBladeOnCritAugment/CritTriggerOnHitAugment.
public class OnHitCritAugment : AugmentEffectPD<OnHitCritAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float critChanceBonus = 0.1f;

    protected override void OnApply()
    {
        Stats.EnableOnHitCrit();
        Stats.AddCritChanceFlat(critChanceBonus);
    }
}
