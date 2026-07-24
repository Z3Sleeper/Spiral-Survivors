using PurrNet.Prediction;
using UnityEngine;

// Simplest member of the Phantom Blade family — a pure "+damage%, +size%" grant, rather than reaching into
// already-summoned blades and modifying them on the fly like old PhantomBladeSizeAugment did. Every blade
// already reads the combined total once at spawn (PhantomBladeSummonHelper.SummonAtTarget), so a flat
// one-shot grant here applies to every future summon from every trigger (crit, passive, dash) with no
// per-blade bookkeeping needed. Both bonuses deliberately go into the "Percent"-suffixed multiplicative
// layer (phantomBladeDamageBonusPercent/phantomBladeSizeBonusPercent), SEPARATE from the additive
// phantomBladeDamageBonus/phantomBladeSizeBonus pools every other Phantom Blade augment contributes to —
// this augment's own bonuses are meant to scale the blade's already-combined additive totals (bigger
// blade = proportionally more damage on top of everything else), not just join those same additive pools.
// See CharacterStatsPD.phantomBladeDamageBonusPercent's own doc comment.
public class PhantomBladeSizeAugment : AugmentEffectPD<PhantomBladeSizeAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float phantomBladeDamageBonusPercent = 0.5f;
    [SerializeField] private float phantomBladeSizeBonusPercent   = 1f;

    protected override void OnApply()
    {
        Stats.AddPhantomBladeDamageBonusPercent(phantomBladeDamageBonusPercent);
        Stats.AddPhantomBladeSizeBonusPercent(phantomBladeSizeBonusPercent);
    }
}
