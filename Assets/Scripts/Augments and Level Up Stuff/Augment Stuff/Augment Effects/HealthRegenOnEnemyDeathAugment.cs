using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/HealthRegenOnEnemyDeathAugment.cs. Old system's own local
// luck-variance roll (_variancePercent, a min/max lerp by LuckChance) is gone entirely now — Heal() itself
// rolls luck variance (WeaponDamageMath.RollDamage/Highroll) internally these days, so this just passes the
// raw amount straight through. Subscribes to the new CharacterStatsPD.OnEnemyDeath (EnemyBasePD's own
// death-detection had TODOs for this) rather than a per-kill event, since this heals off EVERY enemy death,
// not just this player's own kills — matches old subscribing to the general death event, not a kill-specific
// one.
public class HealthRegenOnEnemyDeathAugment : AugmentEffectPD<HealthRegenOnEnemyDeathAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private float healthRegenMultiplier = 0.2f;

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
        Stats.Heal(healthRegenMultiplier * Stats.HealthRegen);
    }
}
