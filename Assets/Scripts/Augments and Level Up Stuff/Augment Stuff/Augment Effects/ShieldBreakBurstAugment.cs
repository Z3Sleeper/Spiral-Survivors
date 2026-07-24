using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// New tech — when your shield breaks (CharacterStatsPD._onShieldBreak), become invincible for 1s, deal
// 200%* unpenalized damage to nearby enemies, knock them back, and stun them for 1s. No onhit trigger
// (unlike DashExplosionAugment) — not part of this augment's spec. Rolls its own independent crit per enemy
// (same as DashExplosionAugment/PhantomBladeSummonHelper) since this is a direct damage source.
//
// Knockback here is deliberately NOT Stats.Knockback/baseKnockback — it's an augment-owned base value
// (baseKnockback below, default 10) that only scales with Stats.knockbackMultiplier, per spec ("the
// knockback value is not inherited but rather a trait of the augment").
//
// The stun is the first real caller of the public IEnemyPD.Stun(duration) entry point anywhere in this
// codebase, but NOT a new mechanism — EnemyBasePD already has a fully generic stunTimer gate baked into its
// base Simulate() (every subclass's movement/attack/dash/enrage logic already funnels through it), already
// respects stunResistant/crowdControlResist/isStunnable/IsReadyToAttack uniformly. This augment is just the
// first thing to use it outside knockback's own internal derived-stun path.
//
// Everything fires directly inside the PredictedEvent listener (OnShieldBreak) — same "safe because
// PredictedEvent guarantees exactly-once-per-real-occurrence" reasoning as SurgeDualWeildAugment's own
// listeners and DashExplosionAugment's own dash-end listener, both of which already call directly into
// Stats/enemy state mutation from inside a listener callback.
public class ShieldBreakBurstAugment : AugmentEffectPD<ShieldBreakBurstAugment.State>
{
    public struct State : IPredictedData<State> { public void Dispose() { } }

    [SerializeField] private float invincibilityDuration = 1f;
    [SerializeField] private float damagePercent   = 2f;
    [SerializeField] private float burstRadius     = 5f;
    [SerializeField] private float baseKnockback   = 10f;
    [SerializeField] private float stunDuration    = 1f;

    [Header("VFX")]
    [SerializeField] private GameObject radiusVFXPrefab;
    [SerializeField] private float      vfxDuration = 0.3f;

    // Static, not instance — scratch overlap buffer, same "shared, immediately drained within one
    // synchronous call" pattern as PhantomBladeSummonHelper/DashExplosionAugment.
    private static readonly Collider[] _overlapResults = new Collider[32];
    private readonly HashSet<IEnemyPD> _found = new();

    protected override void OnApply()
    {
        Stats.AddShieldBreakListener(OnShieldBreak);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (Stats != null) Stats.RemoveShieldBreakListener(OnShieldBreak);
    }

    private void OnShieldBreak()
    {
        if (Stats == null || Stats.IsDead) return;

        Stats.GrantInvincibility(invincibilityDuration);

        float knockback = baseKnockback * Stats.knockbackMultiplier;

        _found.Clear();
        int n = Physics.OverlapSphereNonAlloc(Stats.transform.position, burstRadius, _overlapResults, CombatLayerMasks.EnemyHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var enemy = _overlapResults[i].GetComponent<IEnemyPD>();
            if (enemy == null || enemy._isDead || !_found.Add(enemy)) continue;

            bool isCrit = GameRandom.instance.NextFloat() < Stats.CritChance;

            float baseDamage = Stats.UnpenalizedStrength * damagePercent;
            float rolled = WeaponDamageMath.RollDamage(baseDamage, Stats.LuckChance, Stats.LuckFloor, Stats.LuckCeiling, Stats.Highroll, out bool isHighroll);
            if (isHighroll) rolled *= 1f + Stats.HighrollBonus;
            if (isCrit) rolled *= Stats.CritDamage;

            enemy.TakeDamage(rolled, isCrit, Stats.owner ?? default);
            Stats.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolled, isCrit = isCrit, isHighroll = isHighroll, attacker = Stats, isPriority = true });

            Vector3 knockDir = (enemy.transform.position - Stats.transform.position).normalized;
            enemy.AddKnockbackVelocity(knockDir * knockback);
            enemy.Stun(stunDuration);
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
            vfx.Launch(burstRadius, vfxDuration);
    }

}
