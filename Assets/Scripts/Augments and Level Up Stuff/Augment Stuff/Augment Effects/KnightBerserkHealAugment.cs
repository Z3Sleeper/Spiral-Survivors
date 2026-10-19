using PurrNet.Prediction;
using UnityEngine;

// Knight Berserk-specific augment (2026-10-02), third requiredAbilities example (see
// KnightBerserkMoveSpeedAugment's own doc comment for the first two patterns this codebase has established).
// This augment's own .asset must set requiredAbilities = [KnightBerserk] — scripts-only convention, that
// part is Editor-side, not here.
//
// While KnightBerserkAbilityPD's own Berserk window is active, heals healPerSecondPercent (10%) Max Health
// per second, applied in discrete tickInterval (0.25s) chunks — NOT a smooth per-frame trickle, so each chunk
// is independently HealPower/crit-rolled and fires its own heal VFX/number, same chunked feel ArrowRain's own
// damage ticks have. The FINAL chunk, when Berserk ends mid-tick, is a pro-rated partial heal for whatever
// fraction of tickInterval had actually elapsed since the last full chunk (e.g. a 0.45s-long Berserk window
// fires one full 2.5% chunk at 0.25s, then a 2% chunk — 0.20s/0.25s of a chunk — the instant it ends at
// 0.45s) rather than losing that leftover sliver or rounding it up to a full extra chunk.
//
// Simulate() accumulates a monotonically-growing desiredTotalHealed total into this augment's OWN predicted
// State (safe — same as SurgeInterruptAoEAugment's own currentState.stackCount), reading
// KnightBerserkAbilityPD's IsActive each tick (a plain cross-identity property read, same safety class as
// every other *.EffectiveX read elsewhere in this codebase).
//
// The actual Stats.Heal() call now ALSO fires from Simulate() (2026-10-16 — was deferred to UpdateView(),
// off verified state, for just the delta since last applied). That deferral was originally there because a
// discrete one-shot += is not safe called UNGUARDED/unconditionally from Simulate() (risks double-firing
// during PurrDiction's own in-place/jump resimulation pass, which can re-run one tick's Simulate() a second
// time against the same not-yet-rolled-back state — see PredictionManager.OnPostTick's RollbackToFrame ->
// SimulateFrameInPlace -> SimulateFrame(sameTick,true) sequence). But Heal() also rolls GameRandom
// (RollHealMultiplier, whenever CritHealPower != 1f) and fires the _onHeal PredictedEvent — both of which
// NEED to run deterministically, tick-ordered, through Simulate(), not from UpdateView()'s own per-machine
// render-cadence timing (see CharacterStatsPD.RaiseOnEnemyDeathDeterministic's doc comment for the fuller
// "why" of this exact bug class, found the same day). The double-fire risk is avoided here the same way
// Die()'s own deathDeterministicReactionsFired guard avoids it: appliedAmount lives in predicted State
// alongside desiredTotalHealed, so comparing them and applying only the delta is naturally idempotent across
// an in-place double-call (the second call sees them already equal and does nothing) and correctly rolls
// back together if an earlier tick's accumulation turns out to be a misprediction. UpdateView() now only
// fires the heal VFX, off verified.appliedAmount change-detection (same shape it always had, just no longer
// also calling Heal() itself).
public class KnightBerserkHealAugment : AugmentEffectPD<KnightBerserkHealAugment.State>
{
    public struct State : IPredictedData<State>
    {
        // Time accumulated toward the next full tickInterval chunk while Berserk is active — flushed as a
        // pro-rated partial chunk the instant Berserk ends (see wasActive below), never carried over into a
        // later Berserk window.
        public float tickTimer;
        // Previous tick's berserk.IsActive — lets Simulate() detect the exact falling edge (Berserk just
        // ended) to fire the partial-chunk flush exactly once, rather than every tick while inactive.
        public bool  wasActive;
        // Monotonically-growing total amount this augment has EARNED so far (never decreases) — same role as
        // SurgeHealAugment's own desiredTotalHealed.
        public float desiredTotalHealed;
        // How much of desiredTotalHealed has actually been granted via Stats.Heal() so far (2026-10-16,
        // moved from a plain instance field into State) — comparing this against desiredTotalHealed every
        // Simulate() tick is what makes the real Heal() application idempotent across an in-place double-
        // call; see class doc comment.
        public float appliedAmount;
        public void Dispose() { }
    }

