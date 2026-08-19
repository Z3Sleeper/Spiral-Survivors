using PurrNet.Prediction;
using UnityEngine;

// Refills energy on every ability start — CharacterStatsPD._onAbilityStart fires the instant any ability's
// own AbilityIsActive flips true (see that event's own doc comment). Amount scales with which ability was
// cast: (BonusEnergyRegen * energyRegenPercentPerCooldownSecond + baseAmountPerCooldownSecond) times that
// ability's own BaseCooldownDuration (the raw, un-hasted cooldown — see IAbilityPD.BaseCooldownDuration's
// own doc comment for why this and not the haste-scaled CooldownDuration). BonusEnergyRegen (2026-08-19,
// was the full EnergyRegen) — "current minus base" (see its own doc comment on CharacterStatsPD), so this
// only scales off actual Energy Regen investment, not the character's own baseline. baseAmountPerCooldownSecond
// (2026-08-19) is a flat floor added to the per-second portion BEFORE the baseCooldown multiply — without
// it, a build with zero BonusEnergyRegen investment would get literally nothing from this augment, same
// "flat floor" fix pattern as OnHitDamageHealthAugment/HealthToHealthRegenAugment's own baseDamage/
// maxHealthGrant. Same convention OnAbilityStartHealHealthRegenAugment already uses for this exact event.
// Deliberately does NOT roll luck variance — the "everything that deals/restores an amount rolls luck" rule
// is specifically for damage and healing, not every restore effect; this one's a flat, predictable refill by
// design. GainEnergy itself already clamps at MaxEnergy, so no separate cap check needed here.
public class OnAbilityStartRefillEnergyAugment : AugmentEffectPD<OnAbilityStartRefillEnergyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 1f = 100% Bonus Energy Regen per second of BaseCooldownDuration.
    public float energyRegenPercentPerCooldownSecond = 0.5f;
    public float baseAmountPerCooldownSecond = 1f;

    // No single number to preview (the actual refill depends on which of this character's two abilities was
    // cast) — shows both class abilities' own actual values instead, "Ability1/Ability2", for
    // AugmentDescriptionFormatter's {LiveEnergyRegenRatio} token.
    public string LiveEnergyRegenRatio(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        float perSecond = stats.BonusEnergyRegen * energyRegenPercentPerCooldownSecond + baseAmountPerCooldownSecond;
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            float value = perSecond * ability.BaseCooldownDuration;
            if (ability.Slot == AbilitySlot.Ability1) ability1Value = value;
            else if (ability.Slot == AbilitySlot.Ability2) ability2Value = value;
        }
        // "|" not "/" — see OnAbilityStartHealHealthRegenAugment's identical LiveAbilityHealAmount for why.
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(ability1Value)}|{AugmentDescriptionFormatter.FormatLiveNumber(ability2Value)} ";
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

        float perSecond = Stats.BonusEnergyRegen * energyRegenPercentPerCooldownSecond + baseAmountPerCooldownSecond;
        Stats.GainEnergy(perSecond * baseCooldown);
    }
}
