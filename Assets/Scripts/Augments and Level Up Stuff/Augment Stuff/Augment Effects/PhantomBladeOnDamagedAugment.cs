using PurrNet.Prediction;
using UnityEngine;

// Not a port — the user's current intended design supersedes old's OnDamaged/OnSummon stat-bonus behavior
// entirely. Purely a trigger: when this player takes damage, summon a Phantom Blade at the closest enemy
// (not the enemy that dealt the damage — same "closest enemies" targeting convention as
// PhantomBladePassiveAugment/PhantomBladeOnDashAugment) for 100%* unpenalized damage, plus the same +10%
// Phantom Blade damage bonus every other family member grants. Subscribes to CharacterStatsPD._onDamaged
// (fires from TakeDamage on any positive damage taken, excluding a killing blow) — per-owner-instance, same
// cross-player-safety reasoning as every other Phantom Blade trigger in this family.
public class PhantomBladeOnDamagedAugment : AugmentEffectPD<PhantomBladeOnDamagedAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // Large enough to reliably reach enemies out at EnemySpawnerPD's own max spawn distance (40) with room
    // to spare — not exposed as its own tunable, matching PhantomBladePassiveAugment/PhantomBladeOnDashAugment.
    private const float TargetSearchRadius = 50f;

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private int   bladeCount = 1;
    [SerializeField] private float damageMultiplier = 0.1f;
    [SerializeField] private float phantomBladeDamageBonus = 0.1f;

    protected override void OnApply()
    {
        // Matches PhantomBladeOnCritAugment's exact size += 0.5f * phantomBladeDamageBonus ratio.
        Stats.AddPhantomBladeDamageBonus(phantomBladeDamageBonus);
        Stats.AddPhantomBladeSizeBonus(0.5f * phantomBladeDamageBonus);
        Stats.AddDamagedListener(OnDamaged);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveDamagedListener(OnDamaged);
    }

    private void OnDamaged(float amount)
    {
        if (Stats == null || Stats.IsDead) return;

        PhantomBladeSummonHelper.SummonAtClosestEnemies(hierarchy, phantomBladePrefab, Stats,
            Stats.transform.position, TargetSearchRadius, bladeCount, (damageMultiplier * amount));
    }
}
