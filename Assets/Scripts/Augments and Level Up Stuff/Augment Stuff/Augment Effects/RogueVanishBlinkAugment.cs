using PurrNet.Prediction;

// Rogue Vanish-specific augment (2026-10-04). This augment's own .asset must set requiredAbilities =
// [RogueVanish] — scripts-only convention, that part is Editor-side, not here.
//
// Makes RogueVanishAbilityPD blink the caster to their cursor on cast, max range capped at
// PlayerControllerPD.DashRange (the same real-world distance — 20 units/sec * 0.35s = 7 units — a normal
// dash covers; both are private consts there, not augmentable, so this is a fixed cap). Also extends the
// duration of the invisibility grant itself by invisibilityDurationBonusPercent (50% default) — NOT the
// Move Speed buff or the ability's own active window, just the GrantInvisibility contribution, per the
// explicit "extends the duration of the invisibility" framing this was specified with.
//
// Also makes RogueVanishAbilityPD's own cooldown get reduced by Dash Haste, the SAME way Ability Haste
// already reduces it (2026-10-05, explicit request — "compared to the others it's just a small dash on a
// long cooldown," this gives it a second haste source to shrink that cooldown with), but only counting
// dashHasteToCooldownPercent of Dash Haste's full value (nerfed 2026-10-08 from a full 1:1 fold-in to 50%,
// explicit request) — see RogueVanishAbilityPD.EffectiveCooldown's own doc comment for the combined formula
// (the two haste sources add together inside one divide, not two separate divides multiplying together).
//
// Pattern B (see KnightBerserkMoveSpeedAugment's own doc comment) — all three no-op-by-default switches
// live directly on the ability (RogueVanishAbilityPD.blinkOnCast/invisibilityDurationBonusPercent/
// dashHasteContributionPercent), since the teleport has to happen synchronously inside Activate() itself
// (mirrors BWBlinkAbilityPD's own instant-teleport block) and the other two only touch formulas owned by
// this ability (GrantInvisibility's own contribution, EffectiveCooldown). This augment just flips all
// three in OnApply().
public class RogueVanishBlinkAugment : AugmentEffectPD<RogueVanishBlinkAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {invisibilityDurationBonusPercent} directly as a description token; only public fields/properties/
    // methods are checked. Still Inspector-serializable either way.
    public float invisibilityDurationBonusPercent = 0.5f;
    // "Percent" suffix at the END (not buried mid-name like dashHastePercentToCooldown would've been) so
    // AugmentDescriptionFormatter's auto-*100 formatting actually fires. 0.5 = only 50% of Dash Haste counts
    // toward this ability's cooldown reduction — nerfed 2026-10-08 from a full 1:1 fold-in (see
    // RogueVanishAbilityPD.dashHasteContributionPercent's own doc comment).
    public float dashHasteToCooldownPercent = 0.5f;

    protected override void OnApply()
    {
        var vanish = Stats.GetComponent<RogueVanishAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with RogueVanishAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (vanish == null) return;
        vanish.blinkOnCast = true;
        vanish.invisibilityDurationBonusPercent += invisibilityDurationBonusPercent;
        vanish.dashHasteContributionPercent = dashHasteToCooldownPercent;
    }
}
