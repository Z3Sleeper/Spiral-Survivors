using PurrNet.Prediction;

// Knight BuffStun-specific augment (2026-10-02), third requiredAbilities example for KnightBuffStunAbilityPD.
// This augment's own .asset must set requiredAbilities = [KnightBuffStun] — scripts-only convention, that
// part is Editor-side, not here.
//
// Adds +strengthPercent (20%) Total Strength and +knockbackPercent (200%) Knockback to the list of buffs
// KnightBuffStunAbilityPD grants during its active window, alongside the existing Max Health/Range/Size —
// same lifecycle (granted in Activate(), reverted in EndBuff(), snapshotted into _appliedStrengthPercent/
// _appliedKnockbackPercent at cast time so a live Inspector/augment change mid-buff can't desync the later
// revert). Sets two dormant, [HideInInspector] fields directly on the ability
// (KnightBuffStunAbilityPD.strengthPercent/knockbackPercent, both 0f/no-op until this augment sets them) —
// same Pattern B shape as KnightBerserkMoveSpeedAugment.
//
// Also correctly amplified by KnightBuffStunBuffStrengthAugment if the player has BOTH (20%/200% -> 30%/300%
// at that augment's default 1.5x) — see KnightBuffStunAbilityPD.Activate()'s own
// strengthPercent/knockbackPercent * buffStrengthMultiplier snapshot lines, and
// KnightBuffStunBuffStrengthAugment's own doc comment for why that multiplier is applied live by the
// ability itself rather than baked destructively into specific fields, so it correctly reaches these two
// fields regardless of which of the two augments was picked first.
public class KnightBuffStunStrengthKnockbackAugment : AugmentEffectPD<KnightBuffStunStrengthKnockbackAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {strengthPercent}/{knockbackPercent} directly as description tokens; only public fields/properties/
    // methods are checked. Still Inspector-serializable either way.
    public float strengthPercent  = 0.2f;
    public float knockbackPercent = 2f;

    protected override void OnApply()
    {
        var buffStun = Stats.GetComponent<KnightBuffStunAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with KnightBuffStunAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (buffStun == null) return;
        buffStun.strengthPercent  += strengthPercent;
        buffStun.knockbackPercent += knockbackPercent;
    }
}
