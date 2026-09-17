using PurrNet.Prediction;
using UnityEngine;

// On ANYTHING that calls PlayerControllerPD.NotifyDashStarted — a real Dash, RogueSpinAbilityPD's leap,
// BWBlinkAbilityPD's teleport, any future ability-driven movement that fires the same event — rolls Luck via
// the same WeaponDamageMath.RollLuckMultiplier shape every other luck-influenced roll in this codebase uses
// (against Stats.LuckChance/LuckFloor/LuckCeiling/Highroll), from GameRandom since this runs from
// Simulate()-context (NotifyDashStarted always fires synchronously from within some Simulate()-context
// caller).
//
// Reworked (2026-08-24) to drop the old "any positive roll" outcome entirely — only an actual Highroll does
// anything now, for a flat cooldownReduction (5s) off the dash charge regen timer, no longer scaled by
// reductionPercent/Stats.HighrollBonus at all. At base values that 5s exactly matches baseDashCooldown, so
// landing the highroll is effectively a free dash charge refund.
//
// highrollBonus passed as 0f to RollLuckMultiplier itself — only used here to decide WHETHER a highroll
// happened (isHighroll), same convention PhantomBladeOnHighrollAugment's own identical roll uses.
//
// Can overflow into granting multiple dash charges at once — see PlayerControllerPD.ReduceDashCooldown's own
// doc comment for the cascading "grant a charge, carry the remainder into the next charge's timer, repeat"
// shape.
//
// Also grants its own +3% Highroll Chance (Stats.AddBonusHighrollPercent — see that method's own doc comment
// for why this is the Bonus tier, not AddHighrollPenalty's Penalty tier, for an ordinary buff like this one)
// on top of the base trigger — same reasoning DodgeOnHighrollAugment's own identical +3% uses, this augment
// felt underpowered relying purely on whatever HighrollChance investment the player happened to already have.
public class OnDashStartedRollLuckForReducedCDAugment : AugmentEffectPD<OnDashStartedRollLuckForReducedCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {cooldownReduction} directly in the description text — flat and fixed, no live-stat scaling left to
    // compute (see class doc comment), so a plain field is all this needs now.
    public float cooldownReduction = 5f;

    [SerializeField] private float highrollChanceBonus = 0.03f;

    private PlayerControllerPD _controller;

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null) _controller.AddDashStartedListener(OnDashStarted);
        Stats.AddBonusHighrollPercent(highrollChanceBonus);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashStartedListener(OnDashStarted);
    }

    private void OnDashStarted()
    {
        if (_controller == null || Stats == null || Stats.IsDead) return;

        WeaponDamageMath.RollLuckMultiplier(Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, 0f, GameRandom.instance.NextFloat, out bool isHighroll);
        if (!isHighroll) return;

        _controller.ReduceDashCooldown(cooldownReduction);
    }
}
