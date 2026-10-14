using PurrNet.Prediction;

// New Dash/Surge synergy augment (2026-10-12) — the two have been largely mutually exclusive until now
// (Surge only ever touches Move Speed/Attack Speed, neither of which interacts with Dash at all). Hastens
// Dash's OWN charge-regen tick rate while actively Surging — same Pattern B shape as
// KnightBerserkLowHealthCDRateAugment (see its own class doc comment): the actual check-and-mutate lives on
// PlayerControllerPD itself (dashCooldownRateBonus, a no-op-by-default field), checked directly in that
// component's own Simulate() against its own chargeRegenTimer, gated on a plain cross-identity READ of
// CharacterStatsPD.IsSurging. This augment just sets that one field once in OnApply().
//
// Deliberately NOT a CharacterStatsPD stat, and deliberately NOT folded into CharacterStatsPD.DashCooldown's
// own target-duration formula — this multiplies the per-tick RATE the charge-regen timer counts down at
// (chargeRegenTimer -= delta * rate), not the total duration a fresh charge starts at. Same distinction
// KnightBerserkLowHealthCDRateAugment's own cooldownRate multiply already established for Berserk.
public class SurgeDashCDRateAugment : AugmentEffectPD<SurgeDashCDRateAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, ending in "Percent" so AugmentDescriptionFormatter's reflection can resolve
    // {dashCooldownRatePercent} directly as a live, auto-*100 description token (1f -> "100%"). Extra
    // multiplier added on top of the normal 1x tick rate while Surging — 1f (default) nets exactly "twice
    // as fast" overall, same convention KnightBerserkLowHealthCDRateAugment's own berserkCooldownRatePercent
    // uses.
    public float dashCooldownRatePercent = 1f;
    // Flat, unconditional Max Energy grant — same shape as KnightBerserkLowHealthCDRateAugment's own
    // bonusMaxEnergyFlat, tying this augment to Surge's own energy-spending cost thematically too.
    public float bonusMaxEnergyFlat = 10f;

    protected override void OnApply()
    {
        var controller = Stats.GetComponent<PlayerControllerPD>();
        // Should never be null — every player has a PlayerControllerPD — but guarded anyway, same defensive
        // convention every other GetComponent lookup in this codebase already follows.
        if (controller != null) controller.dashCooldownRateBonus += dashCooldownRatePercent;
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }
}
