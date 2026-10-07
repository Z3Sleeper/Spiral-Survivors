using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Shared, non-generic weapon-system infrastructure — split out of the old WeaponBasePD.cs (2026-09-30) when
// that file's own WeaponBasePD<TState> class was deleted as part of the full Normalized-system migration
// (Melee/Axe/Ranged all now inherit WeaponBasePD<TState> defined in WeaponBasePD.cs — formerly
// WeaponBasePDNormalized, renamed once it became the only base class). None of the types below belong to
// that generic class itself — they're plain top-level types every weapon-adjacent script (old and new alike)
// already depended on, so they needed a home independent of whichever concrete base class happens to exist.

// Lets WeaponManagerPD (and anything else that needs to treat "a weapon" generically) operate on any
// WeaponBasePD<TState> subclass regardless of its own state type — same reason IEnemyPD exists for
// EnemyBasePD<TState>. Melee and ranged weapons each have their own simulated state shape, so neither can be
// the other's base class; this interface is what lets WeaponManagerPD manage both through one list instead
// of being hard-coded to one concrete weapon type.
public interface IWeaponPD
{
    int SlotIndex { get; set; }
    // True for a weapon spawned onto OuterTrackAugment's second ring — set once by WeaponManagerPD at
    // registration time (see its own RegisterWeapon/_spawningOuter), never changes after. Read by
    // OrbitalPosition to orbit from just past the main track's own reach instead of from the player
    // directly, using a separate slot-count space, and by each weapon's own LateSimulate to spin the
    // opposite direction (see WeaponMeleePD.LateSimulate's own angleSign comment).
    bool IsOuterTrack { get; set; }
    bool IsMelee { get; }
    Transform transform { get; }
    Transform hitboxTarget { get; }
    Transform rangeScaleTarget { get; }
    float RangeValue { get; set; }
    Vector3 RootScale { get; set; }
    void Init(CharacterStatsPD stats);
    void InitModelVisuals();
    void ScaleModel(float range);
    // Instant visual-only toggle (renderer.enabled, not GameObject.SetActive — same reasoning as
    // ExeAxeThrowProjectilePD/WeaponRangedProjectilePD's own _renderers: SetActive(false) would also stop
    // this weapon's own PredictedIdentity Simulate/LateSimulate from running, which still needs to happen
    // while hidden, e.g. a hidden weapon still orbits so it's back in the right place the instant it's shown
    // again). See WeaponManagerPD.SetWeaponsVisible for the per-player entry point that drives this.
    void SetRenderersEnabled(bool value);
    // Nominal (design/stat-derived) distance from wherever this weapon's orbit is currently centered to its
    // own outer hit-detection edge — see WeaponBasePD's shared implementation for why one formula covers
    // every weapon type, and for why this deliberately never reads any live transform position (no origin
    // parameter here on purpose).
    float GetOuterReach();
}

