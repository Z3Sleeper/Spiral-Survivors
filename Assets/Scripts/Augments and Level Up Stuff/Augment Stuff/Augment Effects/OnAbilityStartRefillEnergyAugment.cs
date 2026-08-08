using PurrNet.Prediction;
using UnityEngine;

// Refills energy on every ability start — CharacterStatsPD._onAbilityStart fires the instant any ability's
// own AbilityIsActive flips true (see that event's own doc comment). Amount scales with which ability was
// cast: 100% Energy Regen times that ability's own BaseCooldownDuration (the raw, un-hasted cooldown — see
// IAbilityPD.BaseCooldownDuration's own doc comment for why this and not the haste-scaled CooldownDuration),
// same convention OnAbilityStartHealPercentHPAugment already uses for this exact event. Deliberately does
// NOT roll luck variance — the "everything that deals/restores an amount rolls luck" rule is specifically
// for damage and healing, not every restore effect; this one's a flat, predictable refill by design.
// GainEnergy itself already clamps at MaxEnergy, so no separate cap check needed here.
public class OnAbilityStartRefillEnergyAugment : AugmentEffectPD<OnAbilityStartRefillEnergyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 1f = 100% Energy Regen per second of BaseCooldownDuration.
    public float energyRegenPercentPerCooldownSecond = 1f;

    // No single number to preview (the actual refill depends on which of this character's two abilities was
    // cast) — shows both class abilities' own actual values instead, "Ability1/Ability2", for
    // AugmentDescriptionFormatter's {LiveEnergyRegenRatio} token.
    public string LiveEnergyRegenRatio(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            float value = stats.EnergyRegen * energyRegenPercentPerCooldownSecond * ability.BaseCooldownDuration;
            if (ability.Slot == AbilitySlot.Ability1) ability1Value = value;
            else if (ability.Slot == AbilitySlot.Ability2) ability2Value = value;
        }
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(ability1Value)}/{AugmentDescriptionFormatter.FormatLiveNumber(ability2Value)} ";
    }

    protected override void OnApply() => Stats.AddAbilityStartListener(OnAbilityStart);

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAbilityStartListener(OnAbilityStart);
    }

    private void OnAbilityStart(IAbilityPD ability)
    {
        if (Stats == null || Stats.IsDead) return;

        float baseCooldown = ability.BaseCooldownDuration;
        if (baseCooldown <= 0f) return;

        Stats.GainEnergy(Stats.EnergyRegen * energyRegenPercentPerCooldownSecond * baseCooldown);
    }
}
