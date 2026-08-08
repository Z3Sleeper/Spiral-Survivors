using PurrNet.Prediction;

// Reworked — the old +Attack Speed/+Move Speed timed buff on shield break felt weak no matter how it was
// tuned, so this now triggers CharacterStatsPD's Berserk Surge mechanic instead (StartBerserkSurge — see
// its own doc comment for the full contract: refills energy to max, forces an uncancellable Surge that runs
// until that energy pool drains dry, grants invincibility for the whole window, pauses shield regen and the
// Death Prevention grant timer, and blocks any ActiveSurgeBlock ability from being cast mid-surge). This
// augment owns none of that logic anymore — it's just the shield-break trigger. Still fires the normal
// Surge Start/End events (CharacterStatsPD._onSurgeStarted/_onSurgeEnded), same as a player-initiated Surge,
// so anything else already listening for those (SurgeDualWeildAugment, etc.) reacts identically.
public class ShieldBreakFrenzyAugment : AugmentEffectPD<ShieldBreakFrenzyAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    protected override void OnApply()
    {
        Stats.AddShieldBreakListener(OnShieldBreak);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveShieldBreakListener(OnShieldBreak);
    }

    private void OnShieldBreak() => Stats.StartBerserkSurge();
}