    // Public, and named with the literal "Percent" suffix at the END (not buried mid-name as
    // healPercentPerSecond was) — so AugmentDescriptionFormatter's reflection can resolve
    // {healPerSecondPercent} as a description token AND get its auto-*100 percent formatting, which only
    // fires on an EndsWith("Percent") check (same "PerUnit vs Percent" trap OnHitDamageDistanceAugment's own
    // perUnitDistancePercent rename fixed for the identical reason).
    public float healPerSecondPercent = 0.10f;
    // Chunk size — NOT exposed as a description token (it's an implementation/feel detail, not a stat the
    // player is picking for), same "private tickInterval" shape ArcherArrowRainAbilityPD's own DoT ticks use.
    [SerializeField] private float tickInterval = 0.25f;
    // Flat, unconditional Max Energy grant (2026-10-02) — same shape as KnightBerserkMoveSpeedAugment's own
    // +25, tying this augment to Knight's energy-spending Berserk ability thematically too.
    public float bonusMaxEnergyFlat = 50f;

    private KnightBerserkAbilityPD _berserk;
    // VFX-dedup only now (2026-10-16) — tracks the last value of verified.appliedAmount the heal VFX fired
    // for, so it doesn't re-fire every frame while verified sits on an already-handled value. The actual
    // heal-applied tracking moved into State.appliedAmount (see its own doc comment); this is purely a
    // render-layer "did I already play the VFX for this" check, same safety class as _wasDeadLastFrame.
    private float _vfxFiredAmount;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _berserk = Stats.GetComponent<KnightBerserkAbilityPD>();
    }

    protected override void OnApply()
    {
        Stats.AddMaxEnergyFlat(bonusMaxEnergyFlat);
    }

    protected override void Simulate(ref State state, float delta)
    {
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with KnightBerserkAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (_berserk == null) { state.wasActive = false; return; }

        bool isActive = _berserk.IsActive;
        if (isActive)
        {
            state.tickTimer += delta;
            while (state.tickTimer >= tickInterval)
            {
                state.tickTimer -= tickInterval;
                state.desiredTotalHealed += HealAmount(tickInterval);
            }
        }
        else if (state.wasActive && state.tickTimer > 0f)
        {
            // Berserk just ended mid-tick — flush the pro-rated partial chunk for whatever fraction of
            // tickInterval had elapsed, rather than losing it or rounding up to a full extra chunk.
            state.desiredTotalHealed += HealAmount(state.tickTimer);
            state.tickTimer = 0f;
        }

        state.wasActive = isActive;

        // Applies the real heal the instant this tick's accumulation grows past what's already been
        // granted — deterministic, tick-ordered (see class doc comment). Comparing against appliedAmount
        // (in State, not a plain field) is what makes this safe to call unconditionally every tick: once
        // applied, the two are equal and this is a no-op, including on an in-place double-call of this same
        // tick.
        if (state.desiredTotalHealed > state.appliedAmount)
        {
            Stats.Heal(state.desiredTotalHealed - state.appliedAmount);
            state.appliedAmount = state.desiredTotalHealed;
        }
    }

    // seconds * healPerSecondPercent * MaxHealth — a full tickInterval chunk and the final partial chunk
    // both just call this with however many seconds actually elapsed, so the total healed over the whole
    // Berserk window always comes out to exactly healPerSecondPercent * window-length * MaxHealth, split into
    // chunks rather than smoothed.
    private float HealAmount(float seconds) => Stats.MaxHealth * healPerSecondPercent * seconds;

    protected override void UpdateView(State viewState, State? verified)
    {
        base.UpdateView(viewState, verified);

        if (Stats == null || !verified.HasValue) return;

        float applied = verified.Value.appliedAmount;
        if (Mathf.Approximately(applied, _vfxFiredAmount)) return;
        _vfxFiredAmount = applied;

        // Heal VFX only now — the actual Stats.Heal() call moved to Simulate() (see class doc comment).
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
