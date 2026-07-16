using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/PhantomBladeOnCritAugment.cs. Old system subscribed to
// global static events (WeaponBase.OnCritStrike, PhantomBlade.OnSummon) filtered by `if (stats != _stats)
// return;` — a real cross-player-interaction risk baked into the architecture itself. Replaced with a
// per-player event chain: each weapon fires its own crit event -> WeaponManagerPD relays every one of this
// player's own weapons up to this player's own CharacterStatsPD -> this augment subscribes once to its own
// Stats. Every hop is a per-owner-instance relationship with no shared/static channel anywhere, so
// cross-player interaction isn't just filtered against — there's no channel for it to happen through.
//
// No custom predicted-state counting needed — the crit-progress counter is a plain field, mutated only
// from inside the PredictedEvent callback (itself guaranteed to fire exactly once per logical crit,
// deterministically, on every machine — same reasoning that already makes _onHighroll/_onHeal safe
// elsewhere in this codebase).
public class PhantomBladeOnCritAugment : AugmentEffectPD<PhantomBladeOnCritAugment.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    [SerializeField] private GameObject phantomBladePrefab;
    [SerializeField] private float bonusCritChance = 0.1f;
    [SerializeField] private float bladeDamageBonus = 0.1f;
    [SerializeField] private int   critsPerBlade = 3;
    [SerializeField] private float baseDamageMultiplier = 0.75f;
    // Matches old PhantomBlade.spawnRadius (a hardcoded const there) — exposed as a tunable field here
    // since PhantomBladePD doesn't carry the same hardcoded-constants block old's did.
    [SerializeField] private float spawnRadius = 3f;

    private int _critCount;

    protected override void OnApply()
    {
        Stats.AddCritChanceFlat(bonusCritChance);
        // Matches old's exact size += 0.5f * bladeDamageBonus ratio (OnSummon handler in both
        // PhantomBladePassiveAugment and PhantomBladeOnCritAugment).
        Stats.AddPhantomBladeDamageBonusPercent(bladeDamageBonus);
        Stats.AddPhantomBladeSizeBonusPercent(0.5f * bladeDamageBonus);
        Stats.AddCritStrikeListener(OnCritStrike);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveCritStrikeListener(OnCritStrike);
    }

    private void OnCritStrike(PredictedObjectID target)
    {
        if (++_critCount < critsPerBlade) return;
        _critCount = 0;

        if (phantomBladePrefab == null) return;
        if (!target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy) || enemy._isDead) return;

        // Fixed-radius, random-angle offset — matches old (Random.insideUnitCircle.normalized *
        // spawnRadius, a uniform random angle at a FIXED distance), ported to GameRandom for determinism,
        // same convention as KingSpikePD's remote-burst offset.
        float angle = GameRandom.instance.NextFloat(0f, 360f) * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * spawnRadius;
        Vector3 spawnPos = enemy.transform.position + offset;
        Vector3 forward = (enemy.transform.position - spawnPos).normalized;

        float rolled = WeaponDamageMath.RollDamage(baseDamageMultiplier, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
        if (isHighroll) rolled *= 1f + Stats.HighrollBonus;
        float damage = rolled * Stats.UnpenalizedWeaponDamage * (1f + Stats.phantomBladeDamageBonusPercent);
        float size   = 1f + Stats.phantomBladeSizeBonusPercent;

        var id = hierarchy.Create(phantomBladePrefab, spawnPos, Quaternion.LookRotation(forward));
        if (!id.HasValue) return;
        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out PhantomBladePD blade))
            blade.Launch(Stats, damage, size);
    }
}
