using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/HealthRegenOnEnemyDeathAugment.cs. Old system's own local
// luck-variance roll (_variancePercent, a min/max lerp by LuckChance) is gone entirely — heals don't roll
// luck at all anymore (see CharacterStatsPD.Heal's own doc comment), so this just passes the raw amount
// straight through. Subscribes to the new CharacterStatsPD.OnEnemyDeath (EnemyBasePD's own
// death-detection had TODOs for this) rather than a per-kill event, since this heals off EVERY enemy death,
// not just this player's own kills — matches old subscribing to the general death event, not a kill-specific
// one.
public class HealthRegenOnEnemyDeathAugment : AugmentEffectPD<HealthRegenOnEnemyDeathAugment.State>
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
    // literally nothing for a build with zero HealthRegen investment) — a pure additive constant.
    [SerializeField] private float baseHeal = 0.2f;

    // Fully accurate live preview (no per-event input to exclude — every enemy death heals the same
    // amount) for AugmentDescriptionFormatter's {LiveHealthRegenRatio} token. Includes baseHeal so the
    // preview matches what OnEnemyDeath actually heals.
    public string LiveHealthRegenRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(baseHeal + bonusHealthRegenPercent * stats.BonusHealthRegen)} "
            : "";

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
        Stats.Heal(baseHeal + bonusHealthRegenPercent * Stats.BonusHealthRegen);
    }
}
