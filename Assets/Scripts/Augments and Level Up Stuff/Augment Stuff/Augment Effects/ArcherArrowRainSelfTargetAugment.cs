using PurrNet.Prediction;

// Archer ArrowRain-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [ArcherArrowRain] — scripts-only convention, that part is Editor-side, not here.
//
// Pattern B switch: sets ArcherArrowRainAbilityPD.castOnSelf true, which makes Activate() fire a second,
// fully independent rain cast centered directly on the caster, on top of the normal cursor-targeted one —
// see castOnSelf's own doc comment for the exact mechanism and the one known, already-accepted tradeoff
// (ArcherArrowRainGrowthAugment's own per-tick growth only ever tracks ONE chain at a time, so with both
// augments equipped only the self-cast zone — the one SpawnChain() call made second — actually grows; the
// cursor-targeted zone stays at its cast-time radius). No separate damage/duration/slow bonus — this
// augment's whole value is the free second zone itself.
public class ArcherArrowRainSelfTargetAugment : AugmentEffectPD<ArcherArrowRainSelfTargetAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    protected override void OnApply()
    {
        var arrowRain = Stats.GetComponent<ArcherArrowRainAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ArcherArrowRainAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (arrowRain == null) return;

        arrowRain.castOnSelf = true;
    }
}
