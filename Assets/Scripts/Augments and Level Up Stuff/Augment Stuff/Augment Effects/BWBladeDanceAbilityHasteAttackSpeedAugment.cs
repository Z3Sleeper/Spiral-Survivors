using PurrNet.Prediction;

// Bladeweaver BladeDance-specific augment (2026-10-06). This augment's own .asset must set
// requiredAbilities = [BWBladeDance] — scripts-only convention, that part is Editor-side, not here.
//
// Restores a mechanic that used to be hardcoded, always-on base-form behavior on BWBladeDanceAbilityPD
// before Stuart pulled it out into its own augment (Bladeweaver's abilities otherwise "don't do too much"
// on their own): while BladeDance's Outbound->Hold->Return window is active, every point of
// EffectiveAbilityHaste converts directly into attackSpeedPercentPerHaste Attack Speed (ADDED via
// AddAttackSpeedFlat) — AbilityHaste normally only ever shortens BladeDance's own already-low cooldown, so
// this gives it a second, more meaningful use for this specific ability. Pattern B — the actual grant/
// revert pairing lives on the ability itself (BWBladeDanceAbilityPD.attackSpeedPercentPerHaste, applied in
// Activate()/reverted in EndDance() — see that field's own doc comment).
public class BWBladeDanceAbilityHasteAttackSpeedAugment : AugmentEffectPD<BWBladeDanceAbilityHasteAttackSpeedAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {AttackSpeedPerHastePercent} as a description token; only public fields/properties/methods are
    // checked. attackSpeedPercentPerHaste itself doesn't END in "Percent" (it's in the middle), so a direct
    // {attackSpeedPercentPerHaste} reference wouldn't get the auto-*100 treatment — same alias-property fix
    // KnightBuffStunAbilityPD.BaseHealPercent/ArcherArrowRainAbilityPD.BaseDamagePercent already use.
    public float attackSpeedPercentPerHaste = 0.01f;
    public float AttackSpeedPerHastePercent => attackSpeedPercentPerHaste;

    // Live preview of what this augment would actually grant at the player's CURRENT EffectiveAbilityHaste
    // — same "preview the candidate value, not what's already applied" shape every other augment's own
    // Live*Ratio uses. Falls back to 0 outside a live match — EffectiveAbilityHaste's own neutral
    // (no-investment) value is 0, and 0 haste genuinely means 0% bonus, so this isn't a misleading
    // placeholder the way it would be for a stat with no true "neutral".
    public string LiveAttackSpeedBonusRatio(CharacterStatsPD stats)
    {
        float value = AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? stats.EffectiveAbilityHaste * attackSpeedPercentPerHaste
            : 0f;
        return $"{AugmentDescriptionFormatter.FormatLivePercent(value)}";
    }

    protected override void OnApply()
    {
        var bladeDance = Stats.GetComponent<BWBladeDanceAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with BWBladeDanceAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (bladeDance == null) return;
        bladeDance.attackSpeedPercentPerHaste += attackSpeedPercentPerHaste;
    }
}
