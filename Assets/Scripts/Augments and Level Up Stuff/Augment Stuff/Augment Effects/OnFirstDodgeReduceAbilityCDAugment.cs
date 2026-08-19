using PurrNet.Prediction;
using UnityEngine;

// On First Dodge (FD — CharacterStatsPD.AddFirstDodgeListener, fires once per dash-movement's own activation
// window — see CharacterStatsPD.NotifyDodge's own doc comment for the exact 3-condition Dodge contract and
// why only the FIRST dodge of a given dash counts here, same SI/FSI-family "once per activation" shape),
// shaves time off both class abilities' remaining cooldowns — reductionPercent of EACH ability's own
// BaseCooldownDuration individually, same "per-ability base" shape OnDashStartedReduceAbilityCDAugment/
// OnAbilityStartReduceDashCDAugment/OnStrikeReduceAbilityCDAugment already use. Default 0.3 (30%).
//
// Reduces via IAbilityPD.ReduceCooldownFlat, not ReduceCooldownPercentRemaining/ReduceCooldownPercentTotal —
// the percent here is of the ability's fixed BASE cooldown (a flat seconds amount once resolved per ability),
// not of however much cooldown currently remains — same reasoning OnStrikeReduceAbilityCDAugment's own doc
// comment gives. Every ability implementer already clamps its own cooldownTimer at 0, so calling this on an
// ability that's already off cooldown is a harmless no-op, no extra check needed here first.
//
// Not a damage/restore amount, so — matching every other "reduce a cooldown" augment in this family — this
// deliberately does NOT roll luck variance.
public class OnFirstDodgeReduceAbilityCDAugment : AugmentEffectPD<OnFirstDodgeReduceAbilityCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {reductionPercent}/{LiveCooldownReduction} directly in the description text — see its own doc comment
    // (only public fields/properties/methods are checked).
    public float reductionPercent = 0.3f;

    // Cached once — Ability1/Ability2 are permanent one-per-character fixtures for the whole match, same
    // convention OnDashStartedReduceAbilityCDAugment/OnStrikeReduceAbilityCDAugment's own caches use.
    private IAbilityPD[] _abilities;

    // Shows reductionPercent of both class abilities' own BaseCooldownDuration, "Ability1|Ability2" — same
    // token shape/naming as OnDashStartedReduceAbilityCDAugment/OnAbilityStartReduceDashCDAugment's own
    // identical tokens, since each ability's own actual reduction depends on its own base cooldown.
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
        Stats.AddFirstDodgeListener(OnFirstDodge);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveFirstDodgeListener(OnFirstDodge);
    }

    private void OnFirstDodge()
    {
        if (_abilities == null || Stats == null || Stats.IsDead) return;
        foreach (var ability in _abilities)
        {
            float reduction = ability.BaseCooldownDuration * reductionPercent;
            if (reduction > 0f) ability.ReduceCooldownFlat(reduction);
        }
    }
}
