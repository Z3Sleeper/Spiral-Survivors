using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. Every deathsPerStack enemy deaths (any enemy worth anything, any player's kill —
// same "reacts to every death" scope as HealthRegenOnEnemyDeathAugment, not a per-kill event), grants
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
// Stats.AddMaxHealthFlat directly from here is safe, same as HealthRegenOnEnemyDeathAugment's own direct
// Stats.Heal() call from the same event.
//
// Recomputes its OWN total contribution from scratch on every new stack (removing exactly what it last
// applied, then reapplying fresh against the CURRENT StackPower) rather than incrementally adding per stack —
// same "re-derive fully, don't incrementally adjust" principle SurgeStackingAttackSpeedAugment already uses,
// so a later StackPowerAugment pick retroactively boosts every stack already earned, not just future ones.
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
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(bonusMaxHealthPerStack * stats.StackPower * currentState.stackCount)} "
            : "0 ";

    protected override void OnApply()
    {
        CharacterStatsPD.OnEnemyDeath += OnEnemyDeath;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        CharacterStatsPD.OnEnemyDeath -= OnEnemyDeath;
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

        Stats.AddMaxHealthFlat(-_lastAppliedBonus);
        _lastAppliedBonus = bonusMaxHealthPerStack * currentState.stackCount * Stats.StackPower;
        Stats.AddMaxHealthFlat(_lastAppliedBonus);
    }
}
