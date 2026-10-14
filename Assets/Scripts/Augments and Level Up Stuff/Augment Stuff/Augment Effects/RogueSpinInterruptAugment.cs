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
//     BASE cooldown once per cast, the first time ANYTHING interrupts an enemy while Spin's own active
//     window is open (2026-10-14 rework, replacing the 2026-10-12 "self-triggers only" version — see below
//     for why).
//
//     Listens to CharacterStatsPD's GLOBAL SI event (AddSuccessfulInterruptListener — fires on ANY
//     successful interrupt, any source) instead of reading RogueSpinAbilityPD.InterruptedThisCast (the
//     ability's own self-only FSI guard, still defined on the ability but no longer read here). The
//     self-only version had a real overlap bug with DashThroughInterruptAugment: Rogue's leap fires the same
//     DashMovementStarted/Ended pair that augment's own per-tick sweep listens to (see its own class doc
//     comment), and that sweep runs in LateSimulate() — ahead of Spin's own hit detection in most cases — so
//     the sweep's Interrupt() call usually landed FIRST, leaving the enemy no longer in an interruptible
//     state by the time Spin's own hit tried, which meant InterruptedThisCast silently never went true and
//     this augment's refund never fired, even though an interrupt plainly happened during the cast. Rather
//     than add a timing/ordering restriction between the two augments (explicitly not wanted — both are kept
//     as-is, no interaction changes), this just credits ANY interrupt landed during Spin's own active
//     window, regardless of which augment's hit actually caused it — exactly matching the new description
//     text ("On First Interrupt during Spinfiltrate"), not "when Spin itself interrupts."
//
//     OnSuccessfulInterrupt (the new SI listener below) only arms State.anyInterruptDuringCast while
//     _spin.IsActive is true at the moment it fires — so an interrupt from a totally unrelated later dash,
//     after this cast has already ended, can't retroactively credit a future cast. State.wasSpinActive still
//     edge-detects the active window ending inside Simulate() below, same as before, and arms
//     State.pendingCooldownReduction off anyInterruptDuringCast instead of the old ability-side flag —
//     covers BOTH end paths uniformly (a natural finish AND Spin itself getting interrupted mid-cast by
//     something else), same as before.
//
//     The actual ReduceCooldownFlat call deliberately does NOT fire from Simulate() itself, even though the
//     edge-detection above lives there — see AugmentEffectPD's own class doc comment: "a discrete one-time
//     += is not safe to call from Simulate(), since it risks double-firing during the client's routine
//     in-place reconciliation pass." Simulate() only arms the State flag (itself a clean, idempotent,
//     resimulation-safe operation — re-deriving the same flag value from the same inputs every replay is
//     fine); UpdateView() below consumes it off VERIFIED state only, gated by a plain (non-State)
//     _reductionApplied field — same "mutated only from a real-rendered-frame UpdateView call, edge-
//     detected against verified state" pattern AugmentEffectPD's own _applied field already uses one scope
//     up for OnApply().
//
//     This ALSO fixes a real bug in the old implementation: applying the reduction immediately at the
//     moment of interrupt (mid-cast, while the ability's own active window is still running) silently no-
//     opped on a single-charge RogueSpin — ReduceCooldownFlat gates on IsCooldownActivelyTicking, which is
//     false while isActive is still true UNLESS EffectiveMaxCharges > 1 (a multi-charge ability's own
//     regen timer always ticks concurrently with its active window, see that property's own doc comment) —
//     explaining exactly why Stuart saw this work "when it has charges" (multiple max charges) but silently
//     do nothing otherwise. Applying it only once the active window has already ended sidesteps this
//     entirely: cooldownTimer is guaranteed to already be actively ticking by that point regardless of
//     charge count, since !isActive alone satisfies IsCooldownActivelyTicking's own condition.
public class RogueSpinInterruptAugment : AugmentEffectPD<RogueSpinInterruptAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Edge-detects RogueSpinAbilityPD's own active window toggling, inside Simulate() below — see class
        // doc comment for why this lives in State rather than a plain field.
        public bool wasSpinActive;
        // Set true by OnSuccessfulInterrupt (the generic SI listener below) the first time ANY interrupt
        // lands while _spin.IsActive is true; cleared the instant a fresh cast begins. See class doc comment
        // for why this replaced reading RogueSpinAbilityPD.InterruptedThisCast directly.
        public bool anyInterruptDuringCast;
        // Armed the instant Spin's active window ends IF it interrupted something this cast; cleared the
        // instant a fresh cast begins, so it can arm again for THAT cast's own result. Only ever consumed
        // (the real ReduceCooldownFlat side effect fired) from UpdateView, off verified state — see class
        // doc comment.
        public bool pendingCooldownReduction;
        public void Dispose() { }
    }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {cooldownReductionPercent} directly as a description token; only public fields/properties/methods
    // are checked. Still Inspector-serializable either way.
    public float cooldownReductionPercent = 0.5f;

    // Cached once — see KnightBerserkHealAugment._berserk's own identical caching pattern, avoids a fresh
    // GetComponent call every tick.
    private RogueSpinAbilityPD _spin;

    // Edge-detects verified.pendingCooldownReduction — see class doc comment for why this plain field (not
    // State) is what actually gates the one-shot ReduceCooldownFlat call.
    private bool _reductionApplied;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _spin = Stats.GetComponent<RogueSpinAbilityPD>();
    }

    protected override void OnApply()
    {
        if (_spin != null) _spin.canInterrupt = true;
        Stats.AddSuccessfulInterruptListener(OnSuccessfulInterrupt);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveSuccessfulInterruptListener(OnSuccessfulInterrupt);
    }

    // Generic SI — fires on ANY successful interrupt, any source (see class doc comment for why this
    // replaced reading RogueSpinAbilityPD.InterruptedThisCast). Only credits the CURRENT cast, gated on
    // _spin.IsActive at the exact moment this fires — PredictedEvent listener, safe to mutate currentState
    // directly (guaranteed exactly once per real interrupt, regardless of resimulation), same established
    // shape every other OnDodge/OnStrike/OnSuccessfulInterrupt-reacting augment in this codebase uses.
    private void OnSuccessfulInterrupt()
    {
        if (_spin == null || !_spin.IsActive) return;
        currentState.anyInterruptDuringCast = true;
    }

    protected override void Simulate(ref State state, float delta)
    {
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with RogueSpinAbilityPD equipped — but guarded anyway, same defensive convention every
        // other GetComponent lookup in this codebase already follows.
        if (_spin == null) return;

        bool isActiveNow = _spin.IsActive;

        // Fresh cast just started — clear any stale armed flag from a previous cast that UpdateView hasn't
        // gotten around to consuming yet (shouldn't normally still be armed by then, but cheap safety), and
        // reset the new per-cast interrupt-credit flag so a late SI from the PREVIOUS cast can't bleed into
        // this one.
        if (!state.wasSpinActive && isActiveNow)
        {
            state.pendingCooldownReduction = false;
            state.anyInterruptDuringCast = false;
        }

        // Cast just ended (either path — natural finish or Spin itself interrupted) and ANY interrupt landed
        // while it was active — see class doc comment for why this is anyInterruptDuringCast now, not
        // _spin.InterruptedThisCast.
        if (state.wasSpinActive && !isActiveNow && state.anyInterruptDuringCast)
            state.pendingCooldownReduction = true;

        state.wasSpinActive = isActiveNow;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);
        if (!verified.HasValue) return;

        if (verified.Value.pendingCooldownReduction)
        {
            if (!_reductionApplied)
            {
                _reductionApplied = true;
                _spin?.ReduceCooldownFlat(cooldownReductionPercent * _spin.BaseCooldownDuration);
            }
        }
        else
        {
            _reductionApplied = false;
        }
    }
}
