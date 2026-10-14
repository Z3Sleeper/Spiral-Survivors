using PurrNet.Prediction;
using UnityEngine;

// Shaves time off both class abilities' remaining cooldowns on every real weapon hit — moved from the
// separate OnStrike chain onto the generic OnHit chain (AddHitListener, 2026-10-14, explicit request).
// Unlike Strike, OnHit CAN fire twice for a single real connection if the player also has
// CritTriggerOnHitAugment (which re-invokes NotifyHit on a crit specifically to make other onhit-triggered
// effects treat it as two hits) — same intentional synergy RogueVanishOnHitDurationAugment's own doc
// comment describes: a player stacking both gets double the cooldown reduction on a crit, not an
// accidental interaction. Scaled by data.onHitPower, same as OnHitEnergyRegenAugment's own energy gain — so
// this still scales down with WeaponCount the same way every other OnHit-triggered effect does (see
// OnHitDamageAugment's own doc comment for the WeaponCount-divide reasoning). Not a damage/restore amount,
// so unlike most on-hit effects in this codebase, this deliberately does NOT roll luck variance — see
// [[feedback_all_damage_rolls_luck]]'s own scope (damage-DEALING effects), which a cooldown tick-down isn't.
//
// Reduces via IAbilityPD.ReduceCooldownFlat, not a direct field write — every ability implementer already
// clamps its own cooldownTimer at 0, so calling this on an ability that's already off cooldown (or one this
// character doesn't even have equipped in that slot) is a harmless no-op, no extra IsReady/CooldownRemaining
// check needed here first.
//
// reductionPercent (2026-08-21 numeric pass, was a flat cooldownReductionFlat seconds constant) — a
// percent of the STRUCK ability's own BaseCooldownDuration, same "per-ability base" shape
// OnAbilityStartReduceOtherAbilityCDAugment already uses, instead of an amount untethered from any stat.
// Default 0.005 (0.5%, nerfed 2026-08-31 from 0.02/2% after playtesting) — unchanged by the OnStrike -> OnHit
// move, already matched the live prefab value — targets roughly 0.025s off a 5s-base ability per hit, this
// fires far more often than this family's other triggers, hence the much smaller per-proc percent. Still
// scaled by data.onHitPower same as before (see OnHitEnergyRegenAugment's own doc comment for the
// WeaponCount-divide reasoning) — that scaling wasn't part of this pass.
//
// Reduces via IAbilityPD.ReduceCooldownFlat, not ReduceCooldownPercentRemaining/ReduceCooldownPercentTotal —
// the percent here is of the ability's fixed BASE cooldown (a flat seconds amount once resolved), not of
// however much cooldown currently remains.
public class OnHitReduceAbilityCDAugment : AugmentEffectPD<OnHitReduceAbilityCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {reductionPercent}/{LiveCooldownReduction} directly in the description text — see its own doc comment
    // (only public fields/properties/methods are checked).
    public float reductionPercent = 0.005f;

    // Cached once — Ability1/Ability2 are permanent one-per-character fixtures for the whole match, same
    // "resolve components once, not on every event" convention as CharacterStatsPD._onAbilityStart's own doc
    // comment describes. This augment effect is a separate spawned identity (see AugmentEffectPD's own class
    // doc comment), so the abilities live on Stats' GameObject, not this one.
    private IAbilityPD[] _abilities;

    // Shows reductionPercent of both class abilities' own BaseCooldownDuration, "Ability1|Ability2", for
    // AugmentDescriptionFormatter's {LiveCooldownReduction} token — same shape/naming as
    // OnDashStartedReduceAbilityCDAugment's own identical token. Assumes 100% OnHitPower (the actual per-hit
    // value isn't known ahead of a real hit — same convention every other onHitPower-scaled Live*Ratio token
    // in this codebase uses for its own static preview).
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
        Stats.AddHitListener(OnHit);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveHitListener(OnHit);
    }

    private void OnHit(WeaponHitData data)
    {
        if (_abilities == null) return;
        foreach (var ability in _abilities)
        {
            float reduction = ability.BaseCooldownDuration * reductionPercent * data.onHitPower;
            if (reduction > 0f) ability.ReduceCooldownFlat(reduction);
        }
    }
}
