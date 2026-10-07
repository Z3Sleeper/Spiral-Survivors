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
// Per AugmentEffectPD's own doc comment, a discrete one-shot Stats.Heal() call is NOT safe directly from
// Simulate() (risks double-firing during the client's own resimulation/reconciliation pass). So this follows
// SurgeHealAugment's exact established shape: Simulate() only ever accumulates a monotonically-growing
// desiredTotalHealed total into this augment's OWN predicted State (safe — same as SurgeInterruptAoEAugment's
// own currentState.stackCount), reading KnightBerserkAbilityPD's IsActive each tick (a plain cross-identity
// property read, same safety class as every other *.EffectiveX read elsewhere in this codebase). The actual
// Stats.Heal() call only ever fires from UpdateView(), off verified state, for just the DELTA since last
// applied — and the heal VFX (SurgeHealAugment's own 2026-09-26 precedent) piggybacks on that exact same
// verified-state call, so it's exactly as safe as the Heal() call it sits next to.
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
        // SurgeHealAugment's own desiredTotalHealed. The real Stats.Heal() application is deferred to
        // UpdateView() instead of happening right where this grows.
        public float desiredTotalHealed;
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
    public float maxEnergyFlat = 50f;

    private KnightBerserkAbilityPD _berserk;
    // Tracks how much has actually been granted via Stats.Heal so far — mutated only from UpdateView() off
    // verified state, same role as SurgeHealAugment's own _appliedAmount.
    private float _appliedAmount;

    protected override void LateAwake()
    {
        base.LateAwake();
        if (Stats != null) _berserk = Stats.GetComponent<KnightBerserkAbilityPD>();
    }

    protected override void OnApply()
    {
        Stats.AddMaxEnergyFlat(maxEnergyFlat);
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

        float desired = verified.Value.desiredTotalHealed;
        if (Mathf.Approximately(desired, _appliedAmount)) return;

        Stats.Heal(desired - _appliedAmount);
        _appliedAmount = desired;

        // Heal VFX — see class doc comment for why this piggybacks on this exact verified-state delta-apply.
        VFXManager.instance?.PlayHealVfx(Stats.transform);
    }
}
