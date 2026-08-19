using PurrNet.Prediction;
using UnityEngine;

// On ANYTHING that calls PlayerControllerPD.NotifyDashStarted — a real Dash, RogueSpinAbilityPD's leap,
// BWBlinkAbilityPD's teleport, any future ability-driven movement that fires the same event — rolls Luck via
// the same WeaponDamageMath.RollLuckMultiplier shape every other luck-influenced roll in this codebase uses
// (against Stats.LuckChance/LuckFloor/LuckCeiling/Highroll), from GameRandom since this runs from
// Simulate()-context (NotifyDashStarted always fires synchronously from within some Simulate()-context
// caller). If the roll lands "positive" (> 1f — since LuckCeiling is never designed to go below 1f, a
// Highroll is always itself a positive roll too, see project_luckfloor_luckceiling_bounds), reduces the dash
// charge regen timer by reductionPercent (default 30%) of baseDashCooldown; if that same roll ALSO
// highrolls, the reduction is reductionPercent * baseDashCooldown * Stats.HighrollBonus instead (the same
// damage-scope Highroll Bonus stat everything else reads, unscaled — at base values (3f) that's 30% *
// baseDashCooldown * 3). Tuned down twice now (100% -> 50% -> 30%, preventative balance passes) — reads as
// overtuned once combined with certain other dash-cooldown-reduction augments.
//
// highrollBonus passed as 0f to RollLuckMultiplier itself — only used here to decide WHETHER a highroll
// happened (isHighroll) and to clamp the roll's own magnitude to LuckCeiling on one; the actual highroll
// REWARD (Stats.HighrollBonus) is applied separately below, not through RollLuckMultiplier's own internal
// "+bonus on top" step.
//
// Can overflow into granting multiple dash charges at once — see PlayerControllerPD.ReduceDashCooldown's own
// doc comment for the cascading "grant a charge, carry the remainder into the next charge's timer, repeat"
// shape.
public class OnDashStartedRollLuckForReducedCDAugment : AugmentEffectPD<OnDashStartedRollLuckForReducedCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {reductionPercent} directly in the description text — see its own doc comment (only public
    // fields/properties/methods are checked). Remember the *100-on-Percent-suffix convention: {reductionPercent}
    // in the description text shows as 30, not 0.3.
    public float reductionPercent = 0.3f;

    private PlayerControllerPD _controller;

    // Normal (non-highroll) reduction — a flat reductionPercent * baseDashCooldown, so this token is fully
    // accurate any time it's shown (no per-roll variance to approximate, unlike a damage preview).
    public string LiveCooldownReduction(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.baseDashCooldown * reductionPercent)} "
            : "0 ";

    // Highroll reduction — reductionPercent * baseDashCooldown * Stats.HighrollBonus, live off the real
    // current stat.
    public string LiveHighrollCooldownReduction(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(stats.baseDashCooldown * reductionPercent * stats.HighrollBonus)} "
            : "0 ";

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null) _controller.AddDashStartedListener(OnDashStarted);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashStartedListener(OnDashStarted);
    }

    private void OnDashStarted()
    {
        if (_controller == null || Stats == null || Stats.IsDead) return;

        float roll = WeaponDamageMath.RollLuckMultiplier(Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, 0f, GameRandom.instance.NextFloat, out bool isHighroll);
        if (roll <= 1f) return;

        float reduction = Stats.baseDashCooldown * reductionPercent * (isHighroll ? Stats.HighrollBonus : 1f);
        if (reduction <= 0f) return;

        _controller.ReduceDashCooldown(reduction);
    }
}
