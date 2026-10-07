using PurrNet.Prediction;
using UnityEngine;

// On ANYTHING that calls PlayerControllerPD.NotifyDashStarted — a real Dash, RogueSpinAbilityPD's leap,
// BWBlinkAbilityPD's teleport, any future ability-driven movement that fires the same event — rolls a
// Highroll check from GameRandom since this runs from Simulate()-context (NotifyDashStarted always fires
// synchronously from within some Simulate()-context caller).
//
// Reworked (2026-08-24) to drop the old "any positive roll" outcome entirely — only an actual Highroll does
// anything now, for a flat cooldownReduction (5s) off the dash charge regen timer, no longer scaled by
// reductionPercent/Stats.HighrollBonus at all. At base values that 5s exactly matches baseDashCooldown, so
// landing the highroll is effectively a free dash charge refund.
//
// DOUBLED highroll roll (2026-09-26, explicit user request, same "roll luck with doubled highroll chance"
// treatment DodgeOnHighrollAugment's own dodge-check got — see CharacterStatsPD.TakeDamage's doc comment for
// the shared "Min(base*2,1f)" shape). Was WeaponDamageMath.RollLuckMultiplier purely to get isHighroll out of
// it (its own multiplier result was always discarded here, never assigned to anything) — replaced with a
// direct Stats.HighrollChance roll, since that IS the exact probability RollLuckMultiplier's own isHighroll
// resolves to anyway (same derivation CharacterStatsPD.HighrollChance's own doc comment gives), just without
// needing to solve for an adjusted highroll THRESHOLD input to express "doubled" — a plain *2 on the
// probability itself is far more direct.
//
// Can overflow into granting multiple dash charges at once — see PlayerControllerPD.ReduceDashCooldown's own
// doc comment for the cascading "grant a charge, carry the remainder into the next charge's timer, repeat"
// shape.
//
// Also grants its own +2.5% Highroll Chance (Stats.AddBonusHighrollPercent — see that method's own doc
// comment for why this is the Bonus tier, not AddHighrollPenalty's Penalty tier, for an ordinary buff like
// this one) on top of the base trigger — same reasoning DodgeOnHighrollAugment's own identical +2.5% uses,
// this augment felt underpowered relying purely on whatever HighrollChance investment the player happened to
// already have.
public class OnDashStartedRollLuckForReducedCDAugment : AugmentEffectPD<OnDashStartedRollLuckForReducedCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {cooldownReduction} directly in the description text — flat and fixed, no live-stat scaling left to
    // compute (see class doc comment), so a plain field is all this needs now.
    public float cooldownReduction = 5f;

    // Public, not [SerializeField] private — same "AugmentDescriptionFormatter's reflection only checks
    // public members" reasoning every other description-referenced augment field in this codebase already
    // follows. Ends in "Percent" so the formatter's own *100 display convention applies directly.
    public float highrollChancePercent = 0.025f;

    private PlayerControllerPD _controller;

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null) _controller.AddDashStartedListener(OnDashStarted);
        Stats.AddBonusHighrollPercent(highrollChancePercent);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashStartedListener(OnDashStarted);
    }

    private void OnDashStarted()
    {
        if (_controller == null || Stats == null || Stats.IsDead) return;

        // See class doc comment for why this replaced WeaponDamageMath.RollLuckMultiplier and why *2 is safe
        // to apply directly to the probability.
        bool isHighroll = GameRandom.instance.NextFloat() < Mathf.Min(Stats.HighrollChance * 2f, 1f);
        if (!isHighroll) return;

        _controller.ReduceDashCooldown(cooldownReduction);

        // Dash Restore VFX opt-in (2026-09-26, user-curated, non-persistent dash-cooldown-reduction triggers
        // only) — safe unconditionally: this callback only ever runs once per real dash start, off
        // PlayerControllerPD._onDashStarted, a genuine PredictedEvent (see VFXManager.PlayDashRestoreVfx's
        // own per-target cooldown for the anti-spam side of this).
        VFXManager.instance?.PlayDashRestoreVfx(Stats.transform);
    }
}
