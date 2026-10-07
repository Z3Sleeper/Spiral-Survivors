using PurrNet.Prediction;
using UnityEngine;

// Knight Berserk-specific augment (2026-10-02), second real use of the AbilityIdentity/requiredAbilities
// system (see RogueSpinChargeAugment's own doc comment for the first). This augment's own .asset must set
// requiredAbilities = [KnightBerserk] so it's only ever offered to a player who actually has
// KnightBerserkAbilityPD equipped — scripts-only convention, that part is Editor-side, not here.
//
// Grants +moveSpeedPercent Move Speed, but ONLY while KnightBerserkAbilityPD's own Berserk window is
// active — deliberately NOT a CharacterStatsPD stat (which would apply permanently), so the actual
// no-op-by-default field and its Activate()/EndBuff() application live on the ability itself
// (KnightBerserkAbilityPD.moveSpeedPercent — see its own doc comment). This augment just bumps that field.
//
// ALSO grants +maxEnergyFlat Max Energy, permanently (2026-10-02, explicit request) — unlike the Move Speed
// half above, this one IS a genuine always-on CharacterStatsPD stat (AddMaxEnergyFlat, same generic
// permanent-grant pattern RogueSpinChargeAugment's AddAbilityCharges call uses), tying this augment to
// Knight's energy-spending Berserk ability thematically. The Move Speed buff alone is a short-duration,
// long-cooldown conditional (see KnightBerserkAbilityPD's own cooldown/isActive window), so this half is
// what makes the augment worth picking outside those windows.
public class KnightBerserkMoveSpeedAugment : AugmentEffectPD<KnightBerserkMoveSpeedAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {moveSpeedPercent}/{maxEnergyFlat} directly as description tokens; only public fields/properties/
    // methods are checked. Still Inspector-serializable either way.
    public float moveSpeedPercent = 0.5f;
    public float maxEnergyFlat = 25f;

    protected override void OnApply()
    {
        var berserk = Stats.GetComponent<KnightBerserkAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with KnightBerserkAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (berserk != null) berserk.moveSpeedPercent += moveSpeedPercent;
        Stats.AddMaxEnergyFlat(maxEnergyFlat);
    }
}
