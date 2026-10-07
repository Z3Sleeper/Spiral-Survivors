using PurrNet.Prediction;
using UnityEngine;

// On a weapon Strike Highroll, reduces the dash charge regen timer by a flat flatCooldownReduction seconds
// multiplied by the highrolling hit's own onHitPower. Flat, not percent-of-baseDashCooldown (2026-08-21
// numeric pass) — baseDashCooldown is a fixed 5s for every class, so a percent of it is just a roundabout
// way of writing a flat number; default 0.5f is 10% of that fixed 5s base. onHitPower multiplier retained
// (added 2026-08-19, preventative balance pass) — onHitPower alone is usually a NET PENALTY in practice, not
// a buff: this augment's own grants are typically paired with WeaponCount/WeaponTracks/Multishot picks, each
// of which halves onHitPower (see OnHitPower's own doc comment). Not a damage/restore amount, so — matching
// OnAbilityStartReduceDashCDAugment/OnStrikeReduceAbilityCDAugment's own identical reasoning — this
// deliberately does NOT roll luck variance itself (the Highroll it reacts to is the TRIGGER, not something to
// roll again here).
//
// Can overflow into granting multiple dash charges at once — see PlayerControllerPD.ReduceDashCooldown's own
// doc comment for the cascading "grant a charge, carry the remainder into the next charge's timer, repeat"
// shape. All the actual charge/timer math lives there, this augment just computes the raw seconds to feed
// in, same split as OnAbilityStartReduceDashCDAugment.
public class OnStrikeHighrollReduceDashCDAugment : AugmentEffectPD<OnStrikeHighrollReduceDashCDAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {flatCooldownReduction} directly in the description text — see its own doc comment.
    public float flatCooldownReduction = 0.5f;

    private PlayerControllerPD _controller;

    // Assumes 100% OnHitPower (the actual per-hit value isn't known ahead of a real hit) — same convention
    // every other onHitPower-scaled Live*Ratio token in this codebase uses for its own static preview.
    public string LiveCooldownReduction(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(flatCooldownReduction)} "
            : "0 ";

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        Stats.AddStrikeHighrollListener(OnStrikeHighroll);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveStrikeHighrollListener(OnStrikeHighroll);
    }

    private void OnStrikeHighroll(StrikeHighrollData data)
    {
        if (_controller == null || Stats == null || Stats.IsDead) return;

        float reduction = flatCooldownReduction * data.onHitPower;
        if (reduction <= 0f) return;

        _controller.ReduceDashCooldown(reduction);

        // Dash Restore VFX opt-in (2026-09-26, user-curated, non-persistent dash-cooldown-reduction triggers
        // only) — safe unconditionally: this callback only ever runs once per real strike highroll, off
        // CharacterStatsPD._onStrikeHighroll, a genuine PredictedEvent<StrikeHighrollData> (see
        // VFXManager.PlayDashRestoreVfx's own per-target cooldown for the anti-spam side of this).
        VFXManager.instance?.PlayDashRestoreVfx(Stats.transform);
    }
}
