using System;
using System.Collections.Generic;
using PurrNet.Prediction;
using UnityEngine;

// Shared cast logic for anything using the "AoE Effects" AreaEffectSize stat — DashExplosionAugment/
// DashHealAugment/ShieldBreakBurstAugment/KnightBuffStunAbilityPD (all via the shared AreaEffectPD for
// multi-cast chains) — augment or ability, whatever else follows the same shape. One single Cast() method
// covers every existing combination (damage, knockback, onhit, stun, heal, self-heal-by-hit-count, ...) instead
// of a new near-duplicate method per augment — see its own doc comment for the full parameter breakdown.
// Centralizing this means every caller's immediate-cast path and any chain-retrigger path can never silently
// drift out of sync with each other, same rationale as PhantomBladeSummonHelper. Callers are responsible for
// scaling radius by owner.AreaEffectSize themselves before calling in (radius is just "how big," not
// something this helper needs to know is AreaEffectSize-derived). AreaEffectPower/AreaEffectCount (the
// damage/onhit-power multiplier and the multi-cast repeat count) were removed entirely (2026-09-13) — damage/
// onHitPower below are now each caller's own plain baseline, unscaled.
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
    // all (e.g. KnightBuffStunAbilityPD) skips a real TakeDamage(0, ...) call and a 0-damage hit-feedback
    // popup entirely, rather than just zeroing the number.
    // notifyOnHit: DashExplosionAugment wants onhit-triggered augments (OnHitDamageAugment,
    // CritTriggerOnHitAugment, etc.) to fire; ShieldBreakBurstAugment explicitly doesn't.
    // hasKnockback: deliberately its OWN gate, independent of dealDamage — a caller can want knockback with
    // no damage at all (KnightBuffStunAbilityPD) just as easily as damage with no knockback
    // (DashExplosionAugment's own call). Defaults to false ("no knockback at all") rather than every caller
    // needing to remember to explicitly opt out — DashExplosionAugment/ShieldBreakBurstAugment/
    // KnightBuffStunAbilityPD all explicitly pass hasKnockback: true. Only meaningful when hasKnockback
    // is true: knockback null means "use the default" (owner.Knockback exactly); a specific value overrides
    // entirely (ShieldBreakBurstAugment's own reworked knockback).
    //
    // onEnemyHit/onAllyHit: per-target extra, invoked once per target actually hit/found (after
    // damage/knockback are applied, for enemies) — e.g. ShieldBreakBurstAugment's Stun, or a caller doing its
    // own ally.Heal(...) inline (DashHealAugment's own RollAndHeal). Exactly one of these is ever non-null for
    // a given call, matching targetAllies.
    // onCastComplete: fired once at the very end with the total hit/found count — e.g.
    // KnightBuffStunAbilityPD healing itself based on how many enemies got stunned. Fires even when the
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
    // CharacterStatsPD.NotifyInterrupted) — e.g. KnightBuffStunAbilityPD.
    // overrideHitSfx/hitSfxOverride: only meaningful when dealDamage is true — see HitFeedbackData's own doc
    // comment for the full tri-state shape. false (default) plays owner.hitSfx exactly as before this
    // existed. A caller expecting many simultaneous/rapid hits per cast (ArcherArrowRainAbilityPD: up to 20
    // ticks * every enemy in radius) should pass overrideHitSfx: true and leave hitSfxOverride null for
    // silence, rather than letting owner.hitSfx spam on every single tick.
    // forcedCrit/forcedHighroll: null (default, every existing caller) rolls independently exactly as before —
    // only a caller reacting to an originating hit/strike event (e.g. TipStrikeRangeExplosionAugment, an
    // _onHit listener) passes the triggering WeaponHitData's own isCrit/isHighroll here, so this cast's own
    // damage crits/highrolls in lockstep with the hit that caused it instead of independently re-rolling —
    // same "inherit rather than roll" reasoning WeaponDamageMath.RollDamageInherited's own doc comment
    // describes, threaded through this shared helper (2026-09-14) rather than duplicated per caller.
    public static void Cast(PredictedHierarchy hierarchy, CharacterStatsPD owner, Vector3 position, float radius,
        GameObject radiusVFXPrefab, bool targetAllies = false,
        bool dealDamage = false, float damagePercent = 0f, float onHitPowerPercent = 0f, bool notifyOnHit = true,
        bool hasKnockback = false, float? knockback = null,
        bool interrupt = false,
        Action<IEnemyPD> onEnemyHit = null, Action<IFriendlyPD> onAllyHit = null, Action<int> onCastComplete = null,
        Action<IEnemyPD> onSuccessfulInterrupt = null,
        bool overrideHitSfx = false, AudioClip hitSfxOverride = null,
        bool? forcedCrit = null, bool? forcedHighroll = null)
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
                    bool isCrit = forcedCrit ?? (owner.NextFloat() < owner.CritChance);

                    float baseDamage = owner.Strength * damagePercent;
                    bool dmgHighroll;
                    float rolledDamage;
                    if (forcedHighroll.HasValue)
                    {
                        dmgHighroll = forcedHighroll.Value;
                        rolledDamage = WeaponDamageMath.RollDamageInherited(baseDamage, owner.LuckChance, owner.LuckFloor, owner.LuckCeiling, dmgHighroll, owner.NextFloat);
                    }
                    else
                    {
                        rolledDamage = WeaponDamageMath.RollDamage(baseDamage, owner.LuckChance, owner.LuckFloor, owner.LuckCeiling, owner.Highroll, owner.NextFloat, out dmgHighroll);
                    }
                    rolledDamage = WeaponDamageMath.ApplyCritAndHighroll(rolledDamage, dmgHighroll, isCrit, owner.HighrollBonus, owner.CritDamage);

                    enemy.TakeDamage(rolledDamage, isCrit, owner.owner ?? default, dmgHighroll, owner.executeThreshold, owner.critExecuteThreshold);
                    owner.NotifyHitFeedback(new HitFeedbackData { target = enemy.ObjectId, damage = rolledDamage, isCrit = isCrit, isHighroll = dmgHighroll, attacker = owner, isPriority = true, overrideSfx = overrideHitSfx, sfx = hitSfxOverride });

                    if (notifyOnHit)
                    {
                        // Passed through RAW, not luck/highroll-rolled here — matches every weapon type's own
                        // convention (WeaponMeleePD/WeaponAxePD/WeaponRangedProjectilePD all pass onHitPower =
                        // Stats.OnHitPower unrolled; only the damage roll above highrolls). Rolling this
                        // separately (2026-08-19 fix) meant a single hit could highroll TWICE — once here,
                        // once more in whichever on-hit augment consumes this onHitPower for its own roll —
                        // a hit is only ever supposed to be able to highroll once.
                        float onHitPower = owner.OnHitPower * onHitPowerPercent;

                        // isCrit/isHighroll inherited from this same hit (isHighroll added 2026-09-14, was
                        // left unset/false here — a real gap, since this IS this cast's own real highroll
                        // status), matching every other onhit-notify call site.
                        owner.NotifyHit(new WeaponHitData { target = enemy.ObjectId, weaponDamage = rolledDamage, onHitPower = onHitPower, isCrit = isCrit, isHighroll = dmgHighroll });
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
    // that high). AreaEffectPD now calls this itself, once, from Launch() only. RadiusIndicatorVFXPD still
    // owns its own playback timing by default (a [SerializeField] on the prefab itself, same convention as
    // any other prefab-authored visual effect) — abilityDuration below only overrides that for a caller
    // whose real duration can vary at runtime; every other caller still gets the prefab's own flat value.
    // tintColor: null (default, every existing caller) leaves the prefab's own authored color alone — see
    // RadiusIndicatorVFXPD.TintColor's own doc comment. Added for ArcherArrowRainAbilityPD, which passes its
    // caster's PlayerColor so the outline/rain particles match whoever cast it.
    // abilityDuration: null (default, every existing caller) — ArcherArrowRainAbilityPD-specific (2026-10-05),
    // forwarded to RadiusIndicatorVFXPD.Launch's own durationOverride (2026-10-11 fix — see its own doc
    // comment) AND to ArrowRainParticleRadiusPD.SetAbilityDuration on the spawned prefab's nested child,
    // bypassing RadiusIndicatorVFXPD's class-level logic entirely for everyone else rather than routing an
    // ArrowRain-only concept through a class every other AoE VFX caller also shares.
    // radiusGrowthPerSecond: null (default, every existing caller) — ArcherArrowRainGrowthAugment-specific
    // (2026-10-05), same bypass-RadiusIndicatorVFXPD shape as abilityDuration above: forwarded straight to
    // ArrowRainParticleRadiusPD.SetGrowth, which then grows the particle shape's radius (and, via its
    // existing radiusSource.SetRadius push, the ground outline too) at this constant per-second rate for as
    // long as the ability's own duration lasts. Deliberately NOT the same value as the real gameplay
    // AreaEffectPD.SetRadius growth (which steps in discrete per-tick increments) — see
    // ArcherArrowRainGrowthAugment's own doc comment for why that tiny visual/gameplay mismatch is accepted.
    public static void SpawnRadiusVFX(PredictedHierarchy hierarchy, GameObject radiusVFXPrefab, Vector3 position, float radius, Color? tintColor = null, float? abilityDuration = null, float? radiusGrowthPerSecond = null)
    {
        if (radiusVFXPrefab == null) return;

        var id = hierarchy.Create(radiusVFXPrefab, position, Quaternion.identity);
        if (!id.HasValue) return;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out RadiusIndicatorVFXPD vfx))
            // durationOverride: abilityDuration (2026-10-11 fix) — this root's own self-timed duration field
            // is a flat, hand-authored value (ArrowRainVFXPD.prefab's own RadiusIndicatorVFXPD: 5) that has
            // no idea the real ability can run longer than that (ArcherArrowRainDurationAugment). Passing
            // the real ability duration through here (null for every other caller, which still falls back
            // to that prefab-authored value exactly as before) keeps the WHOLE VFX tree alive — Unity
            // destroys children with their parent, and this root is deleted the instant its own timer
            // expires — for as long as the real AoE actually lasts, instead of vanishing at a flat 5s
            // regardless of how long the gameplay hazard underneath it keeps ticking.
            vfx.Launch(radius, tintColor, abilityDuration);

        // GetComponentInChildren, NOT go.TryGetComponent (2026-10-11 fix) — ArrowRainParticleRadiusPD lives
        // on ArrowRainVFXPD.prefab's nested Visuals/ArrowRainVFX child, not on the root `go` itself (that's
        // RadiusIndicatorVFXPD's own object, right above). go.TryGetComponent only ever checks the exact
        // GameObject passed in, never descendants, so this was SILENTLY finding nothing and skipping the
        // whole block below on every single cast — the real original cause of growth/duration never visibly
        // doing anything (the Start()-vs-Init() pooling fix earlier was real but moot, since this call site
        // never reached it in the first place).
        var particleRadius = go != null ? go.GetComponentInChildren<ArrowRainParticleRadiusPD>() : null;
        if (particleRadius != null)
        {
            // Unconditional, not gated on .HasValue — this VFX prefab is pooled and reused across casts, so
            // a cast that DOESN'T have the growth/duration augment must still explicitly clear whatever a
            // PREVIOUS cast on this same pooled instance left cached, not just skip setting it. Init() (not
            // Start() — see its own doc comment) then acts on both right after.
            particleRadius.SetAbilityDuration(abilityDuration);
            particleRadius.SetGrowth(radiusGrowthPerSecond);
            particleRadius.Init();
        }
    }
}
