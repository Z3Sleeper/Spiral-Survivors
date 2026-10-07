using PurrNet.Prediction;
using UnityEngine;

// Renamed from OnAbilityStartHealPercentHPAugment (2026-08-20) — reworked away from Max Health ratios
// entirely, now scales with BonusHealthRegen instead. Heals on every ability start —
// CharacterStatsPD._onAbilityStart fires the instant any ability's own AbilityIsActive flips true (see that
// event's own doc comment). Heal amount scales with which ability was cast:
// (flatHealPerCooldownSecond + bonusHealthRegenPercent * BonusHealthRegen) * that ability's own
// BaseCooldownDuration (the raw, un-hasted cooldown — see IAbilityPD.BaseCooldownDuration's own doc comment
// for why this and not the haste-scaled CooldownDuration, same convention PhantomBladeOnAbilityStartAugment
// already established for this exact event). A bigger cooldown investment heals for more. BonusHealthRegen,
// not the full HealthRegen (see CharacterStatsPD.BonusHealthRegen's own doc comment — current minus base),
// per explicit design. Heal() itself scales by HealPower/CritHealPower now (no luck roll — heals don't roll
// luck) — this just passes the raw amount straight through.
public class OnAbilityStartHealHealthRegenAugment : AugmentEffectPD<OnAbilityStartHealHealthRegenAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Flat per second of BaseCooldownDuration — guarantees a non-zero heal even at zero HealthRegen
    // investment, same "flat floor" reasoning OnHitHealHealthRegenAugment.baseHeal already establishes.
    public float flatHealPerCooldownSecond = 1f;
    // 0.25f = 25% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float bonusHealthRegenPercent = 0.25f;

    // No single number to preview (the actual heal depends on which of this character's two abilities was
    // cast) — shows both class abilities' own actual values instead, "Ability1/Ability2", for
    // AugmentDescriptionFormatter's {LiveAbilityHealAmount} token.
    public string LiveAbilityHealAmount(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            float value = (flatHealPerCooldownSecond + bonusHealthRegenPercent * stats.BonusHealthRegen) * ability.BaseCooldownDuration;
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

        Stats.Heal((flatHealPerCooldownSecond + bonusHealthRegenPercent * Stats.BonusHealthRegen) * baseCooldown);

        // Heal VFX opt-in (2026-09-26, user-curated per-augment, non-persistent heals only) — safe
        // unconditionally: this callback only ever runs once per real ability start, off
        // CharacterStatsPD._onAbilityStart, a genuine PredictedEvent<IAbilityPD> (see VFXManager.PlayHealVfx's
        // own per-target cooldown for the anti-spam side of this).
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
