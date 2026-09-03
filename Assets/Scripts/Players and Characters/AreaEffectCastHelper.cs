using System;
using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Shared cast logic for anything using the "AoE Effects" AreaEffectSize/Power/Count stats — DashExplosionAugment/
// DashHealAugment/ShieldBreakBurstAugment/KnightAreaStunHealAbilityPD (all via the shared AreaEffectPD for
// multi-cast chains) — augment or ability, whatever else follows the same shape. One single Cast() method
// covers every existing combination (damage, knockback, onhit, stun, heal, self-heal-by-hit-count, ...) instead
// of a new near-duplicate method per augment — see its own doc comment for the full parameter breakdown.
// Centralizing this means every caller's immediate-cast path and any chain-retrigger path can never silently
// drift out of sync with each other, same rationale as PhantomBladeSummonHelper. Damage/onHitPower scale by
// owner.AreaEffectPower directly — callers are responsible for scaling radius by owner.AreaEffectSize
// themselves before calling in (radius is just "how big," not something this helper needs to know is
// AreaEffectSize-derived).
public static class AreaEffectCastHelper
{
    // Bumped from 32 (see PhantomBladeSummonHelper's own comment on the same pattern) — OverlapSphereNonAlloc
    // truncates once the buffer's full without any distance ordering, so a crowded 4-player fight could
    // silently skip damaging/healing some enemies/allies well within the blast radius just because they
    // didn't happen to be among the first 32 the physics broad-phase returned.
    private static readonly Collider[] _overlapResults = new Collider[128];
    private static readonly HashSet<IEnemyPD> _foundEnemies = new();

