using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Payload for CharacterStatsPD._onPhantomBladeSummon — fired once per "primary" Phantom Blade summon
// (every SummonAtTarget call whose notifySummon param is left true, i.e. every real trigger: crit,
// passive tick, dash, onhit), carrying the already-fully-resolved damage/size so a listener like
// PhantomBladeCarnivalAugment can spawn an exact duplicate without re-rolling luck variance. target uses
// PredictedObjectID (not a raw IEnemyPD reference), matching WeaponHitData's own target field, so
// listeners resolve it the same TryGetComponent<IEnemyPD>(predictionManager, ...) way OnHitDamageAugment
// already does.
public struct PhantomBladeSummonData
{
    public PredictedObjectID target;
    public float damage;
    public float size;
}

// Shared by every Phantom Blade summoning augment — PhantomBladeOnCritAugment already knows its single
// target from the crit event (SummonAtTarget), while PhantomBladePassiveAugment/PhantomBladeOnDashAugment
// target the N closest enemies to the player (SummonAtClosestEnemies). Centralizing the actual spawn math
// (radius/damage roll/positioning) here means every Phantom Blade augment stays in lockstep instead of
// each one carrying its own copy that could silently drift out of sync.
public static class PhantomBladeSummonHelper
{
    private static readonly Collider[] _overlapResults = new Collider[32];
    private static readonly List<IEnemyPD> _found = new();

    // Finds up to `summonCount` closest, unique, alive enemies within `searchRadius` of `origin` and
    // summons one blade at each — nearest first. If fewer valid enemies exist than `summonCount`, the
    // excess blades all target the closest one (index 0) instead, per the family's stated design (unique
    // targets are preferred, but never wasting a summon just because targets ran out).
    public static void SummonAtClosestEnemies(PredictedHierarchy hierarchy, GameObject phantomBladePrefab,
        CharacterStatsPD stats, Vector3 origin, float searchRadius, int summonCount, float baseDamageMultiplier)
    {
        if (phantomBladePrefab == null || summonCount <= 0) return;

        _found.Clear();
        int n = Physics.OverlapSphereNonAlloc(origin, searchRadius, _overlapResults, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var enemy = _overlapResults[i].GetComponent<IEnemyPD>();
            if (enemy == null || enemy._isDead || _found.Contains(enemy)) continue;
            _found.Add(enemy);
        }
        if (_found.Count == 0) return;

        _found.Sort((a, b) =>
            Vector3.SqrMagnitude(a.transform.position - origin).CompareTo(Vector3.SqrMagnitude(b.transform.position - origin)));

        for (int i = 0; i < summonCount; i++)
        {
            var target = _found[i < _found.Count ? i : 0];
            SummonAtTarget(hierarchy, phantomBladePrefab, stats, target, baseDamageMultiplier);
        }
    }

    // Rolls a fresh damage/size total and spawns the "primary" blade for a real trigger (crit, passive
    // tick, dash, onhit) — the one path that also notifies _onPhantomBladeSummon (unless notifySummon is
    // explicitly suppressed) so PhantomBladeCarnivalAugment (or any future listener) can react.
    public static void SummonAtTarget(PredictedHierarchy hierarchy, GameObject phantomBladePrefab,
        CharacterStatsPD stats, IEnemyPD target, float baseDamageMultiplier, bool notifySummon = true)
    {
        // Two separate multiplicative layers, not one combined pool — every non-size Phantom Blade
        // augment's own +size% sums additively into phantomBladeSizeBonus first, then
        // phantomBladeSizeBonusPercent (PhantomBladeSizeAugment's own bonus) scales that already-combined
        // total, then phantomBladeSizePenalty (e.g. PhantomBladeCarnivalAugment's own tradeoff) scales it
        // down again. Same shape as the damage calc below.
        float size = (1f + stats.phantomBladeSizeBonus) * stats.phantomBladeSizeBonusPercent * stats.phantomBladeSizePenalty;

        float rolled = WeaponDamageMath.RollDamage(baseDamageMultiplier, stats.LuckChance, stats.LuckFloor, stats.LuckCeiling, stats.Highroll, out bool isHighroll);
        if (isHighroll) rolled *= 1f + stats.HighrollBonus;
        // Every non-size Phantom Blade augment's own +damage% sums additively into phantomBladeDamageBonus
        // first, then phantomBladeDamageBonusPercent (PhantomBladeSizeAugment's own bonus) scales that
        // already-combined total on top, so e.g. three +10% augments plus a +50% size bonus yields
        // 1.3 * 1.5 = 195% rather than a flat 180% — then phantomBladeDamagePenalty scales the result down
        // again for tradeoff augments like PhantomBladeCarnivalAugment.
        float damage = rolled * stats.UnpenalizedWeaponDamage
            * (1f + stats.phantomBladeDamageBonus) * stats.phantomBladeDamageBonusPercent * stats.phantomBladeDamagePenalty;

        SpawnBlade(hierarchy, phantomBladePrefab, stats, target, damage, size);

        // Structurally recursion-proof, not just guarded: SummonCopyAtTarget (below) never calls back into
        // SummonAtTarget, so there is no path from a copy back into this notify no matter how many
        // "hit summon, do something" listeners end up subscribed.
        if (notifySummon)
            stats.NotifyPhantomBladeSummon(new PhantomBladeSummonData { target = target.ObjectId, damage = damage, size = size });
    }

    // "Exact copy" of an already-resolved primary summon — same damage/size (no fresh roll), but a brand
    // new random radial offset (via SpawnBlade below) so it visibly flies in from a different direction.
    // Deliberately never notifies _onPhantomBladeSummon itself, and deliberately doesn't accept a
    // baseDamageMultiplier to roll its own damage — PhantomBladeCarnivalAugment calls this specifically so
    // its own secondary blade can't recursively trigger another copy.
    public static void SummonCopyAtTarget(PredictedHierarchy hierarchy, GameObject phantomBladePrefab,
        CharacterStatsPD stats, IEnemyPD target, float damage, float size)
    {
        SpawnBlade(hierarchy, phantomBladePrefab, stats, target, damage, size);
    }

    // Fixed-radius, random-angle offset around the target — matches old (Random.insideUnitCircle.normalized
    // * spawnRadius, a uniform random angle at a fixed distance), ported to GameRandom for determinism, same
    // convention as KingSpikePD's remote-burst offset. Shared by both the primary and copy paths, so every
    // call (including a Carnival copy of the same primary summon) independently rolls its own fresh angle.
    private static void SpawnBlade(PredictedHierarchy hierarchy, GameObject phantomBladePrefab,
        CharacterStatsPD stats, IEnemyPD target, float damage, float size)
    {
        float radius = PhantomBladePD.BaseSpawnRadius * size;
        float angle     = GameRandom.instance.NextFloat(0f, 360f) * Mathf.Deg2Rad;
        Vector3 offset  = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
        Vector3 spawnPos = target.transform.position + offset;
        Vector3 forward  = (target.transform.position - spawnPos).normalized;

        var id = hierarchy.Create(phantomBladePrefab, spawnPos, Quaternion.LookRotation(forward));
        if (!id.HasValue) return;
        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out PhantomBladePD blade))
            blade.Summon(stats, damage, size);
    }
}
