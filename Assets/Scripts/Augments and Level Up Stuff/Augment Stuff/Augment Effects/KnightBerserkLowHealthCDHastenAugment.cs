using PurrNet.Prediction;
using UnityEngine;

// Knight Berserk-specific augment (2026-10-02), fourth and final KnightBerserk augment. This augment's own
// .asset must set requiredAbilities = [KnightBerserk] — scripts-only convention, that part is Editor-side,
// not here.
//
// While below lowHealthThresholdPercent (50%) Max Health, KnightBerserkAbilityPD's own cooldown counts down
// twice as fast. Same shape as KnightBerserkMoveSpeedAugment (Pattern B — see that class's own doc comment):
// the actual check-and-mutate lives on the ability itself (KnightBerserkAbilityPD.lowHealthCooldownBonusMultiplier/
// lowHealthThreshold, both no-op defaults), checked directly in the ability's OWN Simulate() against its OWN
// cooldownTimer — a same-identity mutation plus a plain cross-identity READ of Stats.CurrentHealth/MaxHealth,
// which carries none of the double-firing risk a cross-identity WRITE from this augment's own Simulate()
// would. This augment just bumps those two ability-side fields once in OnApply() (named without the Percent
// suffix there — that field is [HideInInspector]/never shown as a description token, so the
// EndsWith("Percent") formatting trigger doesn't matter for it).
public class KnightBerserkLowHealthCDHastenAugment : AugmentEffectPD<KnightBerserkLowHealthCDHastenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, and named with the literal "Percent" suffix (not buried mid-name) — so
    // AugmentDescriptionFormatter's reflection can resolve {lowHealthThresholdPercent}/
    // {extraSpeedMultiplierPercent} directly as description tokens AND get its auto-*100 percent formatting,
    // which only fires on an EndsWith("Percent") check (same "PerUnit vs Percent" trap
    // OnHitDamageDistanceAugment's own perUnitDistancePercent rename fixed for the identical reason).
    public float lowHealthThresholdPercent = 0.5f;
    // Extra multiplier on top of the ability's normal 1x cooldown-tick rate — 1f (default) nets exactly
    // "twice as fast" overall while below lowHealthThresholdPercent.
    public float extraSpeedMultiplierPercent = 1f;
    // Flat, unconditional Max Energy grant (2026-10-02) — same shape as KnightBerserkMoveSpeedAugment's own
    // +25, tying this augment to Knight's energy-spending Berserk ability thematically too.
    public float bonusMaxEnergyFlat = 50f;

    protected override void OnApply()
    {
        var berserk = Stats.GetComponent<KnightBerserkAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with KnightBerserkAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (berserk != null)
        {
            berserk.lowHealthThreshold = lowHealthThresholdPercent;
            berserk.lowHealthCooldownBonusMultiplier += extraSpeedMultiplierPercent;
        }
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }
}