// Shared payload for both the OnHit (every weapon hit) and OnCrit (the subset of hits that also crit)
// event chains — a crit is still a hit, so it carries the same hit data rather than just a bare target id.
// weaponDamage is the pre-luck-roll, pre-crit-multiplier damage baseline for THIS specific hit, not a
// flat re-read of CharacterStatsPD.WeaponStrength — WeaponRangedProjectilePD passes its own already
// ricochet-decayed State.currentDamage, and WeaponAxePD passes WeaponStrength * innerDamageMultiplier for
// handle (inner) hits, so downstream on-hit effects see the damage that hit actually represented rather
// than re-deriving it themselves per weapon type.
public struct WeaponHitData
{
    public PredictedObjectID target;
    public float weaponDamage;
    public float onHitPower;
    // Whether the hit that fired this event was itself a crit — onhit-triggered damage effects
    // (OnHitDamageAugment, OnHitDamageHealthRegenAugment) read this directly and crit in lockstep with the
    // original hit instead of rolling their own independent chance, same "if it crits, crit on all hits"
    // reasoning PhantomBladePD now uses.
    public bool isCrit;
    // Whether the hit that fired this event was itself a highroll (WeaponDamageMath.RollDamage's own
    // isHighroll out-param at the moment this hit rolled its own damage) — same "inherit in lockstep instead
    // of rolling independently" reasoning as isCrit above (2026-09-14, extended to match). False/unset on any
    // construction site that doesn't set it — same as isCrit's own default. PhantomBladePD populates this too
    // (2026-09-14 — used to hardcode isHighroll = false on every hit despite its own damage roll genuinely
    // being able to highroll; now baked in at Summon() time alongside _isCrit and reported for real).
    // Consumed via WeaponDamageMath.RollDamageInherited (see that method's own doc comment) — see also isCrit
    // above.
    public bool isHighroll;
    // World-space distance from the ATTACKER (Stats.transform.position), not the striking weapon/projectile's
    // own current position, to the struck enemy at the moment this hit landed — 0 on any construction site
    // that doesn't set it (only the real weapon-strike sites do: WeaponMeleePD/WeaponAxePD/
    // WeaponRangedProjectilePD/ExeAxeThrowProjectilePD/RogueSpinAbilityPD). Added for
    // PhantomBladeRangeAugment's own distance-accumulation trigger — reads this instead of independently
    // re-deriving distance, so any future consumer stays in lockstep with exactly what that augment already
    // sums. Deliberately player-to-enemy, not weapon-to-enemy, so a hit only counts as "far" when it reflects
    // genuine WeaponRange investment (how far the ATTACKER'S OWN position is from the target), not merely
    // where a projectile happened to be along its own flight when it connected.
    //
    // A "normalized distance" check (any consumer that wants hitDistance to reflect genuine WeaponRange
    // investment alone, not any other reason a hit could land far from the attacker) factors out THREE
    // independent things, all 0/neutral by default on any construction site that doesn't set them: weaponRadius
    // and weaponOffset below (both flat, subtracted from hitDistance), and weaponRangeMultiplier (a scale,
    // divided into whatever remains) — see OnHitDamageDistanceAugment.OnHit for the reference implementation:
    // Mathf.Max(0f, hitDistance - weaponRadius - weaponOffset) / (weaponRangeMultiplier > 0 ? weaponRangeMultiplier : 1).
    public float hitDistance;
    // How far this weapon's own orbit sits from the player, on top of the universal weaponOffset below,
    // regardless of any WeaponRange investment at all — CharacterStatsPD.WeaponRadius, PLUS
    // EffectiveWeaponRange for an OUTER TRACK weapon specifically (OuterTrackAugment's own second ring starts
    // just past the main track's full reach — see WeaponBasePD.NominalOrbitRadius's own identical
    // weaponRadiusOffset local variable, which is exactly this value): weaponRadius = IsOuterTrack ?
    // WeaponRadius + EffectiveWeaponRange : WeaponRadius. Without factoring this out, Bladeweaver (this
    // project's only character with WeaponRadius > 0) would have every hit read as farther-reaching than an
    // equivalent WeaponRange investment on any other class, purely because its weapons orbit further from its
    // own center before even reaching outward by WeaponRange at all — and any class that picks
    // OuterTrackAugment would get the identical inflation on their own outer-ring hits — the exact same "bait
    // option, quietly worse/better for one specific character/pick" class of bug weaponRangeMultiplier
    // already exists to prevent, just for a different stat.
    public float weaponRadius;
    // The universal character-clearance term EVERY weapon's own orbit is held out by, so it doesn't start
    // from literal player center — CharacterStatsPD.bonusSizePercent * 0.5f (see WeaponBasePD.
    // NominalOrbitRadius's own identical term), defaulting to 0.5 for every character/weapon (bonusSizePercent
    // defaults to 1). Unlike weaponRadius above (which varies per character/track), this is the SAME baseline
    // floor hitDistance always carries regardless of build — factored out for the identical "genuine
    // WeaponRange investment only" reasoning.
    public float weaponOffset;
    // Extra range multiplier the striking character's own weaponRangeScalingBonusPercent/weaponRangeScalingPenalty
    // pair applied on top of the raw WeaponRange stat (CharacterStatsPD.EffectiveWeaponRange — e.g. Archer's
    // own bow-range boost, or Bladeweaver's/OuterTrackAugment's own reach reductions) — 0 (the default on every
    // construction site that doesn't set it) is treated as neutral 1 by any consumer, same "0 means unset,
    // read as 1" convention CharacterStatsPD's own *Percent tiers already use. Set by WeaponMeleePD/
    // WeaponAxePD/WeaponRangedProjectilePD's own real weapon-strike hits (2026-09-14 — melee/Axe didn't used
    // to, back when only ranged-flagged prefabs could ever carry a nonzero multiplier at all); ability-driven
    // hits that mimic a weapon strike (ExeAxeThrowProjectilePD/RogueSpinAbilityPD) deliberately still leave
    // this at 0/unset — per explicit design, those abilities read the TRUE, unscaled WeaponRange stat for
    // their own reach, not EffectiveWeaponRange, so there's nothing to normalize away for them.
    public float weaponRangeMultiplier;
    // True if this hit landed in the outer 10% of the weapon's own reach — the "tip" for melee/Axe
    // (WeaponMeleePD/WeaponAxePD, outer 10% of the physical hitbox depth), "snipe" for ranged
    // (WeaponRangedProjectilePD, outer 10% of the weapon's own targeting range — see that class's own
    // ProcessHit comment for why it's measured against the targeting range, not the projectile's further-
    // inflated max travel distance) — same underlying flag, just a different in-game name depending on
    // weapon type, deliberately reused rather than split into two fields so any future consumer works on both
    // automatically. False (default) on every construction site that doesn't set it — ExeAxeThrowProjectilePD/
    // RogueSpinAbilityPD/AoE effects/PhantomBladePD have no equivalent concept defined yet, left for later. An
    // enemy that overlaps both a tip and non-tip portion of a melee/Axe hitbox in the same tick (e.g.
    // straddling the 90% boundary across sub-arc segments) counts as a tip hit — same "any qualifying
    // detection wins" tie-break WeaponAxePD's own inner/outer split already uses (an enemy touching both zones
    // counts as outer). Deliberately scoped to genuine weapon strikes only (WeaponMeleePD/WeaponAxePD/
    // WeaponRangedProjectilePD) — NOT ability effects that merely mimic one (ExeAxeThrowProjectilePD/
    // RogueSpinAbilityPD/AoE effects/PhantomBladePD), which always leave this at its default false. Not a
    // gap to fill in later — a settled design boundary.
    public bool isTip;
}

