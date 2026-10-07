using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. Every deathsPerStack enemy deaths (any enemy worth anything, any player's kill —
// same "reacts to every death" scope as OnEnemyDeathHealthRegenAugment, not a per-kill event), grants
// bonusMaxHealthPerStack * Stats.StackPower bonus max health. No cap — infinitely scales with how many
// enemies happen to die over the course of a run.
//
// EXCLUDES 0-xpValue deaths specifically (2026-08-21) — see OnEnemyDeath's own doc comment. Currently the
// only real-world case is EnemySpawnerPD's own boss-wave plebs (xpMultiplier 0 while a boss is alive), which
// were contributing disproportionately to this augment's stacking relative to how little killing one is
// actually worth. Deliberately scoped to just this augment's own handler, not the shared OnEnemyDeath event
// — every other on-death reaction still fires on these deaths normally.
//
// EnemyBasePD's SilentDespawn (difficulty-pacing wipe, e.g. right before a boss spawns) deliberately never
// calls RaiseOnEnemyDeath at all (see its own doc comment) — only a real Die() does — so a boss-spawn wipe
// never counts toward deathCount here, nothing extra needed on this end.
//
// deathCount/stackCount both live in predicted State even though the mutation happens directly inside the
// OnEnemyDeath handler rather than Simulate() — CharacterStatsPD.OnEnemyDeath is itself already a
// verified-state, edge-detected reaction (fired once from Die()), so mutating currentState and calling
// Stats.AddMaxHealthFlat directly from here is safe, same as OnEnemyDeathHealthRegenAugment's own direct
// Stats.Heal() call from the same event.
//
// Recomputes its OWN total contribution from scratch on every new stack (removing exactly what it last
// applied, then reapplying fresh against the CURRENT StackPower) rather than incrementally adding per stack —
// same "re-derive fully, don't incrementally adjust" principle SurgeStackingAttackSpeedAugment already uses.
// Also reapplies immediately the instant StackPower itself changes (2026-09-02, CharacterStatsPD.
// AddStackPowerChangedListener), not just lazily whenever the next stack happens to come in — so a
// StackPowerAugment picked mid-run retroactively boosts every stack already earned right away, not on a
// delay tied to the next enemy death.
public class OnEnemyDeathHealthStackingAugment : AugmentEffectPD<OnEnemyDeathHealthStackingAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public int deathCount;
        public int stackCount;
        public void Dispose() { }
    }

    // Public (not [SerializeField] private) so AugmentDescriptionFormatter's reflection can resolve
    // {deathsPerStack}/{bonusMaxHealthPerStack} tokens directly in the description text — see its own doc
    // comment (only public fields/properties/methods are checked).
    public int   deathsPerStack         = 25;
    public float bonusMaxHealthPerStack = 1f;

    // Plain view-layer bookkeeping, not State — tracks what this effect has ACTUALLY applied to
    // CharacterStatsPD so it can be reversed exactly, same role as SurgeStackingAttackSpeedAugment's own
    // _lastAppliedBonus.
    private float _lastAppliedBonus;

    // Current total Max Health this augment has actually granted so far (bonusMaxHealthPerStack * current
    // stack count * StackPower) for AugmentDescriptionFormatter's {LiveStackedMaxHealth} token. Reads
    // currentState.stackCount directly — safe (2026-08-19) because AugmentDescriptionFormatter.Format
    // resolves token methods against the real per-player spawned instance (via AugmentManagerPD's own
    // registry) rather than the dead effectPrefab template whenever one exists, so `this` here is the real
    // live instance once picked. Falls back to the prefab's own untouched 0 default for a not-yet-picked
    // level-up option, which is exactly the "0 stacks" state we want to show there anyway. Defaults to "0"
    // (not blank) outside a live match too — 0 IS the real current value in both cases, not a missing one.
    public string LiveStackedMaxHealth(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(bonusMaxHealthPerStack * stats.StackPower * currentState.stackCount)}"
            : "0";

    protected override void OnApply()
    {
        CharacterStatsPD.OnEnemyDeath += OnEnemyDeath;
        // Re-values every already-earned stack the instant StackPower itself changes (2026-09-02), not just
        // lazily on the next new stack — see CharacterStatsPD._onStackPowerChanged's own doc comment.
        Stats.AddStackPowerChangedListener(OnStackPowerChanged);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        CharacterStatsPD.OnEnemyDeath -= OnEnemyDeath;
        if (Stats != null) Stats.RemoveStackPowerChangedListener(OnStackPowerChanged);
    }

    private void OnEnemyDeath(CharacterStatsPD killer, IEnemyPD enemy)
    {
        // Excludes "worth nothing" deaths entirely (2026-08-21) — specifically EnemySpawnerPD's own
        // boss-wave plebs (spawned with xpMultiplier 0 while a boss is alive, so xpValue reads 0 by the time
        // they die), per explicit request: they were contributing disproportionately to this augment's own
        // stacking relative to how little a real kill on one is actually worth, repeatedly forcing this
        // augment's other numbers to be nerfed to compensate. Deliberately scoped to ONLY this handler, not
        // CharacterStatsPD.OnEnemyDeath itself — every other OnEnemyDeath reaction (OnEnemyDeathHealthRegenAugment,
        // OnKillLaunchEnemiesAugment, etc.) still fires on these deaths exactly as before; this one alone
        // treats them as if they never happened at all — not counted toward deathCount, no partial credit.
        if (enemy.XpValue <= 0f) return;

        currentState.deathCount++;
        if (currentState.deathCount < deathsPerStack) return;

        currentState.deathCount -= deathsPerStack;
        currentState.stackCount++;
        ReapplyMaxHealthBonus();
    }

    // Re-derives this augment's OWN total Max Health contribution from scratch (remove exactly what was
    // last applied, reapply fresh against the CURRENT stackCount/StackPower) — shared by both triggers that
    // can change the total: earning a new stack (OnEnemyDeath above) and StackPower itself changing
    // (OnStackPowerChanged below), so the two can never drift out of sync with each other.
    private void ReapplyMaxHealthBonus()
    {
        Stats.AddMaxHealthFlat(-_lastAppliedBonus);
        _lastAppliedBonus = bonusMaxHealthPerStack * currentState.stackCount * Stats.StackPower;
        Stats.AddMaxHealthFlat(_lastAppliedBonus);
    }

    // Fires whenever Stats.StackPower itself changes (e.g. a StackPowerAugment picked mid-run) — reapplies
    // immediately rather than leaving every already-earned stack sitting at a stale total until the next
    // qualifying enemy death happens to come in. No-op at 0 stacks (nothing earned yet to re-value).
    private void OnStackPowerChanged()
    {
        if (Stats == null || Stats.IsDead || currentState.stackCount <= 0) return;
        ReapplyMaxHealthBonus();
    }
}
