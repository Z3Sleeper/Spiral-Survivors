using PurrNet.Prediction;
using UnityEngine;

// Renamed/reworked from ShieldBreakFrenzyAugment (2026-08-23) — triggers CharacterStatsPD's Berserk Surge
// mechanic (StartBerserkSurge — see its own doc comment for the full contract: refills energy to max, forces
// an uncancellable Surge that runs until that energy pool drains dry, grants invincibility for the whole
// window, pauses shield regen and the Death Prevention grant timer, and blocks any ActiveSurgeBlock ability
// from being cast mid-surge) off a Death Prevention save landing instead of a shield break. This augment owns
// none of the Berserk logic itself — it's just the trigger. Still fires the normal Surge Start/End events
// (CharacterStatsPD._onSurgeStarted/_onSurgeEnded), same as a player-initiated Surge, so anything else
// already listening for those (SurgeDualWeildAugment, etc.) reacts identically.
//
// Also activates CharacterStatsPD's own Death Prevention mechanic (Stats.ActivateDeathPreventions() — see
// its own doc comment for the "already active elsewhere, shrink the grant timer instead" stacking rule),
// same convention DeathPreventionShieldDRAugment/DeathPreventionInvisibleAugment already use — this augment
// is now a real source of Death Prevention, not just a reaction to one landing from elsewhere.
//
// Flat Max Energy on pick (2026-08-31) — unlike DeathPreventionShieldDRAugment (an immediate, always-on
// passive while shielded), this augment's only payoff used to be entirely locked behind an up-to-30s Death
// Prevention grant cycle actually landing, giving no reason to pick it otherwise. The Max Energy grant is a
// permanent, immediate incentive independent of that — also a direct, deliberate synergy with the Berserk
// Surge it triggers (StartBerserkSurge refills energy to this now-higher max and the forced Surge runs until
// that pool drains, so more Max Energy also means a longer Berserk window each time it triggers).
public class DeathPreventedBerserkAugment : AugmentEffectPD<DeathPreventedBerserkAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float bonusMaxEnergyFlat = 25f;

    protected override void OnApply()
    {
        Stats.AddDeathPreventedListener(OnDeathPrevented);
        Stats.ActivateDeathPreventions();
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDeathPreventedListener(OnDeathPrevented);
    }

    private void OnDeathPrevented() => Stats.StartBerserkSurge();
}
