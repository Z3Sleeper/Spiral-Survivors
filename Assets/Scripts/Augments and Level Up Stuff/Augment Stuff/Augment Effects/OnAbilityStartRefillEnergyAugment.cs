using PurrNet.Prediction;
using UnityEngine;

// Refills energy on every ability start — CharacterStatsPD._onAbilityStart fires the instant any ability's
// own AbilityIsActive flips true (see that event's own doc comment). energyPerCooldownSecondPercent is a
// PERCENT of MaxEnergy per cooldown second (2026-09-03, was a flat absolute amount — same "percent of
// MaxEnergy" shift EnergyRegen itself already went through, see CharacterStatsPD.baseEnergyRegen's own doc
// comment) times that ability's own BaseCooldownDuration (the raw, un-hasted cooldown — see
// IAbilityPD.BaseCooldownDuration's own doc comment for why this and not the haste-scaled
// CooldownDuration), times Stats.MaxEnergy. Renamed from energyPerCooldownSecond to
// energyPerCooldownSecondPercent (2026-09-17) — the old name didn't end in "Percent" despite already storing
// a fraction, so AugmentDescriptionFormatter's *100 auto-scale never applied to it directly (same trap
// OnDashStartedGainEnergyAugment's own energyPerDash->energyPerDashPercent rename fixed); this rename resets
// the Inspector value back to this field's own default (0.01) — re-verify against whatever was actually
// tuned before, if different. Deliberately does NOT roll luck variance — the "everything that deals/restores
// an amount rolls luck" rule is specifically for damage and healing, not every restore effect; this one's a
// flat, predictable refill by design. GainEnergy itself already clamps at MaxEnergy, so no separate cap
// check needed here.
public class OnAbilityStartRefillEnergyAugment : AugmentEffectPD<OnAbilityStartRefillEnergyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // 0.01f = 1% (this codebase's percent fields store the fraction, e.g. 0.05f = 5%).
    public float energyPerCooldownSecondPercent = 0.01f;
    // Same flat, unconditional +25 Max Energy every other Surge-family energy augment grants
    // (SurgeWeaponCountAugment/SurgeStackingAttackSpeedAugment/SurgeHealAugment/FullEnergyBerserkSurgeAugment/
    // DeathPreventionBerserkAugment) — 2026-09-17, granted once in OnApply(), same shape as those.
    public float bonusMaxEnergyFlat = 25f;

    // No single number to preview (the actual refill depends on which of this character's two abilities was
    // cast) — shows both class abilities' own actual values instead, "Ability1/Ability2", for
    // AugmentDescriptionFormatter's {LiveEnergyRegenRatio} token. Values are now percent-of-MaxEnergy
    // (*100, with a trailing "%" per number) rather than raw energy amounts (2026-09-03, same unit shift as
    // the field itself) — explicit request.
    public string LiveEnergyRegenRatio(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "0%|0%";
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            float value = energyPerCooldownSecondPercent * ability.BaseCooldownDuration * 100f;
            if (ability.Slot == AbilitySlot.Ability1) ability1Value = value;
            else if (ability.Slot == AbilitySlot.Ability2) ability2Value = value;
        }
        // "|" not "/" — see OnAbilityStartHealHealthRegenAugment's identical LiveAbilityHealAmount for why.
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(ability1Value)}%|{AugmentDescriptionFormatter.FormatLiveNumber(ability2Value)}%";
    }

    protected override void OnApply()
    {
        Stats.AddAbilityStartListener(OnAbilityStart);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

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

        // No Surge-specific reduction here anymore (2026-09-03, was a 25%-while-Surging multiplier) — see
        // CharacterStatsPD.GainEnergy's own doc comment: the infinite-Surge-loop concern this used to guard
        // against is now handled centrally there (a hard zero specifically during Berserk/forced Surge),
        // not per-augment, and a normal Surge was never actually the problem.
        Stats.GainEnergy(energyPerCooldownSecondPercent * baseCooldown * Stats.MaxEnergy);
    }
}
