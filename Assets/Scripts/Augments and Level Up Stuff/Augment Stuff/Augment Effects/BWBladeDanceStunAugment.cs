using PurrNet.Prediction;

// Bladeweaver BladeDance-specific augment (2026-10-06). This augment's own .asset must set
// requiredAbilities = [BWBladeDance] — scripts-only convention, that part is Editor-side, not here.
//
// While BladeDance is actively TRAVELING (Outbound or Return — explicitly NOT Hold, where the blades are
// just sitting still at the destination, per the explicit "but NOT during the staying still state"
// framing), every enemy struck (by any weapon — same "struck while the blades are off dancing" reasoning
// BWBladeDanceSlowAugment's own doc comment gives) is stunned for stunDuration AND, if that same hit also
// successfully interrupts them, gets the usual bonus CC-resist-immune InterruptTrueStun portion on top —
// same combined Stun+Interrupt (hard CC) shape KnightBuffStunAbilityPD's own TriggerAoE uses: Interrupt()
// attempted first (feeding the generic SI/FSI event pair via CharacterStatsPD.NotifyInterrupted, passing
// stunDuration as the paired-stun amount), then the plain Stun() call unconditionally after — ApplyStun's
// own Mathf.Max semantics mean the two can never fight each other, whichever produces the longer duration
// simply wins. Listens to the exactly-once _onStrike (not _onHit), same reasoning as the Slow augment.
//
// Also refills energyRefillPercent Max Energy when THIS augment's own hit lands the interrupt (2026-10-10,
// buff pass — this and BWBladeDanceDurationAugment were both underperforming). Deliberately checked inside
// the SAME `if (enemy.Interrupt())` branch below rather than via the generic SI/FSI broadcast every other
// interrupt-reactive augment in this codebase listens to — this needs to know the interrupt came from THIS
// hit specifically (not just "an interrupt happened somewhere, from any source"), and Interrupt()'s own
// return value already tells us exactly that with no extra bookkeeping.
//
// Synergy point with BWBladeDanceDurationAugment falls out for free, no extra code needed: GainEnergy
// already multiplies by CharacterStatsPD.bonusEnergyGainPercent internally, and that augment's own
// +100% energy generation IS bonusEnergyGainPercent for the whole dance window (via AddEnergyGainPercent) —
// so with both picked, GainEnergy(MaxEnergy * energyRefillPercent) already resolves to a full 100% refill
// on its own. A manual "is the other augment also picked" check here would just be duplicating a multiply
// that's already happening.
public class BWBladeDanceStunAugment : AugmentEffectPD<BWBladeDanceStunAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Guards FirstSuccessfulInterrupt (FSI) — reset once per BladeDance cast (via _onAbilityStart,
        // which BWBladeDanceAbilityPD only ever fires on a fresh Activate(), never on the Hold-phase
        // recast-to-return), so a whole Outbound+Return round trip fires FSI at most once, the first time
        // any enemy is actually interrupted. Same shape KnightBuffStunAbilityPD.State.interruptedThisCast
        // uses for its own AoE trigger.
        public bool interruptedThisCast;
        public void Dispose() { }
    }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {stunDuration} directly as a description token; only public fields/properties/methods are checked.
    public float stunDuration = 1f;
    // "Percent" suffix at the END so AugmentDescriptionFormatter's auto-*100 formatting fires.
    public float energyRefillPercent = 0.5f;

    // Cached once — see KnightBerserkHealAugment._berserk's own identical caching pattern, avoids a fresh
    // GetComponent call on every single strike (this event can fire very often in a busy fight).
    private BWBladeDanceAbilityPD _bladeDance;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _bladeDance = Stats.GetComponent<BWBladeDanceAbilityPD>();
    }

    protected override void OnApply()
    {
        Stats.AddStrikeListener(OnStrike);
        Stats.AddAbilityStartListener(OnAbilityStart);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats == null) return;
        Stats.RemoveStrikeListener(OnStrike);
        Stats.RemoveAbilityStartListener(OnAbilityStart);
    }

    private void OnAbilityStart(IAbilityPD ability)
    {
        if (ability.Identity != AbilityIdentity.BWBladeDance) return;
        currentState.interruptedThisCast = false;
    }

    private void OnStrike(WeaponHitData data)
    {
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with BWBladeDanceAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (_bladeDance == null) return;

        var phase = _bladeDance.CurrentPhase;
        if (phase != BWBladeDanceAbilityPD.Phase.Outbound && phase != BWBladeDanceAbilityPD.Phase.Return) return;
        if (!data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy) || enemy._isDead) return;

        // Interrupt first (feeds SI/FSI, applies the CC-resist-immune bonus portion), plain Stun()
        // unconditionally after — see class doc comment for why the ordering doesn't actually matter here
        // (Mathf.Max combines them regardless) but matches the established convention anyway.
        if (enemy.Interrupt())
        {
            Stats.NotifyInterrupted(enemy, ref currentState.interruptedThisCast, stunDuration);

            // Energy refill — see class doc comment for why this is gated on THIS hit's own Interrupt()
            // success specifically, and for why the BWBladeDanceDurationAugment synergy needs no extra code
            // here (GainEnergy's own bonusEnergyGainPercent multiply already handles it).
            Stats.GainEnergy(Stats.MaxEnergy * energyRefillPercent);
        }
        enemy.Stun(stunDuration);
    }
}