// Shared payload for CharacterStatsPD._onAllyStrike — fired whenever any of this player's weapons touches
// (not damages) an ally, so any "hit ally, do something" augment can subscribe once rather than each
// needing its own copy of the weapon-side overlap-detection plumbing. A direct IFriendlyPD reference is safe
// here the same way an old CharacterStatsPD-only version was — unlike enemies, neither CharacterStatsPD
// nor any currently-planned IFriendlyPD implementer is pooled/reused for a different owner mid-match, so
// there's no SpawnGeneration-style staleness risk to guard against (unlike WeaponHitData's enemy target,
// which uses a PredictedObjectID specifically because enemies ARE pooled). onHitPower carries the same
// per-hit dropoff/penalty treatment as WeaponHitData (ricochet decay, axe handle penalty), so ally-strike
// effects scale down the same way onhit-damage effects already do.
public struct AllyStrikeData
{
    public IFriendlyPD ally;
    public float onHitPower;
}

// Payload for a PredictedEvent<HitFeedbackData>-based hit-feedback notification (damage number, hit-flash,
// SFX) — replaces the old "queue a plain List<T> in Simulate()/LateSimulate(), drain it once in
// UpdateView()" pattern used across every weapon/effect that deals damage. That pattern turned out to be
// resimulation-unsafe: the queue isn't predicted State, so it never rolls back — a hit that gets replayed
// N times before the next real-frame UpdateView() call produces N duplicate damage numbers/SFX for one
// real hit (see feedback_manual_queue_drain_pattern_unsafe memory). PredictedEvent<T>.Invoke() already has
// the right guard built in (skips notifying listeners entirely while the owner is mid-replay, only fires
// on the one live execution), so invoking this directly from Hit()/OnHit() and letting the listener call
// TriggerHitFeedback needs no queue at all.
public struct HitFeedbackData
{
    public PredictedObjectID target;
    public float damage;
    public bool isCrit;
    public bool isHighroll;
    public CharacterStatsPD attacker;
    // Which DamageNumberManager stacking bucket this hit belongs to on the enemy it hit — true for real
    // weapon-shaped hits (Weapon, PhantomBlade, AoE Effects casts, etc.), false for onhit-effect "noise"
    // (OnHitDamageAugment/Health/HealthRegen, KillLaunch). See DamageNumberManager's own class doc comment.
    public bool isPriority;

