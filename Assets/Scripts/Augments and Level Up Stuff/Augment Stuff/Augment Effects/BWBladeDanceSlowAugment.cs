using PurrNet.Prediction;

// Bladeweaver BladeDance-specific augment (2026-10-06). This augment's own .asset must set
// requiredAbilities = [BWBladeDance] — scripts-only convention, that part is Editor-side, not here.
//
// While BladeDance's own Outbound->Hold->Return window is active, every enemy struck (by ANY weapon this
// character has — BladeDance itself deals no damage, it only relocates where weapons orbit, so this is
// "struck while the blades are off dancing," not "struck by the blades specifically") is slowed by
// slowPercent for slowDuration. Listens to the GENERIC, exactly-once-per-real-strike CharacterStatsPD.
// _onStrike event (AddStrikeListener), deliberately NOT _onHit — Stuart's own explicit call: he wants a
// consistent slow amount/duration regardless of how a hit happened to roll, not scaled by data.onHitPower
// the way most on-hit-triggered effects in this codebase are (see OnStrikeHealAllyAugment for the usual
// "(base) * data.onHitPower" convention this one is deliberately NOT following).
public class BWBladeDanceSlowAugment : AugmentEffectPD<BWBladeDanceSlowAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    // Public, not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve
    // {slowPercent}/{slowDuration} directly as description tokens; only public fields/properties/methods
    // are checked. "Percent" suffix at the END on slowPercent so its auto-*100 formatting fires.
    public float slowPercent = 0.35f;
    public float slowDuration = 0.5f;

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
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveStrikeListener(OnStrike);
    }

    private void OnStrike(WeaponHitData data)
    {
        // Should never be null — requiredAbilities already guarantees this augment is only ever offered to
        // a player with BWBladeDanceAbilityPD equipped — but guarded anyway, same defensive convention
        // every other GetComponent lookup in this codebase already follows.
        if (_bladeDance == null || !_bladeDance.IsActive) return;
        if (!data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy) || enemy._isDead) return;

        // multiplier = how much speed REMAINS (1 - slowPercent), not slowPercent itself — see
        // ArcherArrowRainAbilityPD's own identical slow-conversion for the same "author it as how much
        // slower, convert at the call site" reasoning. No data.onHitPower scaling — deliberate, see class
        // doc comment.
        enemy.Slow(1f - slowPercent, slowDuration);
    }
}
