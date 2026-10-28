using PurrNet.Prediction;

// Executioner AxeThrow-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities
// = [ExeAxeThrow] — scripts-only convention, that part is Editor-side, not here.
//
// Every enemy hit by a thrown axe heals the caster for healFlat + BonusHealthRegen * healRegenPercent
// (0.5 + 50% default) — same "healBase + BonusHealthRegen * percent" formula shape every other
// Health-Regen-scaled heal in this codebase uses — DOUBLED on a crit. Mini axes (ExeAxeThrowMiniAxesAugment,
// if also equipped) heal at exactly HALF this amount, a fixed ratio specified independently of
// whatever miniAxeDamagePercent/etc. that other augment happens to be tuned to — see
// ExeAxeThrowAbilityPD.Activate()'s own mini-axe spawn block, which halves healFlat/healRegenPercent before
// passing them into each mini axe's own Launch() call. Applied directly inside
// ExeAxeThrowProjectilePD.ProcessHit() (a one-shot, Stats.Heal() call) rather than a generic _onStrike/
// _onHit listener — WeaponHitData carries no "which ability/weapon fired this" tag, so there'd be no way
// for a generic listener to tell an AxeThrow hit apart from any other weapon's hit; ProcessHit() already
// knows it's specifically an axe-throw hit, so the heal lives right there instead.
public class ExeAxeThrowHealAugment : AugmentEffectPD<ExeAxeThrowHealAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {healFlat}/{healRegenPercent} directly as description tokens; only public fields/properties/methods
    // are checked. healRegenPercent ends in "Percent" so its auto-*100 formatting fires.
    public float healFlat = 0.5f;
    public float healRegenPercent = 0.5f;

    // Baseline heal-per-hit at 100% HealPower (the crit double/HealPower multiply itself isn't included
    // since both vary per-hit, not a stat to preview) for AugmentDescriptionFormatter's {LiveHealRatio}
    // token — same shape every other heal augment's own Live*Ratio uses.
    public string LiveHealRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(healFlat + stats.BonusHealthRegen * healRegenPercent)} "
            : "";

    protected override void OnApply()
    {
        var axeThrow = Stats.GetComponent<ExeAxeThrowAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ExeAxeThrowAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (axeThrow == null) return;

        axeThrow.healFlat += healFlat;
        axeThrow.healRegenPercent += healRegenPercent;
    }
}
