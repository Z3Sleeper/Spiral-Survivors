using PurrNet.Prediction;

// Rogue Vanish-specific augment (2026-10-04). This augment's own .asset must set requiredAbilities =
// [RogueVanish] — scripts-only convention, that part is Editor-side, not here.
//
// Doubles total Highroll CHANCE (NOT the raw Highroll threshold — see CharacterStatsPD.
// totalHighrollChancePercent's own doc comment for why those are very different things to multiply)
// while invisible from Vanish, via the new AddTotalHighrollChancePercent tier (2026-10-04, first-ever
// caller — added specifically for this augment). Also extends durationBonusPercent (50% default, bumped
// up from the original 25% — 2026-10-05, alongside RogueVanishHealAugment also granting 50% so all three
// Vanish duration-granting augments now match) — a real, permanent part of the WHOLE ability's duration
// (Move Speed buff + active window + invisibility, all one shared number now — renamed from
// invisibilityDurationBonusPercent, 2026-10-14 rework, see that field's own doc comment on
// RogueVanishAbilityPD) — same field RogueVanishBlinkAugment already uses, all three stack additively.
//
// Pattern B (see KnightBerserkMoveSpeedAugment's own doc comment) — both no-op-by-default switches live
// directly on the ability (RogueVanishAbilityPD.highrollChanceBonusPercent/durationBonusPercent), applied/
// reverted by that ability's OWN Activate()/Simulate() pairing so the bonus is only ever active for the
// actual invisibility window, per the design framing ("an augment-side bump up to a totalpercent field
// when the ability is casted and removed when it ends"). This augment just bumps both fields once in
// OnApply().
public class RogueVanishHighrollAugment : AugmentEffectPD<RogueVanishHighrollAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {highrollChancePercent}/{durationBonusPercent} directly as description tokens; only
    // public fields/properties/methods are checked. Still Inspector-serializable either way. 1f = +100%
    // (doubles whatever HighrollChance the player currently has) — this codebase's percent fields store
    // the fraction/delta, not the final multiplier.
    public float highrollChancePercent = 1f;
    public float durationBonusPercent = 0.5f;

    protected override void OnApply()
    {
        var vanish = Stats.GetComponent<RogueVanishAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with RogueVanishAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (vanish == null) return;
        vanish.highrollChanceBonusPercent += highrollChancePercent;
        vanish.durationBonusPercent += durationBonusPercent;
    }
}
