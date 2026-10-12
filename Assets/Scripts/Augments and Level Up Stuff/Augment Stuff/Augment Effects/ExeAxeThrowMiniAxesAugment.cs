using PurrNet.Prediction;

// Executioner AxeThrow-specific augment (2026-10-06). This augment's own .asset must set requiredAbilities
// = [ExeAxeThrow] — scripts-only convention, that part is Editor-side, not here.
//
// Throws 2 additional mini axes in a 90° fan on every cast, same angle-spread math WeaponRangedPD.Fire()
// uses for its own Multishot (MultishotMath.ComputeAngles) — just a fixed count/arc here, not the player's
// own Multishot stat. Each mini axe is 50% smaller, deals 50% less damage, inflicts 50% less knockback, and
// carries 50% less OnHitPower than the main axe — but travels the SAME full distance the main axe does
// (miniAxeRangePercent reworked 2026-10-12, was 0.5f/"50% less far": playtesting found the minis useless at
// their old half-range, only ever landing hits in a point-blank "shotgun" spread; arc widened 75°->90° at
// the same time for a bit more spread at that now-useful range). Sets fixed values directly on
// ExeAxeThrowAbilityPD's own augment-owned fields (miniAxeCount/miniAxeArcDegrees/the four percent fields)
// rather than live additive-stacking multipliers — there's only ever ONE mini-axe-granting augment, with
// nothing else to compose with (see [[project_augments_never_duplicate_granted]]), so a plain assignment
// here is enough. See ExeAxeThrowAbilityPD.Activate()'s own mini-axe spawn block for the actual throw
// logic — zero changes needed to ExeAxeThrowProjectilePD beyond the sizeMultiplier/onHitPowerPercent
// parameters ExeAxeThrowKnockbackAugment's own knockbackMultiplier threading already established the
// shape for.
public class ExeAxeThrowMiniAxesAugment : AugmentEffectPD<ExeAxeThrowMiniAxesAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve these
    // directly as description tokens; only public fields/properties/methods are checked. The four "Percent"
    // suffixed fields auto-*100 (0.5f -> "50%"); miniAxeArcDegrees is a raw angle, no suffix needed.
    public int   miniAxeCount = 2;
    public float miniAxeArcDegrees = 90f;
    public float miniAxeSizePercent = 0.5f;
    // 1f (full range, same as the main axe) — was 0.5f. See class doc comment for why.
    public float miniAxeRangePercent = 1f;
    public float miniAxeDamagePercent = 0.5f;
    public float miniAxeKnockbackPercent = 0.5f;
    public float miniAxeOnHitPowerPercent = 0.5f;

    protected override void OnApply()
    {
        var axeThrow = Stats.GetComponent<ExeAxeThrowAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with ExeAxeThrowAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (axeThrow == null) return;

        axeThrow.miniAxeCount = miniAxeCount;
        axeThrow.miniAxeArcDegrees = miniAxeArcDegrees;
        axeThrow.miniAxeSizePercent = miniAxeSizePercent;
        axeThrow.miniAxeRangePercent = miniAxeRangePercent;
        axeThrow.miniAxeDamagePercent = miniAxeDamagePercent;
        axeThrow.miniAxeKnockbackPercent = miniAxeKnockbackPercent;
        axeThrow.miniAxeOnHitPowerPercent = miniAxeOnHitPowerPercent;
    }
}
