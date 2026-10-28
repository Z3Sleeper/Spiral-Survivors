using PurrNet.Prediction;

// Knight BuffStun-specific augment (2026-10-02), first requiredAbilities example for KnightBuffStunAbilityPD
// (see KnightBerserkMoveSpeedAugment's own doc comment for the established patterns). This augment's own
// .asset must set requiredAbilities = [KnightBuffStun] — scripts-only convention, that part is Editor-side,
// not here.
//
// Decouples KnightBuffStunAbilityPD's recast trigger from its natural-end trigger, and adds a THIRD,
// automatic trigger on cast — up to 3 total AoE stuns per cast instead of 1:
//  1. On cast — fires immediately (KnightBuffStunAbilityPD.Activate()'s fresh-cast branch), ALWAYS.
//  2. On recast — same as the base ability's own recast-to-trigger-early, still entirely OPTIONAL and still
//     single-use per cast (use-it-or-lose-it — nothing special changes about recast's own availability).
//  3. On natural end — fires when the buff's duration runs out, ALWAYS, regardless of whether #2 (recast)
//     already happened this cast (the base ability's own behavior has recast CONSUME the natural-end
//     trigger, i.e. at most one of #2/#3 ever fires normally — this augment makes them independent).
// So: 2 guaranteed triggers (#1 and #3) plus 1 optional one (#2) if the player actually uses the recast —
// "if the player doesn't use the recast one nothing happens, only two stuns go off automatically" (design
// framing).
//
// Pattern B (see KnightBerserkMoveSpeedAugment) — the actual mechanism lives entirely on the ability itself
// (KnightBuffStunAbilityPD.extraStunsEnabled, a single no-op-by-default bool), including the TriggerAoE
// signature change (setsRecastGate) and the VFX counter fix (aoeVfxTriggerCount) needed so up to 3
// same-cast triggers each get their own shockwave VFX instead of a single bool edge-detector silently
// swallowing the 2nd/3rd one. This augment just flips that one switch in OnApply().
public class KnightBuffStunMoreStunsAugment : AugmentEffectPD<KnightBuffStunMoreStunsAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    protected override void OnApply()
    {
        var buffStun = Stats.GetComponent<KnightBuffStunAbilityPD>();
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with KnightBuffStunAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (buffStun == null) return;
        buffStun.extraStunsEnabled = true;
    }
}
