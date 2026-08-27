using UnityEngine;

// Shared by every "spawns an IEnemyPD target and needs it scaled like a real enemy" event (DPSCheckPD,
// DestroyTheTargetsPD, PerimeterPatrolEventPD) — pulled out to its own static utility (2026-08-21)
// specifically so every caller stays IDENTICAL by construction instead of hand-copied bodies that could
// quietly drift apart over time. Named EventTargetScalingPD (renamed 2026-08-27, was DummyTargetScalingPD) —
// PerimeterPatrolGloomPD is a genuine patrolling enemy, not a dummy, so the old name was misleading once it
// became one of this utility's callers.
public static class EventTargetScalingPD
{
    // Replicates EnemySpawnerPD's own generic per-tier AND per-Threat-Level stat scaling exactly — same
    // formula (1f + (level - 1) * growth) for both, same live growth constants (read via EnemySpawnerPD's
    // own public getters, not hand-copied numbers that could drift), same ConfigureSpawn() API every
    // spawner-spawned enemy already goes through, same category-agnostic shape (difficulty scaling applies
    // uniformly to every category in EnemySpawnerPD, never gated — see DifficultyDamageGrowthPerLevel's own
    // doc comment). XP deliberately gets NO difficulty scalar even when scaleXp is true, matching
    // EnemySpawnerPD's own explicit design ("the game isn't harder if players are also leveling up faster
    // to match") — tier scaling only for XP.
    //
    // Health AND XP also get an extra coopScale layer on top when scaleHealth/scaleXp is true (health since
    // 2026-08-21; XP added 2026-08-27, explicit design correction — XP was originally left unscaled on the
    // reasoning that "the game isn't harder if players are also leveling up faster to match," but per
    // explicit user instruction these events' health-bar targets should coop-scale XP the same way
    // EnemySpawnerPD's own elites/bosses do) — 1f + (FixedPlayerCount - 1) * CoopToughnessScalar, the exact
    // same formula EnemySpawnerPD computes for its own elite/boss coop scaling. Damage/moveSpeed still do
    // NOT get this layer — a spawned target's own damage output/move speed don't need to scale with player
    // count the way its health pool and XP payout do.
    //
    // scaleHealth/scaleDamage/scaleXp/scaleMoveSpeed (2026-08-26, consolidated from two separate methods —
    // ApplyTierScaling scaled all four unconditionally, a since-removed ApplyHealthAndXpTierScaling scaled
    // only health+xp) — opt-OUT, all default true, so every existing call site (DPSCheckPD/
    // DestroyTheTargetsPD, and PerimeterPatrolEventPD as of this pass) that wants every stat scaled just
    // calls ApplyTierScaling(target, currentTier) with no extra arguments, identical to the old
    // unconditional behavior. A future caller that wants a stat left untouched (e.g. a target with no
    // damage output of its own) passes that one flag false explicitly — see EnemyBasePD.ConfigureSpawn's
    // own implementation for why passing a 1f multiplier for damage/moveSpeed is a true no-op there
    // (hard-gated behind `!= 1f`), which is what setting a flag false resolves to below.
    public static void ApplyTierScaling(IEnemyPD target, int currentTier,
        bool scaleHealth = true, bool scaleDamage = true, bool scaleXp = true, bool scaleMoveSpeed = true)
    {
        if (EnemySpawnerPD.instance == null) return;
        var spawner = EnemySpawnerPD.instance;

        int difficulty = GameManagerPD.instance != null ? GameManagerPD.instance.Difficulty : 1;
        float difficultyHealthScalar    = 1f + (difficulty - 1) * spawner.DifficultyHealthGrowthPerLevel;
        float difficultyDamageScalar    = 1f + (difficulty - 1) * spawner.DifficultyDamageGrowthPerLevel;
        float difficultyMoveSpeedScalar = 1f + (difficulty - 1) * spawner.DifficultyMoveSpeedGrowthPerLevel;

        int playerCount = GameManagerPD.instance != null ? GameManagerPD.instance.FixedPlayerCount : 1;
        float coopToughnessScalar = GameManagerPD.instance != null ? GameManagerPD.instance.CoopToughnessScalar : 0f;
        float coopScale = 1f + (playerCount - 1) * coopToughnessScalar;

        float healthMultiplier    = scaleHealth    ? (1f + (currentTier - 1) * spawner.HealthTierGrowth) * difficultyHealthScalar * coopScale : 1f;
        float damageMultiplier    = scaleDamage    ? (1f + (currentTier - 1) * spawner.DamageTierGrowth) * difficultyDamageScalar : 1f;
        float xpMultiplier        = scaleXp        ? (1f + (currentTier - 1) * spawner.XpTierGrowth) * coopScale : 1f;
        float moveSpeedMultiplier = scaleMoveSpeed ? (1f + (currentTier - 1) * spawner.MoveSpeedTierGrowth) * difficultyMoveSpeedScalar : 1f;

        target.ConfigureSpawn(EnemySpawnCategory.Normal, healthMultiplier, damageMultiplier, xpMultiplier, moveSpeedMultiplier, 1f, currentTier);
    }
}
