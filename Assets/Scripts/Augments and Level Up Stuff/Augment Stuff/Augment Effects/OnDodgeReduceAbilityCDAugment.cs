using PurrNet.Prediction;
using UnityEngine;

// On Dodge (CharacterStatsPD.AddDodgeListener, fires once per dash-movement's own activation window — see
// CharacterStatsPD.NotifyDodge's own doc comment for the exact 3-condition contract; a dodge only ever fires
// once per dash, "first dodge" and "dodge" are the same thing since the 2026-09-13 rework), shaves time off
// both class abilities' remaining cooldowns — reductionPercent of EACH ability's own
// BaseCooldownDuration individually, same "per-ability base" shape OnDashStartedReduceAbilityCDAugment/
// OnHitReduceAbilityCDAugment already use. Default 0.25 (25%, nerfed 2026-08-31 from 0.45/45% after
// playtesting — that 45% itself was a 2026-08-23 buff from 30%).
//
// Reduces via IAbilityPD.ReduceCooldownFlat, not ReduceCooldownPercentRemaining/ReduceCooldownPercentTotal —
// the percent here is of the ability's fixed BASE cooldown (a flat seconds amount once resolved per ability),
// not of however much cooldown currently remains — same reasoning OnHitReduceAbilityCDAugment's own doc
// comment gives. Every ability implementer already clamps its own cooldownTimer at 0, so calling this on an
// ability that's already off cooldown is a harmless no-op, no extra check needed here first.
//
// Not a damage/restore amount, so — matching every other "reduce a cooldown" augment in this family — this
// deliberately does NOT roll luck variance.
public class OnDodgeReduceAbilityCDAugment : AugmentEffectPD<OnDodgeReduceAbilityCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {reductionPercent}/{LiveCooldownReduction} directly in the description text — see its own doc comment
    // (only public fields/properties/methods are checked).
    public float reductionPercent = 0.25f;

    // Cached once — Ability1/Ability2 are permanent one-per-character fixtures for the whole match, same
    // convention OnDashStartedReduceAbilityCDAugment/OnHitReduceAbilityCDAugment's own caches use.
    private IAbilityPD[] _abilities;

    // Shows reductionPercent of both class abilities' own BaseCooldownDuration, "Ability1|Ability2" — same
    // token shape/naming as OnDashStartedReduceAbilityCDAugment's own identical token, since each ability's
    // own actual reduction depends on its own base cooldown.
    public string LiveCooldownReduction(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "0|0";
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            if (ability.Slot == AbilitySlot.Ability1) ability1Value = ability.BaseCooldownDuration * reductionPercent;
            else if (ability.Slot == AbilitySlot.Ability2) ability2Value = ability.BaseCooldownDuration * reductionPercent;
        }
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(ability1Value)}|{AugmentDescriptionFormatter.FormatLiveNumber(ability2Value)}";
    }

    protected override void OnApply()
    {
        _abilities = Stats.GetComponents<IAbilityPD>();
        Stats.AddDodgeListener(OnDodge);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDodgeListener(OnDodge);
    }

    private void OnDodge()
    {
        if (_abilities == null || Stats == null || Stats.IsDead) return;
        foreach (var ability in _abilities)
        {
            float reduction = ability.BaseCooldownDuration * reductionPercent;
            if (reduction > 0f) ability.ReduceCooldownFlat(reduction);
        }
    }
}
