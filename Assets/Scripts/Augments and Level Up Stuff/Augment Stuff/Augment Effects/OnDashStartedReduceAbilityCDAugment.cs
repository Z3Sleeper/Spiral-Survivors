using PurrNet.Prediction;
using UnityEngine;

// Shaves time off both class abilities' remaining cooldowns every time a dash (or Rogue's leap — see
// DashThroughInterruptAugment's own doc comment on why OnDashStarted/NotifyDashStarted covers both) starts.
//
// reductionPercent (2026-08-21, reverted from a flat flatCooldownReduction seconds constant back to a
// percent-of-base) — now a percent of EACH ability's own BaseCooldownDuration individually, same "per-ability
// base" shape OnAbilityStartReduceDashCDAugment/OnStrikeReduceAbilityCDAugment/
// OnAbilityStartReduceOtherAbilityCDAugment already use, rather than one flat amount applied identically to
// both abilities regardless of how differently they're tuned. Default 0.15 (15%) of each ability's own base.
//
// Reduces via IAbilityPD.ReduceCooldownFlat, not ReduceCooldownPercentRemaining/ReduceCooldownPercentTotal —
// the percent here is of the ability's fixed BASE cooldown (a flat seconds amount once resolved per ability),
// not of however much cooldown currently remains — same reasoning OnStrikeReduceAbilityCDAugment's own doc
// comment gives. Every ability implementer already clamps its own cooldownTimer at 0, so calling this on an
// ability that's already off cooldown is a harmless no-op, no extra check needed here first.
//
// Not a damage/restore amount, so — matching OnStrikeReduceAbilityCDAugment/
// OnAbilityStartReduceOtherAbilityCDAugment's own identical reasoning — this deliberately does NOT roll luck
// variance.
public class OnDashStartedReduceAbilityCDAugment : AugmentEffectPD<OnDashStartedReduceAbilityCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {reductionPercent}/{LiveCooldownReduction} directly in the description text — see its own doc comment
    // (only public fields/properties/methods are checked).
    public float reductionPercent = 0.15f;

    // Cached once — Ability1/Ability2 are permanent one-per-character fixtures for the whole match, same
    // convention OnStrikeReduceAbilityCDAugment/OnAbilityStartReduceOtherAbilityCDAugment's own caches use.
    private IAbilityPD[] _abilities;
    private PlayerControllerPD _controller;

    // Shows reductionPercent of both class abilities' own BaseCooldownDuration, "Ability1|Ability2" — same
    // token shape/naming as OnAbilityStartReduceDashCDAugment/OnStrikeReduceAbilityCDAugment's own identical
    // tokens, since each ability's own actual reduction now depends on its own base cooldown.
    public string LiveCooldownReduction(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            if (ability.Slot == AbilitySlot.Ability1) ability1Value = ability.BaseCooldownDuration * reductionPercent;
            else if (ability.Slot == AbilitySlot.Ability2) ability2Value = ability.BaseCooldownDuration * reductionPercent;
        }
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(ability1Value)}|{AugmentDescriptionFormatter.FormatLiveNumber(ability2Value)} ";
    }

    protected override void OnApply()
    {
        _abilities = Stats.GetComponents<IAbilityPD>();
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
        if (_abilities == null || Stats == null || Stats.IsDead) return;
        foreach (var ability in _abilities)
        {
            float reduction = ability.BaseCooldownDuration * reductionPercent;
            if (reduction > 0f) ability.ReduceCooldownFlat(reduction);
        }
    }
}
