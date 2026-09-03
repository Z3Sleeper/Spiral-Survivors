using PurrNet.Prediction;
using UnityEngine;

// Shaves time off both class abilities' remaining cooldowns whenever this player takes damage — subscribes
// to CharacterStatsPD._onDamaged (fires from TakeDamage on any positive damage taken, excluding a killing
// blow), same trigger PhantomBladeOnDamagedAugment uses. Scales off DamageTakenData.preMitigationDamage
// deliberately, not postMitigationDamage — same reasoning as PhantomBladeOnDamagedAugment's own doc comment:
// the reduction should reflect how hard the player was actually hit, not get diluted by their own
// shield/DamageReduction.
//
// reductionPercent is a percent of each ability's own BaseCooldownDuration PER POINT of pre-mitigation
// damage taken, same "per-ability base" shape OnStrikeReduceAbilityCDAugment already uses (just scaled by
// raw damage instead of onHitPower). Default 0.005 (0.5%, nerfed 2026-08-31 from 0.01/1% after
// playtesting — the old value fully reset both abilities off a single 100-damage hit) means taking 200
// damage in one hit fully resets both abilities from their base cooldown (200 * 0.005 = 1.0 = 100% of
// BaseCooldownDuration).
//
// Reduces via IAbilityPD.ReduceCooldownFlat, not ReduceCooldownPercentRemaining/ReduceCooldownPercentTotal —
// the percent here is of the ability's fixed BASE cooldown (a flat seconds amount once resolved), not of
// however much cooldown currently remains — same convention OnStrikeReduceAbilityCDAugment's own doc comment
// explains. Every ability implementer already clamps its own cooldownTimer at 0, so calling this on an
// ability that's already off cooldown (or one this character doesn't have equipped in that slot) is a
// harmless no-op, no extra IsReady/CooldownRemaining check needed here first.
public class OnDamageTakenReduceAbilityCDAugment : AugmentEffectPD<OnDamageTakenReduceAbilityCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {reductionPercent} directly in the description text — see OnStrikeReduceAbilityCDAugment's own doc
    // comment (only public fields/properties/methods are checked).
    public float reductionPercent = 0.005f;

    // Shows reductionPercent of both class abilities' own BaseCooldownDuration PER 1 POINT of pre-mitigation
    // damage taken, "Ability1|Ability2", for AugmentDescriptionFormatter's {LiveCooldownReduction} token —
    // same "per 1 point" baseline-coefficient convention PhantomBladeOnDamagedAugment.LiveBladeDamageRatio
    // uses, since the trigger's own per-event damage amount varies hit-to-hit and isn't known ahead of a real
    // hit (unlike OnStrikeReduceAbilityCDAugment's own {LiveCooldownReduction}, which can assume a fixed 100%
    // OnHitPower baseline instead).
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

    // Cached once — Ability1/Ability2 are permanent one-per-character fixtures for the whole match, same
    // convention as OnStrikeReduceAbilityCDAugment's own _abilities field.
    private IAbilityPD[] _abilities;

    protected override void OnApply()
    {
        _abilities = Stats.GetComponents<IAbilityPD>();
        Stats.AddDamagedListener(OnDamaged);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDamagedListener(OnDamaged);
    }

    private void OnDamaged(DamageTakenData data)
    {
        if (_abilities == null || Stats == null || Stats.IsDead) return;
        foreach (var ability in _abilities)
        {
            float reduction = ability.BaseCooldownDuration * reductionPercent * data.preMitigationDamage;
            if (reduction > 0f) ability.ReduceCooldownFlat(reduction);
        }
    }
}
