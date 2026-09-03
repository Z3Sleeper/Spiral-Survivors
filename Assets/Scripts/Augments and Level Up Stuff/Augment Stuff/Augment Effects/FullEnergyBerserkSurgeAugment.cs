using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. A player-initiated Surge activation (StartSurge) that would otherwise be perfectly
// normal turns into a full Berserk Surge instead (Stats.StartBerserkSurge — see its own doc comment for the
// full contract), but ONLY if the player was at full energy the instant they activated it.
//
// Timing: _onSurgeStarted fires AFTER StartSurge() has already deducted SurgeCost from currentState.energy
// (see its own body), so "was full energy at activation" can't be read directly off Stats.CurrentEnergy once
// this listener runs — it has to reconstruct the pre-deduction value instead: CurrentEnergy + SurgeCost.
//
// Infinite-loop safety (explicit design concern, not just a hypothetical): StartBerserkSurge() ITSELF refills
// energy to MaxEnergy and can also fire _onSurgeStarted (when nothing was surging yet) — which is exactly
// this augment's own trigger condition, "full energy + a surge just started." Two things make this provably
// safe, not just probably safe:
// 1. !Stats.IsForcedSurging guards out reacting to a Surge start that's ALREADY a Berserk Surge (including
//    one this same augment just triggered, or one from any other source like DeathPreventedBerserkAugment)
//    — there's nothing left to "upgrade" once forcedSurge is already true.
// 2. Even without that guard, StartBerserkSurge() only ever invokes _onSurgeStarted when nothing was
//    surging yet (see its own "alreadySurging" check) — by the time THIS listener's own StartBerserkSurge()
//    call runs, isSurging is already true (set by whatever started the surge in the first place), so that
//    call can never re-fire _onSurgeStarted again regardless. The reactive chain is structurally bounded at
//    one hop, never recursive.
//
// Flat Max Energy on pick (2026-08-31) — same "give a permanent, immediate incentive" treatment as
// DeathPreventedBerserkAugment's own identical grant, for the same reason: this augment's entire payoff used
// to be entirely conditional (activating Surge at exactly full energy), giving nothing on pick itself.
public class FullEnergyBerserkSurgeAugment : AugmentEffectPD<FullEnergyBerserkSurgeAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusMaxEnergyFlat = 25f;

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveSurgeStartedListener(OnSurgeStarted);
    }

    private void OnSurgeStarted()
    {
        if (Stats == null || Stats.IsForcedSurging) return;
        if (Stats.CurrentEnergy + Stats.SurgeCost < Stats.MaxEnergy) return;

        Stats.StartBerserkSurge();
    }
}
