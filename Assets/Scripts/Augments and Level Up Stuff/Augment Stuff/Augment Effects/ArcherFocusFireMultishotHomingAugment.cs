using PurrNet.Prediction;

// Archer FocusFire-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities =
// [ArcherFocusFire] — scripts-only convention, that part is Editor-side, not here.
//
// Two effects applied directly to ArcherFocusFireAbilityPD at OnApply(), both scoped to only while Focus
// Fire's own active window is running (same shape as ArcherFocusFireRicochetAugment):
//
// 1. +multishotAmount Multishot (`multishotFlat`, Add-at-Activate/Remove-at-end pairing, same mechanism as
//    ricochetFlat).
// 2. Every projectile fired during the window homes in on Focus Fire's own closest-enemy target
//    (`homingProjectiles`/`homingDelay` Pattern B switches) — see WeaponRangedProjectilePD.State's own doc
//    comments for the full mechanism: each shot flies straight on its initial (multishot-fanned-out)
//    heading for homingDelay seconds, then re-aims toward the target every tick until it lands its first
//    real hit, at which point homing stops permanently and the existing ricochet system (unaffected by any
//    of this) takes over as normal. If the target dies mid-flight, the shot retargets to whatever's now the
//    closest living enemy in range; if NOTHING living is in range at all, it just keeps flying straight on
//    its last heading. A shot fired while nothing was in range to begin with never homes in the first place
//    — it's an ordinary straight shot, same as Focus Fire without this augment equipped.
public class ArcherFocusFireMultishotHomingAugment : AugmentEffectPD<ArcherFocusFireMultishotHomingAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {multishotAmount} directly as a description token; only public fields/properties/methods are checked.
    // No "Flat"/"Percent" suffix needed — Multishot is itself a flat bonus-shots count, not a percent, same
    // naming shape ArcherFocusFireRicochetAugment.ricochetAmount/RogueSpinChargeAugment.chargeAmount use.
    public float multishotAmount = 2f;

    // Seconds a fired shot keeps flying straight before homing kicks in — design framing: "like 0.1s so
    // they kinda fan out then home in." Public so it's description-token-resolvable too, though there's
    // no natural percent/ratio phrasing for it.
    public float homingDelay = 0.1f;

    protected override void OnApply()
    {
        var focusFire = Stats.GetComponent<ArcherFocusFireAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ArcherFocusFireAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (focusFire == null) return;

        focusFire.multishotFlat += multishotAmount;
        focusFire.homingProjectiles = true;
        focusFire.homingDelay = homingDelay;
    }
}
