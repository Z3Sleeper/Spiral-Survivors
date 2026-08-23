using PurrNet.Prediction;

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
public class FullEnergyBerserkSurgeAugment : AugmentEffectPD<FullEnergyBerserkSurgeAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    protected override void OnApply()
    {
        Stats.AddSurgeStartedListener(OnSurgeStarted);
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
