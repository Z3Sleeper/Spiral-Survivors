using PurrNet.Prediction;

// Rogue Vanish-specific augment (2026-10-05). This augment's own .asset must set requiredAbilities =
// [RogueVanish] — scripts-only convention, that part is Editor-side, not here.
//
// While Vanish is active, extends ITS OWN active window by onHitDurationBonus (0.05s default) on every
// on-hit, bumped to onHitHighrollDurationBonus (0.1s default) when that specific hit was a highroll, both
// scaled by that hit's own data.onHitPower (same "(base) * data.onHitPower" convention every onhit-
// triggered payout in this codebase uses, e.g. OnStrikeHealAllyAugment) so a weak/glancing hit extends the
// duration less than a fully-powered one —
// resets to the normal duration on the NEXT cast regardless of how much a previous instance got extended
// (see RogueVanishAbilityPD.ExtendActiveTime's own doc comment for the full mechanism, including why it
// also re-syncs the separate invisibility floor-timer, and the hard cap — total extension across a whole
// cast capped at 100% of _appliedDuration, 2026-10-14, this augment was wildly out of balance uncapped).
// Deliberately does NOT also grant
// durationBonusPercent like the other three Vanish augments — a flat +50% doesn't fit this one's own "earn
// more uptime by landing hits" identity. Numbers are explicitly tentative, pending playtesting (Stuart's own
// framing) — this is the kind of augment that'll need real tuning passes.
//
// Deliberately listens to the GENERIC CharacterStatsPD._onHit event (AddHitListener), NOT the "fires
// exactly once per real strike" _onStrike — _onHit can fire TWICE for a single real connection if the
// player also has CritTriggerOnHitAugment (which re-invokes NotifyHit on a crit specifically to make
// other onhit-triggered effects treat it as two hits) — explicit, intentional synergy here: a player
// stacking both augments gets double the duration extension on a crit, not an accidental interaction.
public class RogueVanishOnHitDurationAugment : AugmentEffectPD<RogueVanishOnHitDurationAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {onHitDurationBonus}/{onHitHighrollDurationBonus} directly as description tokens; only public
    // fields/properties/methods are checked. Still Inspector-serializable either way.
    public float onHitDurationBonus = 0.05f;
    public float onHitHighrollDurationBonus = 0.1f;

    // Cached once — see KnightBerserkHealAugment._berserk's own identical caching pattern, avoids a fresh
    // GetComponent call on every single hit (this event can fire very often in a busy fight).
    private RogueVanishAbilityPD _vanish;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _vanish = Stats.GetComponent<RogueVanishAbilityPD>();
    }

    protected override void OnApply()
    {
        Stats.AddHitListener(OnHit);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveHitListener(OnHit);
    }

    private void OnHit(WeaponHitData data)
    {
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered
        // to a player with RogueVanishAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows. ExtendActiveTime itself gates
        // on currentState.isActive, so no separate "is Vanish currently active" check is needed here.
        if (_vanish == null) return;
        // * data.onHitPower — same "(base) * data.onHitPower" convention every onhit-triggered payout in
        // this codebase uses (see e.g. OnStrikeHealAllyAugment.OnAllyStrike), so a weak/glancing hit
        // extends the duration less than a fully-powered one.
        float bonus = data.isHighroll ? onHitHighrollDurationBonus : onHitDurationBonus;
        _vanish.ExtendActiveTime(bonus * data.onHitPower);
    }
}