    // Lets a hit source override the default per-attacker hitSfx entirely — false (default) means "play
    // attacker.hitSfx as normal," exactly what every existing call site already gets for free without
    // setting either field. true hands the choice to the SOURCE: sfx left null means explicitly silent
    // (e.g. ArcherArrowRainAbilityPD, whose 20 ticks * every enemy in radius would otherwise spam
    // attacker.hitSfx into an unlistenable wall of noise), or a specific clip plays that instead of the
    // attacker's own. Any future source with the same "many simultaneous/rapid hits" shape (other AoE
    // ticks, DoTs, etc.) should use this rather than inventing its own ad-hoc SFX-suppression mechanism.
    public bool overrideSfx;
    public AudioClip sfx;
}

// Shared damage-roll formula, non-generic so it's usable by anything that needs it without inheriting
// from WeaponBasePD<TState> — e.g. WeaponRangedProjectilePD, which is deliberately NOT a weapon (no
// orbit, no range-scaling visuals), matching the old system's WeaponBase.RollDamage static helper.
public static class WeaponDamageMath
{
    // Rolls against CharacterStatsPD.LuckFloor/LuckCeiling/Highroll instead of hardcoded constants — any
    // future system that wants a "luck-influenced random roll" should roll against these same stats rather
    // than adding its own variance constant, so a single augment moving the floor, ceiling, or highroll
    // threshold affects all of them at once. luckFloor is already clamped to >= 0 at the source (LuckFloor
    // property), so this never needs to guard against a negative result itself.
    //
    // highroll: landing in the top (1 - highroll) fraction of this roll's own [min, max] window snaps the
    // result up to max and flags it — a true max roll is otherwise a measure-zero event off continuous
    // random sampling, practically unreachable, which is the whole reason this exists.
    public static float RollDamage(float damage, float luckChance, float luckFloor, float luckCeiling, float highroll, out bool isHighroll)
    {
        float max = damage * luckCeiling;
        float rawMin = damage * luckFloor;

        // Threshold is fixed against the pre-luck [rawMin, max] window, not the luck-raised one below —
        // luckChance only raises the roll's actual floor toward this already-placed line, it doesn't move
        // the line itself. That's what makes Luck and Highroll interact: as luckChance climbs, min closes
        // the gap on threshold, so the odds of clearing it rise too, and once luckChance >= highroll, min
        // has caught up to (or passed) threshold and every roll highrolls.
        float highrollThreshold = Mathf.Lerp(rawMin, max, highroll);

        float min = Mathf.Lerp(rawMin, max, luckChance);
        float roll = GameRandom.instance.NextFloat(min, max);

        isHighroll = roll >= highrollThreshold;
        return isHighroll ? max : roll;
    }

    // Same roll shape as RollDamage above, EXCEPT the highroll STATUS is dictated by the caller (inherited
    // from an originating hit/strike event's own WeaponHitData.isHighroll) instead of independently determined
    // via its own roll-against-threshold check — used by onhit/onstrike-triggered secondary effects
    // (OnHitDamageAugment, OnHitDamageDistanceAugment, OnHitDamageHealthAugment, OnHitDamageHealthRegenAugment,
    // AreaEffectCastHelper.Cast's forcedHighroll path, etc.) so they highroll in lockstep with the hit that
    // triggered them, same "if it crits, crit on all hits" reasoning WeaponHitData.isCrit already uses,
    // extended to highroll (2026-09-14). Magnitude for the non-highroll case still rolls normally within
    // [min, max) via luckChance — only the highroll SNAP-TO-MAX is forced rather than independently rolled, so
    // this doesn't remove damage variance entirely, just the highroll coin-flip specifically.
    public static float RollDamageInherited(float damage, float luckChance, float luckFloor, float luckCeiling, bool isHighroll)
    {
        float max = damage * luckCeiling;
        if (isHighroll) return max;

        float rawMin = damage * luckFloor;
        float min = Mathf.Lerp(rawMin, max, luckChance);
        return GameRandom.instance.NextFloat(min, max);
    }

