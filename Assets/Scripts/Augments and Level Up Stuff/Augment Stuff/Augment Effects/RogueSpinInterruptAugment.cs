using PurrNet.Prediction;

// Rogue Spin-specific augment (2026-10-04), replaces the scrapped RogueSpinOnHitAugment draft. This
// augment's own .asset must set requiredAbilities = [RogueSpin] — scripts-only convention, that part is
// Editor-side, not here.
//
// Two halves:
//  1. Makes RogueSpinAbilityPD's own hits capable of interrupting enemies — a no-op-by-default switch
//     on the ability itself (RogueSpinAbilityPD.canInterrupt, Pattern B, same shape as
//     KnightBerserkMoveSpeedAugment) that HitEnemy() checks before applying damage/knockback. Feeds the
//     same generic SI/FSI event pair every other Interrupt() caller in this codebase does
//     (CharacterStatsPD.NotifyInterrupted).
//  2. A passive that reduces RogueSpin's OWN cooldown by cooldownReductionPercent (50% default) of its
//     BASE cooldown on ANY successful interrupt — not just ones from half #1, any source at all (Shield
//     Break, ExeAxeThrow, other players' interrupts, etc.) — by listening to the GLOBAL
//     CharacterStatsPD._onSuccessfulInterrupt event (AddSuccessfulInterruptListener, a plain no-payload
//     PredictedEvent that fires on EVERY successful Interrupt() regardless of source, unlike
//     _onAbilityStart/_onAbilityEnd which carry a specific IAbilityPD). "% of BASE cooldown," not current/
//     haste-scaled CooldownDuration — same convention OnDamageTakenReduceAbilityCDAugment/
//     OnStrikeReduceAbilityCDAugment already use for an identical reason (a flat seconds amount resolved
//     once, not a percent of however much happens to currently remain) — so this calls
//     ReduceCooldownFlat(cooldownReductionPercent * spin.BaseCooldownDuration), NOT
//     ReduceCooldownPercentTotal. ReduceCooldownFlat already overflows any excess into the next charge if
//     RogueSpin currently has 2+ Max Charges (see project_ability_cooldown_reduction_overflow_fix memory) —
//     "same rules as usual" per Stuart's own framing, nothing extra needed here for that.
//
// Both halves are independent of each other mechanically (the passive triggers off ANY interrupt, not
// specifically ones half #1 caused) but are bundled into one augment since they're thematically the same
// "Spin becomes an interrupt tool" idea.
public class RogueSpinInterruptAugment : AugmentEffectPD<RogueSpinInterruptAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {cooldownReductionPercent} directly as a description token; only public fields/properties/methods
    // are checked. Still Inspector-serializable either way.
    public float cooldownReductionPercent = 0.5f;

    // Cached once — see KnightBerserkHealAugment._berserk's own identical caching pattern, avoids a fresh
    // GetComponent call every single interrupt event (this event can fire often in a busy fight).
    private RogueSpinAbilityPD _spin;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _spin = Stats.GetComponent<RogueSpinAbilityPD>();
    }

    protected override void OnApply()
    {
        Stats.AddSuccessfulInterruptListener(OnSuccessfulInterrupt);
        if (_spin != null) _spin.canInterrupt = true;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveSuccessfulInterruptListener(OnSuccessfulInterrupt);
    }

    private void OnSuccessfulInterrupt()
    {
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with RogueSpinAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (_spin == null) return;
        _spin.ReduceCooldownFlat(cooldownReductionPercent * _spin.BaseCooldownDuration);
    }
}
