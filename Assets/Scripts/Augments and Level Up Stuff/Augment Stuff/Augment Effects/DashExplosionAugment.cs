using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Ported from _OldSystemArchive/Augment Effects/DashExplosionAugment.cs — On Dash End, deals unpenalized-
// weapon-damage-scaled AOE damage to nearby enemies and triggers onhit listeners (OnHitDamageAugment,
// CritTriggerOnHitAugment, etc.) on each one via Stats.NotifyHit — same non-weapon-hit-source pattern
// PhantomBladePD uses. Rolls luck variance independently for both the damage and the onhit-power figure
// (both "*" annotated), same as every other damage effect. Deviates from old here: this is a direct damage
// source (same category as PhantomBladePD), so it rolls its own independent crit per enemy
// (GameRandom.instance.NextFloat() < Stats.CritChance), same as PhantomBladeSummonHelper.SummonAtTarget —
// old never crit, but every other direct-damage source in the new system does. No longer grants a flat
// dashHaste bonus (removed per the "no training wheels" balance pass). Subscribes directly to this player's
// own PlayerControllerPD._onDashEnded, same per-owner-instance cross-player-safety reasoning as
// PhantomBladeOnDashAugment.
public class DashExplosionAugment : AugmentEffectPD<DashExplosionAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float damagePercent     = 2f;
    [SerializeField] private float onHitPowerPercent = 2f;
    [SerializeField] private float explosionRadius   = 5f;

    [Header("VFX")]
    [SerializeField] private GameObject radiusVFXPrefab;
    [SerializeField] private float      vfxDuration = 0.3f;

    private PlayerControllerPD _controller;

    // Static, not instance — scratch overlap buffer, same "shared, immediately drained within one
    // synchronous call" pattern as PhantomBladeSummonHelper._overlapResults.
    private static readonly Collider[] _overlapResults = new Collider[32];
    private readonly HashSet<IEnemyPD> _found = new();

    protected override void OnApply()
    {
        _controller = Stats.GetComponent<PlayerControllerPD>();
        if (_controller != null) _controller.AddDashEndedListener(OnDashEnded);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (_controller != null) _controller.RemoveDashEndedListener(OnDashEnded);
    }

    private void OnDashEnded()
    {
        if (Stats == null || Stats.IsDead) return;

        _found.Clear();
        int n = Physics.OverlapSphereNonAlloc(Stats.transform.position, explosionRadius, _overlapResults, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var enemy = _overlapResults[i].GetComponent<IEnemyPD>();
            if (enemy == null || enemy._isDead || !_found.Add(enemy)) continue;

            bool isCrit = GameRandom.instance.NextFloat() < Stats.CritChance;

            float baseDamage = Stats.UnpenalizedStrength * damagePercent;
            float rolledDamage = WeaponDamageMath.RollDamage(baseDamage, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool dmgHighroll);
            if (dmgHighroll) rolledDamage *= 1f + Stats.HighrollBonus;
            if (isCrit) rolledDamage *= Stats.CritDamage;

            enemy.TakeDamage(rolledDamage, isCrit, Stats.owner ?? default);
            Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolledDamage, isCrit = isCrit, isHighroll = dmgHighroll, attacker = Stats, isPriority = true });

            float baseOnHitPower = Stats.OnHitPower * onHitPowerPercent;
            float rolledOnHitPower = WeaponDamageMath.RollDamage(baseOnHitPower, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool onHitHighroll);
            if (onHitHighroll) rolledOnHitPower *= 1f + Stats.HighrollBonus;

            // isCrit inherited from this same hit, matching every other onhit-notify call site.
            Stats.NotifyHit(new WeaponHitData { target = enemy.ObjectId, weaponDamage = rolledDamage, onHitPower = rolledOnHitPower, isCrit = isCrit });
        }

        SpawnRadiusVFX();
    }

    private void SpawnRadiusVFX()
    {
        if (radiusVFXPrefab == null) return;

        var id = hierarchy.Create(radiusVFXPrefab, Stats.transform.position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out RadiusIndicatorVFXPD vfx))
            vfx.Launch(explosionRadius, vfxDuration);
    }

}
