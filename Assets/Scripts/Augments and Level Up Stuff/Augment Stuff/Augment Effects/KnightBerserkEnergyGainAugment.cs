using PurrNet.Prediction;

// Knight Berserk-specific augment (2026-10-03), fourth and final KnightBerserk augment. This augment's own
// .asset must set requiredAbilities = [KnightBerserk] — scripts-only convention, that part is Editor-side,
// not here.
//
// Very similar to the universal EnergyGainAugment (+100% Energy Gain, permanent, every source) — this one
// grants the SAME +100% Energy Gain buff, but ONLY while KnightBerserkAbilityPD's own Berserk window is
// active (Pattern B, same shape as KnightBerserkMoveSpeedAugment: a no-op-by-default field on the ability,
// KnightBerserkAbilityPD.energyGainPercent, applied/reverted via its own Activate()/EndBuff() pairing).
// Deliberately routes through the exact same CharacterStatsPD.AddEnergyGainPercent mutator EnergyGainAugment
// itself calls (both += into the same bonusEnergyGainPercent field, 1f baseline) rather than a separate
// Berserk-only stat — so picking BOTH correctly stacks additively to +200% while Berserk is active
// (1f baseline + 1f permanent + 1f conditional = 3x total energy gain), not a redundant/non-stacking
// duplicate. Also grants +50 Max Energy, permanently (same flat shop-style grant every other KnightBerserk
// augment has included).
public class KnightBerserkEnergyGainAugment : AugmentEffectPD<KnightBerserkEnergyGainAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {energyGainPercent}/{bonusMaxEnergyFlat} directly as description tokens; only public fields/properties/
    // methods are checked. Still Inspector-serializable either way. 1f = 100% (this codebase's percent
    // fields store the fraction), same convention EnergyGainAugment's own field uses.
    public float energyGainPercent = 1f;
    public float bonusMaxEnergyFlat     = 50f;

    protected override void OnApply()
    {
        var berserk = Stats.GetComponent<KnightBerserkAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with KnightBerserkAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (berserk != null) berserk.energyGainPercent += energyGainPercent;
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }
}
