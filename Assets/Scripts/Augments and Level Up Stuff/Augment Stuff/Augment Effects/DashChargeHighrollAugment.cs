using PurrNet.Prediction;
using UnityEngine;

// New (2026-10-14, infinite-dash balance pass — see PlayerControllerPD.ReduceDashCooldown's own doc comment
// for the four flat/on-event cooldown-reduction augments this pass removed). Listens to
// PlayerControllerPD's own _onDashChargeGained event (fires once per charge actually gained — natural regen,
// an immediate top-up when max charges increases, RefundDashCharge, and each charge granted inside
// ReduceDashCooldown's overflow loop) and rolls a plain HighrollChance check; a success refunds one more
// charge on top. PredictedEvent listener — safe to mutate/call directly (guaranteed exactly once per real
// charge gain, regardless of resimulation), same established shape every OnDodge/OnStrike/OnAbilityStart
// cooldown-reaction augment in this codebase already uses.
//
// Deliberately NOT doubled (unlike the old OnDashStartedRollLuckForReducedCDAugment's 2x HighrollChance) —
// explicit user request for a plain roll here, since this can already compound naturally off the charge it
// just granted (see below).
//
// No chaining (explicit user decision): the bonus charge this augment grants calls
// PlayerControllerPD.RefundDashCharge(notifyChargeGained: false), so it does NOT re-fire
// _onDashChargeGained and can't trigger a second roll off its own payout. Without this, a lucky roll could
// cascade into an unbounded burst of free charges in one tick — the exact "infinite dashing" shape this
// whole balance pass exists to remove.
//
// Also grants its own +2.5% Highroll Chance (Stats.AddBonusHighrollPercent — see LuckCeilingAugment's own
// doc comment for why this is the Bonus tier, not AddHighrollPenalty's Penalty tier, for an ordinary buff
// like this one) on top of the trigger above, same "small chunk of its own" convention
// DodgeOnHighrollAugment's identical highrollChancePercent grant already establishes.
public class DashChargeHighrollAugment : AugmentEffectPD<DashChargeHighrollAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {highrollChancePercent} directly as a description token; only public fields/properties/methods are
    // checked. Ends in the literal "Percent" suffix so the auto-*100 formatting fires.
    public float highrollChancePercent = 0.025f;

    private PlayerControllerPD _controller;

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null) _controller.AddDashChargeGainedListener(OnDashChargeGained);
        Stats.AddBonusHighrollPercent(highrollChancePercent);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashChargeGainedListener(OnDashChargeGained);
    }

    private void OnDashChargeGained()
    {
        if (_controller == null || Stats == null || Stats.IsDead) return;
        if (GameRandom.instance.NextFloat() >= Stats.HighrollChance) return;

        _controller.RefundDashCharge(notifyChargeGained: false);
        VFXManager.instance?.PlayDashRestoreVfx(Stats.transform);
    }
}
