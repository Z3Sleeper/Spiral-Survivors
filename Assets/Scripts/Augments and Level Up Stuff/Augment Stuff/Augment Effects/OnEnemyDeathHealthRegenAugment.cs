using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/HealthRegenOnEnemyDeathAugment.cs. Old system's own local
// luck-variance roll (_variancePercent, a min/max lerp by LuckChance) is gone entirely — heals don't roll
// luck at all anymore (see CharacterStatsPD.Heal's own doc comment), so this just passes the raw amount
// straight through. Subscribes to CharacterStatsPD.OnEnemyDeathDeterministic (EnemyBasePD's own
// death-detection had TODOs for this) rather than a per-kill event, since this heals off EVERY enemy death,
// not just this player's own kills — matches old subscribing to the general death event, not a kill-specific
// one.
//
// Renamed (2026-10-07, pure rename) from HealthRegenOnEnemyDeathAugment to OnEnemyDeathHealthRegenAugment —
// matches the .asset/.prefab names, which already used this word order.
//
// Moved from the plain OnEnemyDeath to OnEnemyDeathDeterministic (2026-10-16) — Stats.Heal() below rolls
// GameRandom whenever CritHealPower != 1f, and OnEnemyDeath fires from EnemyBasePD.UpdateView() at each
// machine's own render-cadence-dependent timing, which could land two different enemies' crit-heal rolls in
// a different relative order on different machines and permanently desync the shared GameRandom stream —
// see RaiseOnEnemyDeathDeterministic's own doc comment for the full reasoning.
public class OnEnemyDeathHealthRegenAugment : AugmentEffectPD<OnEnemyDeathHealthRegenAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // 2026-09-14: scales off Stats.BonusHealthRegen (was Stats.HealthRegen, the full/total stat) — see that
    // property's own doc comment. Renamed from healthRegenMultiplier to bonusHealthRegenPercent (now ends in
    // "Percent", so AugmentDescriptionFormatter's *100 convention applies directly) — the old
    // HealthRegenMultiplierPercent alias property this needed is gone, no longer necessary.
    public float bonusHealthRegenPercent = 0.1f;
    // Flat floor added on top of the scaled portion below (2026-08-12, playtesting: this augment did
    // literally nothing for a build with zero HealthRegen investment) — a pure additive constant. Public,
    // not [SerializeField] private — so AugmentDescriptionFormatter's reflection can resolve {baseHeal}
    // directly as a description token.
    public float baseHeal = 0.2f;

    // Fully accurate live preview (no per-event input to exclude — every enemy death heals the same
    // amount) for AugmentDescriptionFormatter's {LiveHealthRegenRatio} token. Includes baseHeal so the
    // preview matches what OnEnemyDeath actually heals.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseHeal + bonusHealthRegenPercent * stats.BonusHealthRegen)} "
            : "";

    protected override void OnApply()
    {
        CharacterStatsPD.OnEnemyDeathDeterministic += OnEnemyDeath;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        CharacterStatsPD.OnEnemyDeathDeterministic -= OnEnemyDeath;
    }

    private void OnEnemyDeath(CharacterStatsPD killer, IEnemyPD enemy)
    {
        Stats.Heal(baseHeal + bonusHealthRegenPercent * Stats.BonusHealthRegen);
    }
}
