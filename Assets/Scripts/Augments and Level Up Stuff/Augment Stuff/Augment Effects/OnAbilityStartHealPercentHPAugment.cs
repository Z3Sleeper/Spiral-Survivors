using PurrNet.Prediction;
using UnityEngine;

// Heals on every ability start — CharacterStatsPD._onAbilityStart fires the instant any ability's own
// AbilityIsActive flips true (see that event's own doc comment). Heal amount scales with which ability was
// cast: healPercentMaxHealthPerCooldownSecond * MaxHealth * that ability's own BaseCooldownDuration (the
// raw, un-hasted cooldown — see IAbilityPD.BaseCooldownDuration's own doc comment for why this and not the
// haste-scaled CooldownDuration, same convention PhantomBladeOnAbilityStartAugment already established for
// this exact event). A bigger cooldown investment heals for more. Heal() itself rolls luck variance and
// HealPower/CritHealPower now — this just passes the raw, un-rolled amount straight through.
public class OnAbilityStartHealPercentHPAugment : AugmentEffectPD<OnAbilityStartHealPercentHPAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.01f = 1% Max Health per second of BaseCooldownDuration.
    public float healPercentMaxHealthPerCooldownSecond = 0.01f;

    // No single number to preview (the actual heal depends on which of this character's two abilities was
    // cast) — shows both class abilities' own actual values instead, "Ability1/Ability2", for
    // AugmentDescriptionFormatter's {LiveHealthRatio} token.
    public string LiveHealthRatio(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            float value = stats.MaxHealth * healPercentMaxHealthPerCooldownSecond * ability.BaseCooldownDuration;
            if (ability.Slot == AbilitySlot.Ability1) ability1Value = value;
            else if (ability.Slot == AbilitySlot.Ability2) ability2Value = value;
        }
        // "|" not "/" — a slash between two live numbers reads as division at a glance; these are two
        // independent per-ability values (Ability1 | Ability2), not a fraction.
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

        Stats.Heal(Stats.MaxHealth * healPercentMaxHealthPerCooldownSecond * baseCooldown);
    }
}