    // Same luck-roll shape as RollDamage above, but with damage fixed at 1 (so the result is a plain
    // multiplier) and the random source passed in rather than hardcoded to GameRandom — callers that need
    // this roll from a purely local, non-deterministic context (e.g. AugmentCardUI's card-display preview,
    // which must NOT touch the shared/order-sensitive GameRandom stream) pass UnityEngine.Random.Range;
    // callers rolling from Simulate()-context (e.g. AugmentManagerPD.AutoPick) pass GameRandom.instance.NextFloat.
    public static float RollLuckMultiplier(float luckChance, float luckFloor, float luckCeiling, float highroll, float highrollBonus, System.Func<float, float, float> rollRange, out bool isHighroll)
    {
        float max = luckCeiling;
        float rawMin = luckFloor;
        float highrollThreshold = Mathf.Lerp(rawMin, max, highroll);
        float min = Mathf.Lerp(rawMin, max, luckChance);
        float roll = rollRange(min, max);
        isHighroll = roll >= highrollThreshold;
        float result = isHighroll ? max : roll;
        if (isHighroll) result *= 1f + highrollBonus;
        return result;
    }

    // Highroll and Crit ADDITIVELY stack now (2026-08-19 rework) — replaces the old shape where each was
    // applied as its own separate multiplicative step (rolled *= (1 + HighrollBonus); rolled *= CritDamage;),
    // which let the two compound into each other and blow up once HighrollBonus got buffed (0.5 -> 3f, see
    // baseHighrollBonus's own doc comment). HighrollBonus/CritDamage are both now "the full multiplier this
    // roll gets IF THIS IS THE ONLY THING THAT PROCS" (e.g. base 3f each = a highroll-only or crit-only hit
    // does 300% damage), not a "+X on top of 1" bonus anymore — so when only one procs, the roll is
    // multiplied by that stat directly (no baseline "1 +" added on top). When BOTH proc on the same hit, the
    // two full values are summed together (e.g. 3f + 3f = 600% damage), which is what "additively stack"
    // means here — literally adding the two multipliers rather than multiplying them together. Neither
    // procs: unmodified (implicit x1).
    public static float ApplyCritAndHighroll(float rolled, bool isHighroll, bool isCrit, float highrollBonus, float critDamage)
    {
        if (isHighroll && isCrit) return rolled * (highrollBonus + critDamage);
        if (isHighroll) return rolled * highrollBonus;
        if (isCrit) return rolled * critDamage;
        return rolled;
    }
}

// Shared multishot fan-angle formula — used by both WeaponRangedPD.Fire() (player) and ShooterGloomPD (enemy),
// so "the same multishot behavior" holds for both without duplicating the math. One shot always goes
// straight (0°); the rest ("extra") split between left (negative) and right (positive), left getting the
// odd one out, each side's shots evenly spaced from near-center out to the full half-spread edge.
// Reconstructed from spec examples: extra=3, spread=135 -> {0, -33.75, -67.5, +67.5} (left gets 2, right
// gets 1; left's 2 shots sit at half*(1/2)=33.75 and half*(2/2)=67.5; right's 1 shot sits at half*(1/1)=67.5).
public static class MultishotMath
{
    // Fills `angles` (cleared first) with 1 + extra angle offsets in degrees. Static shared scratch list
    // pattern, not a fresh allocation per call — same "shared, immediately drained within one synchronous
    // call" convention as PhantomBladeSummonHelper._overlapResults/DashExplosionAugment._overlapResults.
    public static void ComputeAngles(int extra, float spread, List<float> angles)
    {
        angles.Clear();
        angles.Add(0f);
        if (extra <= 0) return;

        float half = spread / 2f;
        int leftCount = Mathf.CeilToInt(extra / 2f);
        int rightCount = extra - leftCount;

        for (int i = 1; i <= leftCount; i++)
            angles.Add(-half * (i / (float)leftCount));
        for (int i = 1; i <= rightCount; i++)
            angles.Add(half * (i / (float)rightCount));
    }
}

// Predicted-state-safe hit-cooldown entry — for weapons that need a REPEATING "don't re-hit the same
// target immediately" gate (WeaponMeleePD, WeaponAxePD; a one-shot weapon like WeaponRangedProjectilePD/
// PhantomBladePD just uses a permanent _hitTargets HashSet instead, since it only ever needs "never hit
// twice", not "cooldown, then allow again"). Ticked down by delta inside LateSimulate() itself
// (deterministic, tick-based, rolls back correctly) rather than Time.deltaTime inside UpdateView() — a
// real-time-decayed plain Dictionary was the actual cause of enemies flashing red and then never
// reverting, and of the "possible WeaponMeleePD double hit-feedback, unconfirmed if real double damage or
// cosmetic" entry in the project backlog: a replayed historical tick would see the cooldown already
// expired (real time kept advancing independently of which tick was being resimulated) and re-fire a hit
// that had already happened.
public struct HitCooldownEntry
{
    public PredictedObjectID target;
    // Ticks remaining, not seconds — see EnemyBaseState.ContactCooldown.remaining's own doc comment for the full
    // reasoning (PurrNet's delta-packer costs a full 33 bits for any float that changed since last
    // snapshot; this field changes almost every tick whenever a cooldown is active, so an int (cheap
    // zigzag-varint diff) is far cheaper than a float here too). delta is a constant tick duration now
    // that nothing manipulates Time.timeScale, so converting a seconds-duration to ticks at TryStart time
    // is exact, not approximate.
    public int remaining;
}

