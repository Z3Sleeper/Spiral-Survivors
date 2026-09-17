using UnityEngine;

// Shared by every "spawns an IEnemyPD target and needs it scaled like a real enemy" event (DPSCheckPD,
// DestroyTheTargetsPD, PerimeterPatrolEventPD) — pulled out to its own static utility (2026-08-21)
// specifically so every caller stays IDENTICAL by construction instead of hand-copied bodies that could
// quietly drift apart over time. Named EventTargetScalingPD (renamed 2026-08-27, was DummyTargetScalingPD) —
// PerimeterPatrolGloomPD is a genuine patrolling enemy, not a dummy, so the old name was misleading once it
// became one of this utility's callers.
public static class EventTargetScalingPD
{
    // Replicates EnemySpawnerPD's own generic per-tier AND per-Threat-Level stat scaling exactly — tier
    // scaling is read via EnemySpawnerPD's own public ComputeTier*Multiplier(tierNumber) methods (2026-09-16,
    // was each formula hand-copied here from the raw growth constants — that copy silently fell out of sync
    // with health's own shape once health stopped using the shared "1 + (tier-1)*growth" shape every other
    // stat still uses; see EnemySpawnerPD.ComputeTierHealthMultiplier's own doc comment). Difficulty scaling
    // is still hand-applied below since EnemySpawnerPD has no equivalent Compute method for it yet — same
    // category-agnostic shape either way (difficulty scaling applies uniformly to every category in
    // EnemySpawnerPD, never gated — see DifficultyDamageGrowthPerLevel's own doc comment). XP deliberately
    // gets NO difficulty scalar even when scaleXp is true, matching EnemySpawnerPD's own explicit design
    // ("the game isn't harder if players are also leveling up faster to match") — tier scaling only for XP.
    //
    // Health AND points also get an extra coopScale layer on top when scaleHealth/scaleXp is true (health
    // since 2026-08-21; XP+points added 2026-08-27, explicit design correction — XP was originally left
    // unscaled on the reasoning that "the game isn't harder if players are also leveling up faster to
    // match," but per explicit user instruction these events' health-bar targets should coop-scale XP the
    // same way EnemySpawnerPD's own elites/bosses do) — 1f + (FixedPlayerCount - 1) * CoopToughnessScalar,
    // the exact same formula EnemySpawnerPD computes for its own coop scaling.
    //
    // XP itself was reverted back OFF this layer on 2026-09-03 (see EnemySpawnerPD's own coopScale doc
    // comment for the full reasoning) — these events always spawn exactly one target regardless of player
    // count, same fixed-count shape as a boss, so flat (uncoop-scaled) XP is what keeps total XP earned from
    // clearing one of these equal between solo and co-op. Points keeps the coopScale bump, matching every
    // other category. Damage/moveSpeed still do NOT get this layer — a spawned target's own damage output/
    // move speed don't need to scale with player count the way its health pool and points payout do.
    //
    // scaleHealth/scaleDamage/scaleXp/scaleMoveSpeed/scaleAttackSpeed (2026-08-26, consolidated from two
    // separate methods — ApplyTierScaling scaled all four unconditionally, a since-removed
    // ApplyHealthAndXpTierScaling scaled only health+xp; scaleAttackSpeed added 2026-08-31 alongside
    // EnemySpawnerPD's own new difficultyAttackSpeedGrowthPerLevel) — opt-OUT, all default true, so every
    // existing call site (DPSCheckPD/DestroyTheTargetsPD, and PerimeterPatrolEventPD as of this pass) that
    // wants every stat scaled just calls ApplyTierScaling(target, currentTier) with no extra arguments,
    // identical to the old unconditional behavior. A future caller that wants a stat left untouched (e.g. a
    // target with no damage output of its own) passes that one flag false explicitly — see
    // EnemyBasePD.ConfigureSpawn's own implementation for why passing a 1f multiplier for damage/moveSpeed/
    // attackSpeed is a true no-op there (hard-gated behind `!= 1f`), which is what setting a flag false
    // resolves to below.
    public static void ApplyTierScaling(IEnemyPD target, int currentTier,
        bool scaleHealth = true, bool scaleDamage = true, bool scaleXp = true, bool scaleMoveSpeed = true, bool scaleAttackSpeed = true)
    {
        if (EnemySpawnerPD.instance == null) return;
        var spawner = EnemySpawnerPD.instance;

        int difficulty = GameManagerPD.instance != null ? GameManagerPD.instance.Difficulty : 1;
        float difficultyHealthScalar      = 1f + (difficulty - 1) * spawner.DifficultyHealthGrowthPerLevel;
        float difficultyDamageScalar      = 1f + (difficulty - 1) * spawner.DifficultyDamageGrowthPerLevel;
        float difficultyMoveSpeedScalar   = 1f + (difficulty - 1) * spawner.DifficultyMoveSpeedGrowthPerLevel;
        float difficultyAttackSpeedScalar = 1f + (difficulty - 1) * spawner.DifficultyAttackSpeedGrowthPerLevel;

        int playerCount = GameManagerPD.instance != null ? GameManagerPD.instance.FixedPlayerCount : 1;
        float coopToughnessScalar = GameManagerPD.instance != null ? GameManagerPD.instance.CoopToughnessScalar : 0f;
        float coopScale = 1f + (playerCount - 1) * coopToughnessScalar;

        float healthMultiplier    = scaleHealth    ? spawner.ComputeTierHealthMultiplier(currentTier) * difficultyHealthScalar * coopScale : 1f;
        float damageMultiplier    = scaleDamage    ? spawner.ComputeTierDamageMultiplier(currentTier) * difficultyDamageScalar : 1f;
        // No coopScale (2026-09-03, was * coopScale) — see this method's own doc comment for why XP stays
        // flat here now, same as every other fixed-count category.
        float xpMultiplier        = scaleXp        ? spawner.ComputeTierXpMultiplier(currentTier) : 1f;
        // Reuses the scaleXp opt-out flag (no separate scalePoints param) and the same coopScale layering
        // XP gets — points is treated as XP's own "kill reward" sibling for this helper's purposes, even
        // though its own tier growth rate is fully independent from XP's (2026-08-29, see EnemyBasePD.
        // pointsValue's own doc comment for why points is no longer derived from XP at all).
        float pointsMultiplier    = scaleXp        ? spawner.ComputeTierPointsMultiplier(currentTier) * coopScale : 1f;
        float moveSpeedMultiplier = scaleMoveSpeed ? spawner.ComputeTierMoveSpeedMultiplier(currentTier) * difficultyMoveSpeedScalar : 1f;
        // Difficulty-only, no tier component — mirrors EnemySpawnerPD.SpawnEnemy's own identical
        // attackSpeedMultiplier computation.
        float attackSpeedMultiplier = scaleAttackSpeed ? difficultyAttackSpeedScalar : 1f;

        target.ConfigureSpawn(EnemySpawnCategory.Normal, healthMultiplier, damageMultiplier, xpMultiplier, pointsMultiplier, moveSpeedMultiplier, 1f, attackSpeedMultiplier, currentTier);
    }
}
