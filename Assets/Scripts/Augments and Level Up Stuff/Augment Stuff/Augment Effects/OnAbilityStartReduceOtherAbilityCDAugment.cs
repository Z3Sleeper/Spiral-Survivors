using PurrNet.Prediction;
using UnityEngine;

// Reduces the OTHER ability's remaining cooldown by reductionPercent (default 50%) of the CASTED ability's
// own CURRENT cooldown (i.e. CooldownDuration — the live, ability-haste-scaled cooldown, NOT
// BaseCooldownDuration and NOT CooldownRemaining), every time either ability starts — same _onAbilityStart
// trigger OnAbilityStartRefillEnergyAugment/OnAbilityStartHealPercentHPAugment already use
// (CharacterStatsPD._onAbilityStart fires the instant any ability's own AbilityIsActive flips true). At the
// default 50%, casting a 6s-current-cooldown ability shaves 3 seconds off whatever the OTHER ability
// currently has left.
//
// Reduces via IAbilityPD.ReduceCooldownFlat, not a direct field write — every ability implementer already
// clamps its own cooldownTimer at 0 AND guards against reducing a cooldown that isn't actually ticking down
// yet (see each implementer's own IsCooldownActivelyTicking), so calling this on an already-ready OTHER
// ability, or one mid-active-window with its own cooldown still frozen, is a harmless no-op — no extra check
// needed here first.
//
// Not a damage/restore amount, so — matching OnStrikeReduceAbilityCDAugment's own identical reasoning — this
// deliberately does NOT roll luck variance.
public class OnAbilityStartReduceOtherAbilityCDAugment : AugmentEffectPD<OnAbilityStartReduceOtherAbilityCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float reductionPercent = 0.5f;

    // Cached once — Ability1/Ability2 are permanent one-per-character fixtures for the whole match, same
    // convention OnStrikeReduceAbilityCDAugment's own _abilities cache already uses.
    private IAbilityPD[] _abilities;

    // No single number to preview (the actual reduction depends on which ability was cast, applied to
    // whichever ability WASN'T) — shows reductionPercent of both class abilities' own live CooldownDuration
    // instead, "Ability1|Ability2", for AugmentDescriptionFormatter's {LiveCooldownReduction} token.
    // Ability1's own value IS what Ability2 gets reduced by (and vice versa).
    public string LiveCooldownReduction(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            if (ability.Slot == AbilitySlot.Ability1) ability1Value = ability.CooldownDuration * reductionPercent;
            else if (ability.Slot == AbilitySlot.Ability2) ability2Value = ability.CooldownDuration * reductionPercent;
        }
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(ability1Value)}|{AugmentDescriptionFormatter.FormatLiveNumber(ability2Value)} ";
    }

    protected override void OnApply()
    {
        _abilities = Stats.GetComponents<IAbilityPD>();
        Stats.AddAbilityStartListener(OnAbilityStart);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAbilityStartListener(OnAbilityStart);
    }

    private void OnAbilityStart(IAbilityPD ability)
    {
        if (_abilities == null || Stats == null || Stats.IsDead) return;

        float reduction = ability.CooldownDuration * reductionPercent;
        if (reduction <= 0f) return;

        foreach (var other in _abilities)
        {
            if (other.Slot == ability.Slot) continue;
            other.ReduceCooldownFlat(reduction);
        }
    }
}
