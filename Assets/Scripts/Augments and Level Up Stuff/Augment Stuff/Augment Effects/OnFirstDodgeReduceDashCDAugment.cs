using PurrNet.Prediction;
using UnityEngine;

// On First Dodge (FD — CharacterStatsPD.AddFirstDodgeListener, fires once per dash-movement's own activation
// window — see CharacterStatsPD.NotifyDodge's own doc comment for the exact 3-condition Dodge contract and
// why only the FIRST dodge of a given dash counts here, same SI/FSI-family "once per activation" shape),
// reduces the dash charge regen timer by flatCooldownReduction seconds flat.
//
// Flat, not a percent of Dash's own base cooldown (unlike OnAbilityStartReduceDashCDAugment's own
// percent-of-CASTED-ability shape) — Dash's own base cooldown is a fixed 5s for every class (same reasoning
// OnDashStartedReduceAbilityCDAugment's own pre-2026-08-21 flat design used to cite), so a flat amount here
// is already consistent across every character without needing a percent at all.
//
// Can overflow into granting multiple dash charges at once — see PlayerControllerPD.ReduceDashCooldown's own
// doc comment for the cascading "grant a charge, carry the remainder into the next charge's timer, repeat"
// shape. All the actual charge/timer math lives there, this augment just feeds in the raw seconds.
//
// Not a damage/restore amount, so — matching every other "reduce a cooldown" augment in this family — this
// deliberately does NOT roll luck variance.
public class OnFirstDodgeReduceDashCDAugment : AugmentEffectPD<OnFirstDodgeReduceDashCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {flatCooldownReduction} directly in the description text — see its own doc comment.
    public float flatCooldownReduction = 5f;

    private PlayerControllerPD _controller;

    // Flat value, no per-stat computation needed — kept as a method (not a plain property) purely for
    // consistency with every other augment's own {LiveCooldownReduction} token in this family.
    public string LiveCooldownReduction(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(flatCooldownReduction)} "
            : "0 ";

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        Stats.AddFirstDodgeListener(OnFirstDodge);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveFirstDodgeListener(OnFirstDodge);
    }

    private void OnFirstDodge()
    {
        if (_controller == null || Stats == null || Stats.IsDead) return;
        if (flatCooldownReduction <= 0f) return;

        _controller.ReduceDashCooldown(flatCooldownReduction);
    }
}