// target (2026-08-30, was PlayerID ally) — rekeyed to PredictedObjectID so ally-hit cooldown tracking works
// against any IFriendlyPD (a future non-player ally has no PlayerID), same rekey EnemyBasePD.ContactCooldown
// gets for the identical reason. See HitCooldownEntry's own doc comment for the ticks-not-seconds reasoning.
public struct AllyHitCooldownEntry
{
    public PredictedObjectID target;
    public int remaining;
}

// Shared tick-down + gate-check helpers for HitCooldownEntry/AllyHitCooldownEntry lists — see
// HitCooldownEntry's own doc comment for why this replaced a plain Dictionary. DisposableList<T> wraps an
// internal List<T> reference, so passing it by value and mutating it (Add/RemoveAt/indexer) still mutates
// the caller's own field — no ref needed.
public static class HitCooldownMath
{
    public static void Tick(DisposableList<HitCooldownEntry> cooldowns)
    {
        for (int i = cooldowns.Count - 1; i >= 0; i--)
        {
            var entry = cooldowns[i];
            entry.remaining--;
            if (entry.remaining <= 0) cooldowns.RemoveAt(i);
            else cooldowns[i] = entry;
        }
    }

    // Returns true and starts the cooldown if target wasn't already on cooldown; false (no-op) otherwise.
    // duration is still seconds at call sites (derived from WeaponSpeed, etc.) — converted to ticks here.
    public static bool TryStart(DisposableList<HitCooldownEntry> cooldowns, PredictedObjectID target, float duration, float delta)
    {
        for (int i = 0; i < cooldowns.Count; i++)
            if (cooldowns[i].target.Equals(target)) return false;
        cooldowns.Add(new HitCooldownEntry { target = target, remaining = Mathf.Max(1, Mathf.RoundToInt(duration / delta)) });
        return true;
    }

    // Read-only check, for callers (WeaponRangedPD.FindTarget) that need to EXCLUDE cooldown-gated
    // candidates while picking a target, separately from whichever candidate actually ends up committed
    // via TryStart afterward.
    public static bool IsOnCooldown(DisposableList<HitCooldownEntry> cooldowns, PredictedObjectID target)
    {
        for (int i = 0; i < cooldowns.Count; i++)
            if (cooldowns[i].target.Equals(target)) return true;
        return false;
    }

    public static void Tick(DisposableList<AllyHitCooldownEntry> cooldowns)
    {
        for (int i = cooldowns.Count - 1; i >= 0; i--)
        {
            var entry = cooldowns[i];
            entry.remaining--;
            if (entry.remaining <= 0) cooldowns.RemoveAt(i);
            else cooldowns[i] = entry;
        }
    }

    public static bool TryStart(DisposableList<AllyHitCooldownEntry> cooldowns, PredictedObjectID target, float duration, float delta)
    {
        for (int i = 0; i < cooldowns.Count; i++)
            if (cooldowns[i].target.Equals(target)) return false;
        cooldowns.Add(new AllyHitCooldownEntry { target = target, remaining = Mathf.Max(1, Mathf.RoundToInt(duration / delta)) });
        return true;
    }

    public static bool IsOnCooldown(DisposableList<AllyHitCooldownEntry> cooldowns, PredictedObjectID target)
    {
        for (int i = 0; i < cooldowns.Count; i++)
            if (cooldowns[i].target.Equals(target)) return true;
        return false;
    }
}

