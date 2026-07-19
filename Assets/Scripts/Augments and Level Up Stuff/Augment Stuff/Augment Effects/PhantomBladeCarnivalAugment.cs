using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port — old system's Carnival used the same global static OnSummon event pitfall flagged
// in PhantomBladeOnCritAugment's own doc comment. Rebuilt on the per-player _onPhantomBladeSummon event
// (CharacterStatsPD) instead: fires once per REAL Phantom Blade summon trigger (crit, passive, dash,
// onhit) with the fully-resolved damage/size/crit status already baked in, so this augment spawns an exact
// duplicate (including whether the original crit) at a fresh random offset without re-rolling anything.
// Recursion is structurally impossible, not just guarded — SummonCopyAtTarget never calls back into
// SummonAtTarget, so the copy this augment spawns has no path back into _onPhantomBladeSummon at all,
// regardless of how many summon-reacting augments end up subscribed.
public class PhantomBladeCarnivalAugment : AugmentEffectPD<PhantomBladeCarnivalAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float damagePenalty = 0.35f;
    [SerializeField] private float sizePenalty   = 0.35f;

    protected override void OnApply()
    {
        Stats.MultiplyPhantomBladeDamagePenalty(1f - damagePenalty);
        Stats.MultiplyPhantomBladeSizePenalty(1f - sizePenalty);
        Stats.AddPhantomBladeSummonListener(OnPhantomBladeSummon);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemovePhantomBladeSummonListener(OnPhantomBladeSummon);
    }

    private void OnPhantomBladeSummon(PhantomBladeSummonData data)
    {
        if (phantomBladePrefab == null) return;
        if (!data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy) || enemy._isDead) return;

        PhantomBladeSummonHelper.SummonCopyAtTarget(hierarchy, phantomBladePrefab, Stats, enemy, data.damage, data.size, data.isCrit);
    }
}
