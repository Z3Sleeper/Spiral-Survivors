using UnityEngine;

// Shared by every "spawns a DummyTargetPD and needs it scaled like a real enemy" event (DPSCheckPD,
// DestroyTheTargetsPD) — pulled out to its own static utility (2026-08-21) specifically so both events stay
// IDENTICAL by construction instead of two hand-copied bodies that could quietly drift apart over time.
public static class DummyTargetScalingPD
{
    // Replicates EnemySpawnerPD's own generic per-tier AND per-Threat-Level stat scaling exactly — same
    // formula (1f + (level - 1) * growth) for both, same live growth constants (read via EnemySpawnerPD's
    // own public getters, not hand-copied numbers that could drift), same ConfigureSpawn() API every
    // spawner-spawned enemy already goes through, same category-agnostic shape (difficulty scaling applies
    // uniformly to every category in EnemySpawnerPD, never gated — see DifficultyDamageGrowthPerLevel's own
    // doc comment). XP deliberately gets NO difficulty scalar, matching EnemySpawnerPD's own explicit design
    // ("the game isn't harder if players are also leveling up faster to match") — tier scaling only for XP.
    //
    // Health ALSO gets an extra coopScale layer on top (2026-08-21, per explicit design for these events
    // specifically) — 1f + (FixedPlayerCount - 1) * CoopToughnessScalar, the exact same formula
    // EnemySpawnerPD computes for its own elite/boss coop scaling, but applied to health ONLY here (not
    // damage/xp/moveSpeed — a dummy target's own damage output/move speed/XP value don't need to scale with
    // player count the way its health pool does, so this deliberately doesn't mirror how elites/bosses get
    // coopScale applied to multiple stats at once).
    public static void ApplyTierScaling(IEnemyPD dummy, int currentTier)
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

        float healthMultiplier    = (1f + (currentTier - 1) * spawner.HealthTierGrowth) * difficultyHealthScalar * coopScale;
        float damageMultiplier    = (1f + (currentTier - 1) * spawner.DamageTierGrowth) * difficultyDamageScalar;
        float xpMultiplier        = 1f + (currentTier - 1) * spawner.XpTierGrowth;
        float moveSpeedMultiplier = (1f + (currentTier - 1) * spawner.MoveSpeedTierGrowth) * difficultyMoveSpeedScalar;

        dummy.ConfigureSpawn(EnemySpawnCategory.Normal, healthMultiplier, damageMultiplier, xpMultiplier, moveSpeedMultiplier, 1f, currentTier);
    }
}
