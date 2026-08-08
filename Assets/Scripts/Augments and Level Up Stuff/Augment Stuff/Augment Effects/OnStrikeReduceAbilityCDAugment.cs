using PurrNet.Prediction;
using UnityEngine;

// Shaves time off both class abilities' remaining cooldowns on every real weapon Strike — same OnStrike
// chain (AddStrikeListener) as OnStrikeEnergyRegenAugment, for the same "fires exactly once per genuine
// weapon connection" reasoning (see that file's own doc comment). Scaled by data.onHitPower, same as
// OnStrikeEnergyRegenAugment's own energy gain — so this still scales down with WeaponCount the same way
// every other OnHit/OnStrike-triggered effect does (see OnHitDamageAugment's own doc comment for the
// WeaponCount-divide reasoning). Not a damage/restore amount, so unlike most on-hit/on-strike effects in
// this codebase, this deliberately does NOT roll luck variance — see [[feedback_all_damage_rolls_luck]]'s
// own scope (damage-DEALING effects), which a cooldown tick-down isn't.
//
// Reduces via IAbilityPD.ReduceCooldownFlat, not a direct field write — every ability implementer already
// clamps its own cooldownTimer at 0, so calling this on an ability that's already off cooldown (or one this
// character doesn't even have equipped in that slot) is a harmless no-op, no extra IsReady/CooldownRemaining
// check needed here first.
//
// cooldownReductionFlat is deliberately the ONLY knob wired up right now (per explicit design request) —
// IAbilityPD also exposes ReduceCooldownPercentRemaining/ReduceCooldownPercentTotal for a future sibling
// augment (or a redesign of this one) that wants percent-based reduction instead, already implemented
// identically across every ability, ready to use without touching the ability classes again.
public class OnStrikeReduceAbilityCDAugment : AugmentEffectPD<OnStrikeReduceAbilityCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float cooldownReductionFlat = 0.05f;

    // Cached once — Ability1/Ability2 are permanent one-per-character fixtures for the whole match, same
    // "resolve components once, not on every event" convention as CharacterStatsPD._onAbilityStart's own doc
    // comment describes. This augment effect is a separate spawned identity (see AugmentEffectPD's own class
    // doc comment), so the abilities live on Stats' GameObject, not this one.
    private IAbilityPD[] _abilities;

    protected override void OnApply()
    {
        _abilities = Stats.GetComponents<IAbilityPD>();
        Stats.AddStrikeListener(OnStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveStrikeListener(OnStrike);
    }

    private void OnStrike(WeaponHitData data)
    {
        if (_abilities == null) return;
        float reduction = cooldownReductionFlat * data.onHitPower;
        foreach (var ability in _abilities)
            ability.ReduceCooldownFlat(reduction);
    }
}