    // targetAllies: false (default) gathers enemies via OverlapSphere+EnemyHurtboxMask (same broad-phase
    // query every other combat system in this codebase uses); true gathers allies via CharacterStatsPD.All +
    // a plain distance check instead (matches CastHeal's old exact behavior, including the caster naturally
    // healing themselves since distance 0 from `position` is always <= radius) — these are fundamentally
    // different collections/queries, so this really does branch into two gather strategies internally, not
    // just cosmetically, but the surrounding cast (VFX, aggregate callback) stays shared either way.
    //
    // dealDamage/damagePercent/onHitPowerPercent/notifyOnHit: only meaningful when targetAllies is false —
    // there's no "deal damage to an ally" concept in this system, so callers targeting allies should just
    // leave dealDamage at its default (false) rather than pass it. dealDamage itself (not just
    // damagePercent == 0) gates the whole damage/onhit block — a caller that doesn't want to deal damage at
    // all (e.g. KnightAreaStunHealAbilityPD) skips a real TakeDamage(0, ...) call and a 0-damage hit-feedback
    // popup entirely, rather than just zeroing the number.
    // notifyOnHit: DashExplosionAugment wants onhit-triggered augments (OnHitDamageAugment,
    // CritTriggerOnHitAugment, etc.) to fire; ShieldBreakBurstAugment explicitly doesn't.
    // hasKnockback: deliberately its OWN gate, independent of dealDamage — a caller can want knockback with
    // no damage at all (KnightAreaStunHealAbilityPD) just as easily as damage with no knockback
    // (DashExplosionAugment's own call). Defaults to false ("no knockback at all") rather than every caller
    // needing to remember to explicitly opt out — DashExplosionAugment/ShieldBreakBurstAugment/
    // KnightAreaStunHealAbilityPD all explicitly pass hasKnockback: true. Only meaningful when hasKnockback
    // is true: knockback null means "use the default" (owner.Knockback exactly — deliberately NOT scaled by
    // AreaEffectPower, since knockback is an onhit-power-style stat, not a damage stat); a specific value
    // overrides entirely (ShieldBreakBurstAugment's own reworked knockback, itself also deliberately
    // AreaEffectPower-free).
    //
    // onEnemyHit/onAllyHit: per-target extra, invoked once per target actually hit/found (after
    // damage/knockback are applied, for enemies) — e.g. ShieldBreakBurstAugment's Stun, or a caller doing its
    // own ally.Heal(...) inline (DashHealAugment's own RollAndHeal). Exactly one of these is ever non-null for
    // a given call, matching targetAllies.
    // onCastComplete: fired once at the very end with the total hit/found count — e.g.
    // KnightAreaStunHealAbilityPD healing itself based on how many enemies got stunned. Fires even when the
    // count is 0, so a caller that cares can decide for itself whether to no-op (matches every other optional
    // callback here — this helper never assumes what "0 hits" should mean for a given caller).
    // interrupt: only meaningful when targetAllies is false — calls IEnemyPD.Interrupt() on each enemy found,
    // BEFORE damage/knockback/onEnemyHit below, not after. Ordering matters: a caller pairing this with a
    // knockback or Stun() (via onEnemyHit) needs the enemy already un-kinematic'd by Interrupt() before that
    // knockback's own velocity assignment runs, or it gets silently eaten by AddKnockbackVelocity's own
    // "not supported on a kinematic body" guard — see EnemyBasePD.Interrupt()'s own doc comment.
    // onSuccessfulInterrupt: fired only when interrupt is true AND that specific enemy's Interrupt() call
    // actually returned true — Interrupt()'s own return value is otherwise discarded here, so this is the
    // only way a caller learns "did this land." Feeds CharacterStatsPD's generic SI/FSI event pair (see
    // CharacterStatsPD.NotifyInterrupted) — e.g. KnightAreaStunHealAbilityPD.
    // overrideHitSfx/hitSfxOverride: only meaningful when dealDamage is true — see HitFeedbackData's own doc
    // comment for the full tri-state shape. false (default) plays owner.hitSfx exactly as before this
    // existed. A caller expecting many simultaneous/rapid hits per cast (ArcherArrowRainAbilityPD: up to 20
    // ticks * every enemy in radius) should pass overrideHitSfx: true and leave hitSfxOverride null for
    // silence, rather than letting owner.hitSfx spam on every single tick.
    public static void Cast(PredictedHierarchy hierarchy, CharacterStatsPD owner, Vector3 position, float radius,
        GameObject radiusVFXPrefab, bool targetAllies = false,
        bool dealDamage = false, float damagePercent = 0f, float onHitPowerPercent = 0f, bool notifyOnHit = true,
        bool hasKnockback = false, float? knockback = null,
        bool interrupt = false,
        Action<IEnemyPD> onEnemyHit = null, Action<IFriendlyPD> onAllyHit = null, Action<int> onCastComplete = null,
        Action<IEnemyPD> onSuccessfulInterrupt = null,
        bool overrideHitSfx = false, AudioClip hitSfxOverride = null)
    {
        int hitCount = 0;

        if (targetAllies)
        {
            foreach (var ally in FriendlyTargetRegistryPD.All)
            {
                if (ally.IsDead) continue;
                if (Vector3.Distance(ally.transform.position, position) > radius) continue;

                onAllyHit?.Invoke(ally);
                hitCount++;
            }
        }
        else
        {
            float actualKnockback = hasKnockback ? knockback ?? owner.Knockback : 0f;

            _foundEnemies.Clear();
            int n = Physics.OverlapSphereNonAlloc(position, radius, _overlapResults, CombatLayerMasks.EnemyHurtboxMask);
            for (int i = 0; i < n; i++)
            {
                var enemy = _overlapResults[i].GetComponent<IEnemyPD>();
                if (enemy == null || enemy._isDead || !_foundEnemies.Add(enemy)) continue;

                // First, before anything else below — see this method's own doc comment on why the
                // ordering relative to knockback/onEnemyHit matters.
                if (interrupt && enemy.Interrupt())
                    onSuccessfulInterrupt?.Invoke(enemy);

                if (dealDamage)
                {
                    bool isCrit = GameRandom.instance.NextFloat() < owner.CritChance;

                    float baseDamage = owner.Strength * damagePercent * owner.AreaEffectPower;
                    float rolledDamage = WeaponDamageMath.RollDamage(baseDamage, owner.LuckChance, owner.LuckFloor, owner.LuckCeiling, owner.Highroll, out bool dmgHighroll);
                    rolledDamage = WeaponDamageMath.ApplyCritAndHighroll(rolledDamage, dmgHighroll, isCrit, owner.HighrollBonus, owner.CritDamage);

                    enemy.TakeDamage(rolledDamage, isCrit, owner.owner ?? default);
                    owner.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolledDamage, isCrit = isCrit, isHighroll = dmgHighroll, attacker = owner, isPriority = true, overrideSfx = overrideHitSfx, sfx = hitSfxOverride });

                    if (notifyOnHit)
                    {
                        // Passed through RAW, not luck/highroll-rolled here — matches every weapon type's own
                        // convention (WeaponMeleePD/WeaponAxePD/WeaponRangedProjectilePD all pass onHitPower =
                        // Stats.OnHitPower unrolled; only the damage roll above highrolls). Rolling this
                        // separately (2026-08-19 fix) meant a single hit could highroll TWICE — once here,
                        // once more in whichever on-hit augment consumes this onHitPower for its own roll —
                        // a hit is only ever supposed to be able to highroll once.
                        float onHitPower = owner.OnHitPower * onHitPowerPercent * owner.AreaEffectPower;

                        // isCrit inherited from this same hit, matching every other onhit-notify call site.
                        owner.NotifyHit(new WeaponHitData { target = enemy.ObjectId, weaponDamage = rolledDamage, onHitPower = onHitPower, isCrit = isCrit });
                    }
                }

                // Independent of dealDamage — see this method's own doc comment for why.
                if (actualKnockback != 0f)
                {
                    Vector3 knockDir = (enemy.transform.position - position).normalized;
                    enemy.AddKnockbackVelocity(knockDir * actualKnockback);
                }

                onEnemyHit?.Invoke(enemy);
                hitCount++;
            }
        }

        onCastComplete?.Invoke(hitCount);
    }

    // Public (not called from Cast() itself) — a caller that retriggers Cast() multiple times (AreaEffectPD's
    // own totalCasts/retriggerRate) needs the radius VFX to spawn exactly ONCE for the whole chain, not once
    // per retrigger (it used to be called unconditionally at the end of Cast() above, which meant a 21-tick
    // chain spawned the VFX 21 times — a real bug, not just cosmetic waste, once anything used a tick count
    // that high). AreaEffectPD now calls this itself, once, from Launch() only. Duration is NOT a parameter
    // here — RadiusIndicatorVFXPD owns its own playback timing (a [SerializeField] on the prefab itself),
    // same convention as any other prefab-authored visual effect. Only radius is gameplay-derived, so only
    // radius gets threaded in from the caller.
    public static void SpawnRadiusVFX(PredictedHierarchy hierarchy, GameObject radiusVFXPrefab, Vector3 position, float radius)
    {
        if (radiusVFXPrefab == null) return;

        var id = hierarchy.Create(radiusVFXPrefab, position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out RadiusIndicatorVFXPD vfx))
            vfx.Launch(radius);
    }
}
