using PurrNet.Prediction;
using UnityEngine;

// New tech, not a port. When THIS player kills an enemy, the corpse launches directly away from the killer
// at a constant speed, acting as a moving hitbox that deals 100%* unpenalized damage (rolled once, at the
// moment of the kill — not re-rolled per enemy it passes through) to every other enemy it touches along the
// way. Once it's travelled its full distance, it dies for real. Crit/highroll are both inherited from the
// killing blow itself (enemy.WasKilledByCrit/WasKilledByHighroll — see EnemyBasePD's own doc comments) rather
// than independently determined, and baked into the launch's own damage exactly once at OnEnemyDeath time —
// EnemyBasePD.HandleKillLaunch simply replicates that same pre-baked damage/isCrit/isHighroll for every enemy
// the corpse subsequently touches, never re-rolling per hit.
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

    [SerializeField] private float launchSpeed    = 20f;
    [SerializeField] private float launchDistance = 10f;
    public float damagePercent   = 0.3f;

    // Baseline (unrolled) damage for AugmentDescriptionFormatter's {LiveStrengthRatio} token — see
    // OnHitDamageHealthAugment.LiveBonusHealthRatio's own doc comment for the full convention.
    public string LiveStrengthRatio(CharacterStatsPD stats) =>
        AugmentDescriptionFormatter.ShowLiveValues && stats != null
            ? $"{AugmentDescriptionFormatter.FormatLiveNumber(damagePercent * stats.Strength)} "
            : "";

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

        // Inherits the killing hit's own crit/highroll status rather than rolling independently — whether
        // THIS specific hit crit/highrolled is already decided, so this just reads it off the enemy (see
        // EnemyBasePD.TakeDamage's own killedByCrit/killedByHighroll capture). isHighroll extended to match
        // isCrit (2026-09-14, was independently rolled) — same "onhit/onstrike effects inherit rather than
        // roll" convention every other onhit-triggered damage effect in this codebase now follows.
        bool isCrit = enemy.WasKilledByCrit;
        bool isHighroll = enemy.WasKilledByHighroll;

        // Anything that deals damage rolls luck variance — no exceptions. Rolled once here, not re-rolled
        // per enemy the launched corpse later touches — same "whether it crits/highrolls is a property of
        // the whole flight" reasoning PhantomBladeSummonHelper.SummonAtTarget uses, just with both statuses
        // inherited from the killing blow instead of independently rolled/determined.
        float rolled = WeaponDamageMath.RollDamageInherited(damagePercent, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, isHighroll);
        rolled = WeaponDamageMath.ApplyCritAndHighroll(rolled, isHighroll, isCrit, Stats.HighrollBonus, Stats.CritDamage);
        float damage = rolled * Stats.Strength;

        Vector3 direction = enemy.transform.position - Stats.transform.position;
        direction.y = 0f;

        // executeThreshold/critExecuteThreshold (2026-10-08 perf fix) — captured here, while Stats is still
        // the live reference in scope, rather than making HandleKillLaunch re-look it up via
        // CharacterStatsPD.All for every enemy this corpse later sweeps through — see
        // EnemyBasePD.StartKillLaunch's own doc comment.
        enemy.StartKillLaunch(direction, launchSpeed, launchDistance, damage, isCrit, isHighroll, Stats.executeThreshold, Stats.critExecuteThreshold);
    }
}
