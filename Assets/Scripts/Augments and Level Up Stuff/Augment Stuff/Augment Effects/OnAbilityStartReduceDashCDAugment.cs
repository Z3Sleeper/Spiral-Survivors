using PurrNet.Prediction;
using UnityEngine;

// Inverse of OnDashStartedReduceAbilityCDAugment — every time either class ability starts (same
// CharacterStatsPD._onAbilityStart trigger OnAbilityStartReduceOtherAbilityCDAugment/
// OnAbilityStartRefillEnergyAugment already use), reduces the dash charge regen timer by
// reductionPercent (default 20%) of that CASTED ability's own BaseCooldownDuration (raw, un-hasted — same
// "base" reasoning as OnDashStartedReduceAbilityCDAugment's own doc comment).
//
// Can overflow into granting multiple dash charges at once — see PlayerControllerPD.ReduceDashCooldown's
// own doc comment for the cascading "grant a charge, carry the remainder into the next charge's timer,
// repeat" shape. All the actual charge/timer math lives there (PlayerControllerPD owns currentCharges/
// chargeRegenTimer directly), this augment just computes the raw seconds to feed in.
//
// Not a damage/restore amount, so — matching OnStrikeReduceAbilityCDAugment/
// OnAbilityStartReduceOtherAbilityCDAugment's own identical reasoning — this deliberately does NOT roll
// luck variance.
public class OnAbilityStartReduceDashCDAugment : AugmentEffectPD<OnAbilityStartReduceDashCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float reductionPercent = 0.15f;

    private PlayerControllerPD _controller;

    // Shows reductionPercent of both class abilities' own BaseCooldownDuration, "Ability1|Ability2" — same
    // token shape as OnAbilityStartReduceOtherAbilityCDAugment/OnDashStartedReduceAbilityCDAugment's own
    // identical tokens, since the actual dash-cooldown reduction depends on which ability gets cast.
    public string LiveCooldownReduction(CharacterStatsPD stats)
    {
        if (!AugmentDescriptionFormatter.ShowLiveValues || stats == null) return "";
        float ability1Value = 0f, ability2Value = 0f;
        foreach (var ability in stats.GetComponents<IAbilityPD>())
        {
            if (ability.Slot == AbilitySlot.Ability1) ability1Value = ability.BaseCooldownDuration * reductionPercent;
            else if (ability.Slot == AbilitySlot.Ability2) ability2Value = ability.BaseCooldownDuration * reductionPercent;
        }
        return $"{AugmentDescriptionFormatter.FormatLiveNumber(ability1Value)}|{AugmentDescriptionFormatter.FormatLiveNumber(ability2Value)} ";
    }

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        Stats.AddAbilityStartListener(OnAbilityStart);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveAbilityStartListener(OnAbilityStart);
    }

    private void OnAbilityStart(IAbilityPD ability)
    {
        if (_controller == null || Stats == null || Stats.IsDead) return;

        float reduction = ability.BaseCooldownDuration * reductionPercent;
        if (reduction <= 0f) return;

        _controller.ReduceDashCooldown(reduction);
    }
}
