using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. When THIS player kills an enemy, the corpse launches directly away from the killer
// at a constant speed, acting as a moving hitbox that deals 100%* unpenalized damage (rolled once, at the
// moment of the kill — not re-rolled per enemy it passes through) to every other enemy it touches along the
// way. Once it's travelled its full distance, it dies for real.
//
// Subscribes to the general CharacterStatsPD.OnEnemyDeath (fires on every machine for every enemy death,
// carrying the killer) and filters killer == Stats itself, rather than anything killer-local-machine-only —
// the corpse's own launch state lives on the ENEMY (a shared/deterministic entity every machine simulates
// identically), so the reaction that sets it up has to run identically everywhere too, not just on the
// killing player's own client.
//
// The actual launch mechanics (movement, sphere-cast damage, ending the effect) all live on EnemyBasePD
// itself (StartKillLaunch/HandleKillLaunch), not here — this augment has no ongoing per-tick relationship
// with the corpse once it fires the kill, and any enemy type can be the one that dies.
public class OnKillLaunchEnemiesAugment : AugmentEffectPD<OnKillLaunchEnemiesAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float launchSpeed    = 10f;
    [SerializeField] private float launchDistance = 10f;
    [SerializeField] private float damagePercent   = 1f;

    protected override void OnApply()
    {
        // Tells EnemyBasePD.Die() to reserve one death-hold for this player's kills up front — see
        // CharacterStatsPD.killEventCount's own doc comment for why this is counted rather than a bool
        // flag toggled during OnEnemyDeath dispatch (that raced against deletion).
        Stats.AddKillEventCount();
        CharacterStatsPD.OnEnemyDeath += OnEnemyDeath;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        CharacterStatsPD.OnEnemyDeath -= OnEnemyDeath;
    }

    private void OnEnemyDeath(CharacterStatsPD killer, IEnemyPD enemy)
    {
        if (killer != Stats) return;

        // Inherits the killing hit's own crit status rather than rolling an independent chance — whether
        // THIS specific hit crit is already decided, so this just reads it off the enemy (see
        // EnemyBasePD.TakeDamage's own killedByCrit capture).
        bool isCrit = enemy.WasKilledByCrit;

        // Anything that deals damage rolls luck variance — no exceptions. Rolled once here, not re-rolled
        // per enemy the launched corpse later touches — same "whether it crits is a property of the whole
        // flight" reasoning PhantomBladeSummonHelper.SummonAtTarget uses, just with crit status inherited
        // instead of independently rolled.
        float rolled = WeaponDamageMath.RollDamage(damagePercent, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
        if (isHighroll) rolled *= 1f + Stats.HighrollBonus;
        if (isCrit) rolled *= Stats.CritDamage;
        float damage = rolled * Stats.UnpenalizedWeaponDamage;

        Vector3 direction = enemy.transform.position - Stats.transform.position;
        direction.y = 0f;

        enemy.StartKillLaunch(direction, launchSpeed, launchDistance, damage, isCrit, isHighroll);
    }
}