// LayerMask.GetMask() does a string lookup every call — cheap once, wasteful when called per-instance
// (WeaponMeleePD/WeaponRangedPD registration) or worse, per-shot (WeaponRangedProjectilePD.Launch()). The
// layer→index mapping is fixed at editor time and never changes at runtime, so a static field computed
// once (C#'s guaranteed single-init-on-first-access for static fields) covers every caller for the whole
// process lifetime.
public static class CombatLayerMasks
{
    public static readonly int EnemyHurtboxMask = LayerMask.GetMask("Enemy");
    // Every weapon always queries this alongside EnemyHurtboxMask now — ally-hit detection ("canContactAllies")
    // is permanently on, not gated behind an enable flag (see CharacterStatsPD.NotifyAllyStrike's own doc
    // comment). Real friendly-fire damage was removed from the project entirely; a touched ally only ever
    // fires the _onAllyStrike notification, never TakeDamage. "Friendly" — the Unity Layer itself was renamed
    // so any future non-player IFriendlyPD implementer's own hurtbox collider lives on this same layer, not a
    // player-specific one — see EnemyBasePD's own AoE-hit-check conversion (SpearGloomPD.EnterStab etc.) for
    // why enemy attacks now query this directly too, instead of doing a manual FriendlyTargetRegistryPD
    // distance/rectangle check.
    public static readonly int FriendlyHurtboxMask = LayerMask.GetMask("Friendly");
}

// Non-generic home for constants shared across the weapon system regardless of TState — WeaponBasePD<TState>
// itself is generic, so a bare WeaponBasePD.Member reference doesn't resolve without specifying TState (even
// for a member that has nothing to do with TState at all). WeaponRangedProjectilePD deliberately doesn't
// inherit WeaponBasePD<TState> (see its own class doc comment) but still needs the exact same height-lift
// value for its own model, hence pulling it out here instead.
public static class WeaponVisualConstants
{
    // How far above the player's true origin a weapon/projectile's MODEL renders — visual only, never
    // applied to a real hit-detection-relevant position. 0.5 is correct for the player's default 1x1x1-unit
    // sphere. Not exposed for tuning — this ratio is never meant to change per weapon. See
    // WeaponBasePD.ApplyVisualAdjustments/ApplyHeightLift and WeaponRangedProjectilePD.Awake() for the two
    // different ways this gets applied (LOCAL offset under an already-Size-scaled parent vs. an explicit
    // WORLD-space multiply, respectively — see each call site's own comment for why the two differ).
    public const float BaseMidHeightLift = 0.5f;
}

// Non-generic, same reasoning as WeaponVisualConstants above — ExeAxeThrowProjectilePD (also deliberately
// not a WeaponBasePD<TState> subclass, same reasoning as WeaponRangedProjectilePD) needs this for its own
// return-leg homing sweep, not just WeaponMeleePD/WeaponAxePD's orbit sweep.
public static class SweepMath
{
    // How many sub-segments a swept-hitbox mover should sample this tick instead of sweeping straight from
    // its start position to its end position. A straight-line sweep between two points is only a good
    // approximation of the TRUE path when the heading barely changed over that tick — true for
    // WeaponMeleePD/WeaponAxePD's own orbit at normal rotation speeds (the two sampled points sit close
    // together on the circle), and for ExeAxeThrowProjectilePD's return leg when the caster isn't moving
    // sharply relative to the axe. At a big enough single-tick heading change (WeaponSpeed * delta covering
    // many degrees of orbit; or the return leg's bearing-to-caster swinging fast because the axe is close to
    // a caster who's still moving), a straight sweep between the two endpoints cuts across the interior
    // instead of following the true path, and can miss something sitting on the true path between them
    // entirely. Below SubArcTriggerDegrees a single straight sweep is already close enough to the true path
    // that subdividing isn't worth it — by far the common case, and costs nothing extra (falls straight
    // through to the existing single-sweep behavior). Above that, subdivide down to MaxDegreesPerSubArc-sized
    // segments, capped at MaxSubArcs total no matter how much bigger the heading change gets beyond that —
    // more than one full turn doesn't need finer coverage, it's the same circle of points either way.
    private const float SubArcTriggerDegrees = 45f;
    private const float MaxDegreesPerSubArc  = 30f;
    private const int   MaxSubArcs           = 12;

    public static int ComputeSubArcCount(float angleDeltaDegrees)
    {
        float effective = Mathf.Min(Mathf.Abs(angleDeltaDegrees), 360f);
        if (effective <= SubArcTriggerDegrees) return 1;
        return Mathf.Clamp(Mathf.CeilToInt(effective / MaxDegreesPerSubArc), 2, MaxSubArcs);
    }
}
