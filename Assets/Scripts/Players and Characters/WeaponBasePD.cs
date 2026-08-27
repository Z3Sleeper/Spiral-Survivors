using System.Collections.Generic;
using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Lets WeaponManagerPD (and anything else that needs to treat "a weapon" generically) operate on any
// WeaponBasePD<TState> subclass regardless of its own state type — same reason IEnemyPD exists for
// EnemyBasePD<TState>. Melee (WeaponMeleePD) and ranged (WeaponRangedPD) weapons each have their own
// simulated state shape, so neither can be the other's base class; this interface is what lets
// WeaponManagerPD manage both through one list instead of being hard-coded to one concrete weapon type.
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
}

// Shared payload for CharacterStatsPD._onAllyStrike — fired whenever any of this player's weapons touches
// (not damages) an ally, so any "hit ally, do something" augment can subscribe once rather than each
// needing its own copy of the weapon-side overlap-detection plumbing. A direct CharacterStatsPD reference
// (not a PredictedObjectID, unlike WeaponHitData's enemy target) is safe here — unlike enemies,
// CharacterStatsPD instances are never pooled/reused for a different player mid-match, so there's no
// SpawnGeneration-style staleness risk to guard against. onHitPower carries the same per-hit
// dropoff/penalty treatment as WeaponHitData (ricochet decay, axe handle penalty), so ally-strike effects
// scale down the same way onhit-damage effects already do.
public struct AllyStrikeData
{
    public CharacterStatsPD ally;
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

public struct AllyHitCooldownEntry
{
    public PlayerID ally;
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

    public static bool TryStart(DisposableList<AllyHitCooldownEntry> cooldowns, PlayerID ally, float duration, float delta)
    {
        for (int i = 0; i < cooldowns.Count; i++)
            if (cooldowns[i].ally == ally) return false;
        cooldowns.Add(new AllyHitCooldownEntry { ally = ally, remaining = Mathf.Max(1, Mathf.RoundToInt(duration / delta)) });
        return true;
    }

    public static bool IsOnCooldown(DisposableList<AllyHitCooldownEntry> cooldowns, PlayerID ally)
    {
        for (int i = 0; i < cooldowns.Count; i++)
            if (cooldowns[i].ally == ally) return true;
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
    // fires the _onAllyStrike notification, never TakeDamage.
    public static readonly int PlayerHurtboxMask = LayerMask.GetMask("Player");
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

    // An idle wobble sway (X/Z tilt + Y bob on the model, driven by a shared deterministic per-player clock)
    // lived here from 2026-08-25 through the same day — fully reverted after live testing on Knight/Rogue/
    // Bladeweaver/Axe showed it read as negligible-to-off-putting from a top-down view. Removed rather than
    // left dormant/disabled — see project_pd_backlog memory (or git history around that date) for the full
    // design/tuning history if this idea ever gets revisited.
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

public abstract class WeaponBasePD<TState> : PredictedIdentity<TState>, IWeaponPD
    where TState : struct, IPredictedData<TState>
{
    protected CharacterStatsPD _stats;
    public virtual bool IsMelee => true;

    public Transform   hitboxTarget;
    public Transform   rangeScaleTarget;
    public Transform[] rangeFollowers;

    // Explicit interface forwarding, not auto-properties — hitboxTarget/rangeScaleTarget above are plain
    // public fields wired up in the Inspector on existing weapon prefabs (e.g. KnightWeapon.prefab).
    // Converting them to auto-properties would satisfy IWeaponPD directly but silently drop those
    // Inspector-assigned references (auto-properties aren't serialized), breaking the prefab. Forwarding
    // keeps the fields serialized exactly as before while still satisfying the interface for
    // WeaponManagerPD's IWeaponPD-typed access.
    Transform IWeaponPD.hitboxTarget    => hitboxTarget;
    Transform IWeaponPD.rangeScaleTarget => rangeScaleTarget;

    // Back-tip-to-front-tip scale target — e.g. a blade trail particle system that needs to span the whole
    // weapon, from wherever a back-of-hilt decoration's own point renders (if any — see hitboxBackTip) all
    // the way to wherever the front tip decoration's own point renders (see hitboxTip). Far edge is rangePart
    // + hitboxTipOffset, near edge is _hiltEdgeLocal - hitboxBackTipOffset — both optionally growing with
    // xRatio (see ScaleModel's own particleFarEdge/particleNearEdge comments for the full derivation). With
    // no back decoration configured (hitboxBackTip unassigned), the near edge collapses back to plain
    // _hiltEdgeLocal, i.e. this behaves exactly as it did before back-tip support existed. Deliberately writes
    // to different local axes than every other target in this method (see ScaleModel's own comment) — this
    // prefab's particle GameObject has its own rotation that puts X, not Z, as the axis Hierarchy scaling mode
    // actually reads length from. Optional: leave unassigned for weapons that don't need this (e.g. Axe,
    // whose own tip decoration already scales correctly on its own).
    [SerializeField] public Transform particleRangeTarget;
    [SerializeField] public    float     xRangeScaleRatio = 0f;
    [SerializeField] public    Transform[] yScaleFollowers;
    [SerializeField] public    Transform[] xzScaleFollowers;
    [SerializeField] public    Transform[] xScaleFollowers;

    // Decorative tip mesh (e.g. an arrowhead/blade point) that extends past the base hitbox/hb edge —
    // GetOuterReach can't see this from meleeHalfExtents/hb.lossyScale alone since it's a separate piece
    // with its own authored length, not part of the hitbox box itself. hitboxTipOffset is that length AT
    // SCALE 1 (same "base value, scaled live" convention as meleeHalfExtents); the scalesWith bools mark
    // which of hitboxTip's own localScale axes track range growth (e.g. a diamond tip in xzScaleFollowers
    // would check X and Z), so GetOuterReach knows which current scale value(s) to multiply the base
    // offset by. Optional — leave hitboxTip unassigned for weapons with no separate tip decoration.
    [SerializeField] private Transform hitboxTip;
    [SerializeField] private float     hitboxTipOffset = 0f;
    [SerializeField] private bool      hitboxTipScalesWithX = false;
    [SerializeField] private bool      hitboxTipScalesWithY = false;
    [SerializeField] private bool      hitboxTipScalesWithZ = false;

    // Same idea as hitboxTip above, mirrored to the OTHER end — a decoration on the back of the hilt (e.g.
    // Bladeweaver's own back spike) that extends past the base hitbox/hb edge in the opposite direction.
    // Unlike hitboxTip, this decoration's own position never moves with range (it sits on the hilt, not the
    // blade) — only its scale grows if it's also in xzScaleFollowers, same as hitboxTip's own scalesWith
    // bools mark. Optional — leave unassigned for weapons with no back decoration.
    [SerializeField] private Transform hitboxBackTip;
    [SerializeField] private float     hitboxBackTipOffset = 0f;
    [SerializeField] private bool      hitboxBackTipScalesWithX = false;
    [SerializeField] private bool      hitboxBackTipScalesWithY = false;
    [SerializeField] private bool      hitboxBackTipScalesWithZ = false;

    // Every visible mesh piece on this weapon — see IWeaponPD.SetRenderersEnabled's own doc comment for why
    // this toggles Renderer.enabled rather than SetActive. Optional: leave empty and this weapon simply
    // never hides, no error.
    [SerializeField] private Renderer[] _renderers;

    // Particle systems that should hide/show in lockstep with _renderers (e.g. on death) and freeze/resume
    // with PausePD rather than running on Unity's own real-time playback while the game is paused. Unlike
    // _renderers this is safe to Stop/Play outright (not just toggle a visibility flag) — these are plain
    // child GameObjects, not PredictedIdentity-carrying ones, so nothing here risks stopping this weapon's
    // own Simulate/LateSimulate the way SetActive(false) on the whole weapon would. Optional: leave empty
    // for weapons with no particle effects to manage.
    [SerializeField] private ParticleSystem[] weaponParticles;

    // Optional emission-rate scaling for every entry in weaponParticles, driven by live WeaponRange (e.g.
    // 40/s at 10 range up to 80/s at 50 range for Axe's headwind particles). Applied uniformly to all of
    // them, not per-entry — every weapon that's used this so far wants the whole set moving together; a
    // per-entry override can be added later if that stops being true. Opt-in: leave
    // maxEmissionRange <= minEmissionRange (both default 0) and this is a complete no-op, every particle
    // system's own authored/Inspector emission rate is left untouched.
    [SerializeField] private float minEmissionRate  = 0f;
    [SerializeField] private float maxEmissionRate  = 0f;
    [SerializeField] private float minEmissionRange = 0f;
    [SerializeField] private float maxEmissionRange = 0f;

    private float     _hiltEdgeLocal;
    private float     _baseRangeScaleX;
    private float[]   _baseYScales;
    private Vector2[] _baseXZScales;
    private float[]   _baseXScales;
    // hitboxTipOffset is authored against hitboxTip's scale AT THIS captured moment (i.e. whatever the
    // prefab is currently set to) — so GetOuterReach multiplies by (current / base) per marked axis rather
    // than the raw current scale directly. That's what lets hitboxTipOffset be set to the actual,
    // Scene-view-measurable extension length at the prefab's authored scale, instead of a confusing
    // per-unit-scale constant the raw-current-scale approach would otherwise require.
    private Vector3   _hitboxTipBaseScale = Vector3.one;
    // Same as _hitboxTipBaseScale above, for hitboxBackTip.
    private Vector3   _hitboxBackTipBaseScale = Vector3.one;
    // Guards InitModelVisuals() below to a single real capture per GameObject instance — WeaponManagerPD
    // calls InitModelVisuals() from RegisterWeapon() on every logical spawn, including pooled reuse (since
    // LateAwake() re-fires then too). Without this, a reused instance would re-capture the "base" values
    // from wherever ScaleModel() left the visual transforms scaled to at the END of its PREVIOUS life,
    // instead of the prefab's true untouched default — corrupting designOrbit/_hiltEdgeLocal/
    // _baseRangeScaleX/etc. and producing a wrong (usually tiny, badly offset) scale on every reuse after
    // the first. Same "cache once in a hook that re-fires on pooled reuse" bug class as
    // WeaponRangedProjectilePD/PhantomBladePD's own _baseScale, just here fixed with a guard flag instead
    // of moving the capture into Awake() (RegisterWeapon/InitModelVisuals is called well after Awake(), so
    // there's no earlier hook available to move it to).
    private bool _modelVisualsInitialized;

    [System.NonSerialized] public float designOrbit;
    public float   RangeValue { get; set; }
    public Vector3 RootScale  { get; set; } = Vector3.one;
    public int     SlotIndex  { get; set; }
    public bool    IsOuterTrack { get; set; }

    // Opt-in per-prefab range multiplier — checked (isRanged = true) on Archer's own WeaponRangedPD prefab,
    // tuned via rangeMultiplier, rather than inflating the shared WeaponRange stat itself (which every AoE
    // Effects radius formula now also reads directly — see DashExplosionAugment's own doc comment). isRanged
    // is a separate flag from IsMelee on purpose: not every non-melee weapon necessarily wants this, and it
    // keeps the toggle an explicit per-prefab authoring choice rather than an automatic IsMelee side effect.
    [SerializeField] private bool  isRanged = false;
    [SerializeField] private float rangeMultiplier = 1f;

    // What this weapon's own range-dependent behavior (raycast/fire distance, projectile max range,
    // GetOuterReach's own ranged-weapon branch below) should actually use — Stats.WeaponRange for every
    // ordinary weapon, scaled by rangeMultiplier only for the ones explicitly marked isRanged.
    public float EffectiveWeaponRange => _stats.WeaponRange * (isRanged ? rangeMultiplier : 1f);

    // Every weapon (melee or ranged) registers with its owner's WeaponManagerPD the same way — see
    // WeaponMeleePD.TryInit()/WeaponRangedPD's equivalent — and OrbitalPosition() below needs WeaponCount
    // from it for the multi-weapon angular-spacing term, so this lives here rather than being duplicated
    // per weapon type.
    [System.NonSerialized] public WeaponManagerPD _manager;

    public virtual void Init(CharacterStatsPD stats) => _stats = stats;

    public void SetRenderersEnabled(bool value)
    {
        if (_renderers != null)
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].enabled = value;

        if (weaponParticles != null)
            for (int i = 0; i < weaponParticles.Length; i++)
            {
                if (weaponParticles[i] == null) continue;
                if (value) weaponParticles[i].Play();
                // Clears existing particles instantly rather than letting them fade out on their own — matches
                // the instant-hide _renderers already gets, e.g. don't leave a trail hanging in the air after death.
                else weaponParticles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
    }

    // Called every real frame (not gated to verified sim ticks — same reasoning ApplyVisualAdjustments's own
    // doc comment gives for being safe off viewState) from each weapon subclass's own UpdateView(). Unlike
    // the deterministic sim clock this project otherwise uses for anything that must render identically
    // across clients (see the wobble-clock precedent in WeaponVisualConstants's own doc comment),
    // weaponParticles are purely local cosmetic VFX — no gameplay/sync concern — so a plain per-client
    // Pause()/Play() driven off PausePD is enough; nothing here needs to be predicted state.
    protected void UpdateWeaponParticlePause()
    {
        if (weaponParticles == null) return;
        bool paused = PausePD.instance != null && PausePD.instance.IsPaused;
        for (int i = 0; i < weaponParticles.Length; i++)
        {
            var ps = weaponParticles[i];
            if (ps == null) continue;
            // isPlaying/isPaused guards mean a system already Stopped (e.g. weapon hidden via
            // SetRenderersEnabled(false)) is never accidentally kicked back into emitting just because the
            // game happened to pause/unpause while it was off.
            if (paused && ps.isPlaying) ps.Pause();
            else if (!paused && ps.isPaused) ps.Play();
        }
    }

    public void InitModelVisuals()
    {
        if (_modelVisualsInitialized) return;
        _modelVisualsInitialized = true;

        if (rangeScaleTarget != null)
        {
            float basePos    = rangeScaleTarget.localPosition.z;
            float baseScale  = rangeScaleTarget.localScale.z;
            _hiltEdgeLocal   = basePos - baseScale * 0.5f;
            designOrbit      = basePos + baseScale * 0.5f;
            _baseRangeScaleX = rangeScaleTarget.localScale.x;
        }

        if (yScaleFollowers != null)
        {
            _baseYScales = new float[yScaleFollowers.Length];
            for (int i = 0; i < yScaleFollowers.Length; i++)
                if (yScaleFollowers[i] != null)
                    _baseYScales[i] = yScaleFollowers[i].localScale.y;
        }

        if (xzScaleFollowers != null)
        {
            _baseXZScales = new Vector2[xzScaleFollowers.Length];
            for (int i = 0; i < xzScaleFollowers.Length; i++)
                if (xzScaleFollowers[i] != null)
                    _baseXZScales[i] = new Vector2(xzScaleFollowers[i].localScale.x, xzScaleFollowers[i].localScale.z);
        }

        if (xScaleFollowers != null)
        {
            _baseXScales = new float[xScaleFollowers.Length];
            for (int i = 0; i < xScaleFollowers.Length; i++)
                if (xScaleFollowers[i] != null)
                    _baseXScales[i] = xScaleFollowers[i].localScale.x;
        }

        // lossyScale (world), not localScale — see GetHitboxTipExtraReach's own comment for why the ratio
        // needs to reflect the tip's actual WORLD-space growth (including the weapon root's own Size-driven
        // scale), not just its local scale relative to its immediate parent.
        if (hitboxTip != null)
            _hitboxTipBaseScale = hitboxTip.lossyScale;
        if (hitboxBackTip != null)
            _hitboxBackTipBaseScale = hitboxBackTip.lossyScale;
    }

    public void ScaleModel(float range)
    {
        bool emissionScales = weaponParticles != null && maxEmissionRange > minEmissionRange;
        if (!emissionScales && rangeScaleTarget == null && rangeFollowers == null && particleRangeTarget == null && (yScaleFollowers == null || yScaleFollowers.Length == 0)) return;

        if (emissionScales)
        {
            float rate = Mathf.Lerp(minEmissionRate, maxEmissionRate, Mathf.InverseLerp(minEmissionRange, maxEmissionRange, range));
            for (int i = 0; i < weaponParticles.Length; i++)
            {
                if (weaponParticles[i] == null) continue;
                var emission = weaponParticles[i].emission;
                emission.rateOverTime = rate;
            }
        }

        float rootZ       = transform.localScale.z;
        float rangePart   = range / rootZ - designOrbit;
        // Was rangePart - tipOffset/rootZ — tipOffset itself removed (2026-08-26), confirmed unused/always 0
        // on every weapon (an earlier attempt at this same "pull the tip mesh back" idea that never ended up
        // doing what was wanted, kept at 0 rather than deleted at the time). hitboxTipOffset is the field
        // that's actually meaningful here — see particleFarEdge's own comment below for where it's used.
        float visualFront = rangePart;

        float xRatio = xRangeScaleRatio > 0f
            ? Mathf.Lerp(1f, range / Mathf.Max(0.001f, designOrbit * rootZ), xRangeScaleRatio)
            : 1f;

        if (rangeScaleTarget != null)
        {
            float newX = xRangeScaleRatio > 0f ? _baseRangeScaleX * xRatio : rangeScaleTarget.localScale.x;
            rangeScaleTarget.localScale    = new Vector3(newX, rangeScaleTarget.localScale.y, visualFront - _hiltEdgeLocal);
            var p = rangeScaleTarget.localPosition;
            rangeScaleTarget.localPosition = new Vector3(p.x, p.y, (_hiltEdgeLocal + visualFront) / 2f);
        }

        if (rangeFollowers != null)
            foreach (var f in rangeFollowers)
                if (f != null) { var p = f.localPosition; f.localPosition = new Vector3(p.x, p.y, visualFront); }

        // Far edge: rangePart + hitboxTipOffset (optionally growing with xRatio) — hitboxTipOffset, not the
        // (now removed) tipOffset, is the tip decoration's own authored corner-to-center distance (0.2828 for
        // a 0.4-side square rotated 45° — confirmed for Knight) — already the field GetHitboxTipExtraReach
        // uses for the exact same "how far the tip decoration sticks out" measurement on the damage hitbox
        // side. Near edge: _hiltEdgeLocal - hitboxBackTipOffset, same idea mirrored to a back-of-hilt
        // decoration (see hitboxBackTip's own field comment) — collapses back to plain _hiltEdgeLocal when no
        // back decoration is configured (hitboxBackTipOffset stays 0), so this is a strict superset of the
        // pre-back-tip behavior, not a change for weapons without one. Both decorations are typically one of
        // xzScaleFollowers, so their own corner distance grows proportionally with range too, by the exact
        // same xRatio Blade's own width growth already uses above — a flat offset only holds at design range
        // and falls further behind as the diamond gets proportionally chunkier. Growth on each end is gated
        // independently on that end's own hitbox(Back)TipScalesWithX/Y/Z — the SAME per-weapon flags that
        // already mark whether the damage hitbox's own tip reach scales with range at all. Which specific
        // axis is checked doesn't matter here (that's purely a per-model quirk of which axis a given tip mesh
        // happens to use, e.g. a cylinder tip needing Y instead of X/Z) — this only cares whether ANY of them
        // opted in, since the particle offset is always grown via xRatio (never Y or Z; hitboxTip's own axis
        // flags have no other bearing on this weapon's particle system) once a weapon has signaled that end's
        // decoration scales with range at all. Divided by rootZ same as every other world-unit-at-authored-
        // scale constant in this method — re-multiplied by rootZ once Unity resolves the local write to world
        // space. Scale and position deliberately write to DIFFERENT axes here, unlike every other target in
        // this method — the particle prefab's own Edge shape rotation puts X as the axis its Hierarchy-scaled
        // LENGTH reads from, but the transform's own X POSITION moves it sideways (perpendicular to the
        // blade) — Z is still correct for centering it along the blade's own length. Y stays whatever's
        // authored on both.
        if (particleRangeTarget != null)
        {
            bool  farScalesAtAll  = hitboxTipScalesWithX || hitboxTipScalesWithY || hitboxTipScalesWithZ;
            bool  nearScalesAtAll = hitboxBackTipScalesWithX || hitboxBackTipScalesWithY || hitboxBackTipScalesWithZ;
            float farGrowth       = farScalesAtAll ? xRatio : 1f;
            float nearGrowth      = nearScalesAtAll ? xRatio : 1f;
            float particleFarEdge  = rangePart + (hitboxTipOffset * farGrowth) / rootZ;
            float particleNearEdge = _hiltEdgeLocal - (hitboxBackTipOffset * nearGrowth) / rootZ;
            var s = particleRangeTarget.localScale;
            particleRangeTarget.localScale = new Vector3(particleFarEdge - particleNearEdge, s.y, s.z);
            var pp = particleRangeTarget.localPosition;
            particleRangeTarget.localPosition = new Vector3(pp.x, pp.y, (particleNearEdge + particleFarEdge) / 2f);
        }

        if (_baseYScales != null && xRangeScaleRatio > 0f)
            for (int i = 0; i < yScaleFollowers.Length; i++)
            {
                if (yScaleFollowers[i] == null) continue;
                var s = yScaleFollowers[i].localScale;
                yScaleFollowers[i].localScale = new Vector3(s.x, _baseYScales[i] * xRatio, s.z);
            }

        if (_baseXZScales != null && xRangeScaleRatio > 0f)
            for (int i = 0; i < xzScaleFollowers.Length; i++)
            {
                if (xzScaleFollowers[i] == null) continue;
                var s = xzScaleFollowers[i].localScale;
                xzScaleFollowers[i].localScale = new Vector3(_baseXZScales[i].x * xRatio, s.y, _baseXZScales[i].y * xRatio);
            }

        if (_baseXScales != null && xRangeScaleRatio > 0f)
            for (int i = 0; i < xScaleFollowers.Length; i++)
            {
                if (xScaleFollowers[i] == null) continue;
                var s = xScaleFollowers[i].localScale;
                xScaleFollowers[i].localScale = new Vector3(_baseXScales[i] * xRatio, s.y, s.z);
            }
    }

    // isHighroll out so callers (which already have the attacker/target in scope for TakeDamage/etc.) can
    // invoke OnHighroll themselves — keeps the event-raising at the actual hit call site, same separation
    // the old system's RollDamage/RaiseOnCritStrike had.
    protected float RollDamage(float damage, out bool isHighroll) =>
        WeaponDamageMath.RollDamage(damage, _stats.LuckChance, _stats.LuckFloor, _stats.LuckCeiling, _stats.Highroll, out isHighroll);

    // Highroll/crit/hit/strike/hit-feedback are all relayed straight to _stats (this weapon's owning,
    // persistent CharacterStatsPD) rather than dispatched through a PredictedEvent owned by this weapon
    // itself — see CharacterStatsPD._onHitFeedback's own doc comment (and
    // feedback_predictedevent_anchor_persistent_identity memory) for why: a weapon can be despawned
    // (WeaponManagerPD.DespawnWeapons, e.g. SurgeWeaponCountAugment ending) shortly after landing a hit, and
    // PredictedEvent's non-owner/non-server dispatch only fires once the hit's tick verifies, which requires
    // the OWNING identity to still exist to be resimulated — a despawned weapon can never satisfy that,
    // permanently dropping the notification on other clients. _stats outlives every weapon it owns for the
    // whole match, so routing through it instead removes that race. Subclasses (WeaponMeleePD.Hit(),
    // WeaponAxePD.Hit(), WeaponRangedProjectilePD.ProcessHit()) call _stats.NotifyStrikeHighroll/NotifyCritStrike/
    // NotifyHit/NotifyStrike directly rather than through a wrapper here, since there's no weapon-local state
    // left to hide behind one.
    protected void NotifyHitFeedback(HitFeedbackData data) => _stats.NotifyHitFeedback(data);

    // See WeaponVisualConstants.BaseMidHeightLift's own doc comment for what this value means and why it
    // lives outside this generic class. Never applied to OrbitalPosition()'s actual returned position (that
    // would also lift the hitbox itself, since hitboxTarget hangs off the same real transform, and tilt
    // LookRotation(outward) upward since `outward` would pick up a Y component it shouldn't have). In
    // ApplyVisualAdjustments() (LOCAL offset), a bigger/smaller player (Size augments) already scales this
    // for free via the weapon's own scale inheritance (weapon root's own localScale includes bonusSizePercent
    // — see WeaponManagerPD's own RegisterWeapon/LateUpdate) — no explicit multiply needed there.
    // ApplyHeightLift() (WORLD position, used only while flipped) DOES still need an
    // explicit * bonusSizePercent, since a world assignment inherits no parent scale at all.

    // Visual-only target for the mid-height lift above — a dedicated child (e.g. the weapon's "Model"),
    // NOT this weapon's own root `transform` (that's the real, hitbox-relevant orbit position every
    // LateSimulate computes). Optional: weapons with nothing wired here simply never rise, no error.
    // protected (2026-08-25, was private) — WeaponMeleePD's own pivot-based orbit needs to apply its own
    // rotation logic to this same transform (see its ApplyModelVisualAdjustments()'s own comment) without
    // duplicating the Inspector reference; this is a pure accessibility widening, ApplyVisualAdjustments()
    // itself is unchanged, so WeaponAxePD/WeaponRangedPD's own behavior is unaffected.
    [SerializeField] protected Transform visualHeightTarget;

    // Local rotation visualHeightTarget's model tips to while _stats.IsOrbitFlipped — same flag
    // OrbitalPosition's own plane-tip uses (see its own doc comment), reused here rather than a separate
    // flag since both are the same "weapon looks tipped on its side" visual, just applied to two different
    // transforms (this weapon's own orbit-position math vs. the model's own local rotation). Sign (-90, not
    // 90) must always match OrbitFlipTip's own sign below and CharacterStatsPD.ModelSidewaysTipRotation's —
    // negated on purpose (not a sign-flip of the underlying orbit rotation itself, which would also reverse
    // the player's ordinary idle spin/weapon orbit direction, shared via weaponOrbitAngle) so the burst spins
    // the opposite way while everything about normal (non-burst) orbiting stays untouched.
    private static readonly Quaternion ModelTipRotation = Quaternion.Euler(0f, 0f, -90f);

    // Called from each subclass's own UpdateView() — purely cosmetic, so it's safe off viewState/every
    // frame rather than needing to be gated to the verified pass. Writes LOCAL position/rotation, not world
    // — visualHeightTarget's actual parent is already the weapon's own PredictedTransform-smoothed "Visuals"
    // child, so a local offset rides along with whatever smoothing that parent already provides every frame
    // instead of fighting/bypassing it. An earlier version of the height lift wrote world .position directly
    // (computed from this weapon's raw, tick-quantized root) specifically so it would track fast movement
    // during the burst without lagging — that turned out to reintroduce ordinary-movement jitter (overriding
    // world position every frame bypasses the smoothing entirely), so it's back to a plain local offset until
    // the burst-visuals question (tracking the lift itself during fast movement) is revisited separately.
    protected void ApplyVisualAdjustments()
    {
        if (visualHeightTarget == null || _stats == null) return;
        Vector3 p = visualHeightTarget.localPosition;
        // 0 while flipped, not the usual lift — ApplyHeightLift() already bakes the equivalent mid-height
        // compensation directly into the REAL position during the flip (see its own doc comment), so an
        // extra local offset here would double it up. It'd also be wrong on its own during the flip
        // specifically: this is a LOCAL offset under a parent whose rotation now swings through a much wider
        // range (the vertical sweep) than the normal flat orbit ever does, so a fixed local "up" resolves to
        // a different world direction at every point in that sweep instead of staying vertical.
        //
        // No explicit * bonusSizePercent here (unlike the flipped/world-space case in ApplyHeightLift,
        // which genuinely needs it) — visualHeightTarget is a LOCAL offset, and Unity already re-multiplies
        // any local offset by this weapon's own current scale (the weapon root's own localScale, set from
        // bonusSizePercent — see WeaponManagerPD's own RegisterWeapon/LateUpdate) when resolving to world
        // position. An earlier version of this method wrote WORLD
        // .position directly (see this method's own doc comment above for why that changed), where an
        // explicit multiply really was required since a world assignment inherits no parent scale at all —
        // multiplying again here after switching to a local offset applied bonusSizePercent TWICE (once
        // explicitly, once implicitly via the hierarchy), compounding quadratically instead of scaling
        // linearly with the player's actual size (confirmed via a Titan-augment Knight: weapon floated far
        // too high, well beyond just "proportionally bigger"). Leaving this a plain constant lets the
        // hierarchy's own scale inheritance do 100% of the size-scaling, exactly like it already does for
        // every other unscaled child on this weapon.
        float localLift = _stats.IsOrbitFlipped ? 0f : WeaponVisualConstants.BaseMidHeightLift;
        visualHeightTarget.localPosition = new Vector3(p.x, localLift, p.z);
        visualHeightTarget.localRotation = _stats.IsOrbitFlipped ? ModelTipRotation : Quaternion.identity;
    }

    protected override void LateAwake()
    {
        base.LateAwake();
    }

    // Shared orbital-position formula — WeaponMeleePD/WeaponAxePD (swept hitbox) and WeaponRangedPD (fires
    // at whatever it has line-of-sight to) orbit their owner identically, only what happens once they're
    // positioned differs. WeaponCount comes from _manager so multiple simultaneous weapons space evenly
    // around the circle regardless of which weapon type(s) they are — an outer-track weapon (IsOuterTrack)
    // uses the manager's separate OuterWeaponCount/slot-space instead, so the two rings' spacing math never
    // interferes with each other. baseAngle is always ±_stats.WeaponOrbitAngle in practice — a single value
    // shared by every weapon this player owns, negated by outer-track weapons for the opposite spin
    // direction (see WeaponMeleePD.LateSimulate's own angleSign comment for why the sign has to be applied
    // at the call site, not in here) — so only the SlotIndex-based offset term below actually varies
    // between weapons on the SAME ring.
    // 90° around Z — tips the normal flat (Y=0) orbit offset into a vertical plane instead, applied only
    // when _stats.IsOrbitFlipped (e.g. the spin ability's leap). Combined with a yaw first (see
    // OrbitalPosition below) so the tip happens relative to the current travel direction, not a fixed
    // world axis — same composed-rotation approach an earlier, reverted WeaponHolder-parenting attempt at
    // this used, just applied as a pure math offset here instead of a live Unity parent transform. Negative
    // (mirrors which side of the vertical circle bobs up vs down) rather than reversing weaponOrbitAngle's
    // own rotation direction, which is shared with ordinary idle spin/orbit — see ModelTipRotation's own
    // doc comment above for the full reasoning.
    private static readonly Quaternion OrbitFlipTip = Quaternion.Euler(0f, 0f, -90f);

    // Design/stat-derived orbit radius — the distance from origin this weapon's own root sits at while
    // orbiting normally, computed purely from WeaponRadius/WeaponRange/character clearance/designOrbit.
    // Deliberately reads no live transform position at all, so it stays stable regardless of where the
    // orbit's actual center currently is — see GetOuterReach's own doc comment for why that distinction
    // matters (BWBladeDanceAbilityPD's own orbit-origin override was producing a wildly fluctuating range
    // indicator before this was split out, since the old formula measured live distance to wherever the
    // weapon currently happened to be instead of this nominal, always-consistent value). Shared by
    // OrbitalPosition below and GetOuterReach so the two can never drift apart.
    private float NominalOrbitRadius()
    {
        float halfRange = rangeScaleTarget != null
            ? designOrbit * RootScale.z
            : (IsMelee ? RangeValue / 2f : 0.25f);
        // Outer-track weapons start their own orbit from just past the main track's full reach
        // (WeaponRadius + WeaponRange) instead of straight from the player — dynamically rescales with
        // range automatically since both are live stats, recomputed every frame this runs.
        float weaponRadiusOffset = IsOuterTrack ? _stats.WeaponRadius + _stats.WeaponRange : _stats.WeaponRadius;
        // Character-clearance term — reads _stats.bonusSizePercent directly rather than
        // _stats.transform.lossyScale.x (numerically identical for a player root with no scaled parent,
        // since CharacterStatsPD.AddSizePercent sets transform.localScale = Vector3.one * bonusSizePercent
        // uniformly, but the stat is the direct, canonical source rather than reading it back out of the
        // transform).
        return weaponRadiusOffset + _stats.bonusSizePercent * 0.5f + halfRange;
    }

    protected Vector3 OrbitalPosition(float baseAngle, Vector3 origin)
    {
        int   total = _manager != null ? Mathf.Max(1, IsOuterTrack ? _manager.OuterWeaponCount : _manager.WeaponCount) : 1;
        float rad   = (baseAngle + 360f / total * SlotIndex) * Mathf.Deg2Rad;
        float radius = NominalOrbitRadius();
        Vector3 flatOffset = new(Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);

        // Mathematically a no-op whenever IsOrbitFlipped is false (the common case) — nothing here changes
        // for ordinary movement, so there's no "forgot to turn something off" risk the way a separate moving
        // parent transform had. OrbitFacingYaw (already set by whichever effect enabled the flip, e.g.
        // RogueSpinAbilityPD) orients the tip to the current travel direction, same facing every other part
        // of the leap (model tip, body facing) already uses.
        //
        // Deliberately does NOT fold in SpecialHeightOffset here — every call site computes `outward =
        // orbitPos - origin` from this return value to feed LookRotation, and origin (the player's real,
        // never-lifted transform) doesn't rise the way a lifted orbitPos would, so outward would pick up an
        // artificial upward skew (the weapon ends up facing "up" toward where it thinks the ground/origin
        // is, since origin is now "beneath" it) — the exact same class of mistake BaseMidHeightLift's own
        // doc comment already flagged for the visual-only lift. SpecialHeightOffset gets added separately,
        // by ApplyHeightLift() below, only to the FINAL position actually written to the transform, after
        // outward/rotation has already been computed from this un-lifted result.
        if (_stats.IsOrbitFlipped)
        {
            Quaternion tip = Quaternion.Euler(0f, _stats.OrbitFacingYaw, 0f) * OrbitFlipTip;
            return origin + tip * flatOffset;
        }
        return origin + flatOffset;
    }

    // Adds SpecialHeightOffset (e.g. the spin ability's leap arc), plus — while flipped — the same
    // BaseMidHeightLift compensation the visual-only lift normally applies, to a position that's ALREADY
    // been used to compute outward/rotation. Call this only on the value actually passed to
    // transform.SetPositionAndRotation(), never on anything an `outward` calculation still needs to derive
    // from. See OrbitalPosition's own doc comment for why neither offset can just live inside it.
    //
    // The mid-height term specifically fixes the flipped orbit's vertical circle centering on the player's
    // ground-level pivot (origin) instead of roughly mid-body height — without it, the top of the swing
    // doesn't reach as high as it visually should and the bottom dips well below the ground/sphere, since
    // origin.y is the player's real pivot, not the visible sphere's middle (same gap BaseMidHeightLift
    // always existed to compensate for in the normal flat-orbit case, just needed here on the REAL position
    // instead of only a visual-only child, since weapons don't need to stay hit-detection-accurate at ground
    // level while flipped — IsWeaponsDisabled already means they're not dealing damage at all right now).
    protected Vector3 ApplyHeightLift(Vector3 position)
    {
        float lift = _stats.SpecialHeightOffset;
        if (_stats.IsOrbitFlipped) lift += WeaponVisualConstants.BaseMidHeightLift * _stats.bonusSizePercent;
        return position + Vector3.up * lift;
    }

    // Used by PlayerRangeIndicatorPD to show players exactly where their weapons can actually hit, without
    // duplicating each weapon type's own hitbox geometry. WeaponMeleePD's box (half-extent along hb's local
    // Z = meleeHalfExtents.z * hb.lossyScale.z, default 0.5) and WeaponAxePD's inner-box+outer-capsule zones
    // (whose farthest reach past hb.position works out to exactly hb.lossyScale.z * 0.5 once the
    // handle/blade ratio split cancels out — see WeaponAxePD.ComputeZones) both reduce to the same "half of
    // hb.lossyScale.z past hb.position" formula, so one shared implementation covers both without needing
    // per-subclass overrides.
    //
    // Built entirely off NominalOrbitRadius() (design/stat-derived — WeaponRadius, WeaponRange, character
    // clearance, designOrbit) plus hb.lossyScale/GetHitboxTipExtraReach/hitboxLocalOffset below (all
    // scale- or LOCAL-position-derived, never derived from this weapon's actual live WORLD
    // transform.position/hb.position). Used to measure Vector3.Distance(origin, hb.position) instead, which
    // is exact while a weapon is orbiting normally (that live distance equals NominalOrbitRadius() +
    // hitboxLocalOffset by construction — see each of their own doc comments) but goes wrong the instant
    // anything relocates the orbit's real center away from a flat, origin-centered circle: the flipped
    // vertical orbit (RogueSpinAbilityPD's leap — already separately worked around by
    // PlayerRangeIndicatorPD freezing the ring while IsWeaponsDisabled) and BWBladeDanceAbilityPD's own
    // orbit-origin override (NOT covered by that freeze, since weapons stay fully enabled/functional during
    // the dance — this was the actual bug: the ring visibly grew and shrank as the live distance between
    // the player and the blades' real, relocated position swung wildly). Purely nominal/local math
    // sidesteps both cases at once, no special-casing needed for either — WORLD position is what's
    // radioactive here, not local offsets, which stay meaningful regardless of where the orbit center is.
    public float GetOuterReach()
    {
        if (!IsMelee)
            return NominalOrbitRadius() + EffectiveWeaponRange;

        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        // hitboxTip's own pivot tracks the same range-driven position as hb's far edge (both derive from
        // the same WeaponRange-based math, via rangeFollowers/SetupWeaponRange respectively), so its extra
        // length is added directly on top rather than measured from a second, separate origin.
        //
        // hitboxLocalOffset accounts for WeaponManagerPD.SetupWeaponRange's own hitboxTarget.localPosition.z
        // reassignment (xRangeScaleRatio > 0 path only — see its own comment) — hitboxTarget can sit shifted
        // forward from the weapon ROOT by a WeaponRange-driven amount, which NominalOrbitRadius() has no way
        // to know about (it only knows where the root itself orbits). Read directly off the transform rather
        // than re-deriving SetupWeaponRange's own formula, so this can never drift out of sync with whatever
        // that method actually computed. This is safe to read live despite GetOuterReach otherwise avoiding
        // live position entirely (see class-level doc comment above) — it's a purely LOCAL, parent-relative
        // offset, unrelated to where the orbit's real center currently is, so it's just as immune to
        // BWBladeDanceAbilityPD's own orbit-origin override as everything else this method reads.
        float hitboxLocalOffset = hitboxTarget != null ? hitboxTarget.localPosition.z * transform.lossyScale.z : 0f;
        return NominalOrbitRadius() + hitboxLocalOffset + hb.lossyScale.z * 0.5f + GetHitboxTipExtraReach();
    }

    // Shared by GetOuterReach above and WeaponMeleePD's own hit-detection box, so the actual hitbox and the
    // cosmetic range indicator can never drift apart — the real extra forward reach hitboxTip contributes
    // right now, scaled by (current / base) per marked axis rather than the raw current scale (see
    // _hitboxTipBaseScale's own comment for why). 0 if no tip is assigned.
    //
    // lossyScale (world), not localScale — hitboxTip's LOCAL scale only reflects range-driven changes
    // (xzScaleFollowers's own xRatio), completely missing the weapon root's own Size-driven scale, which
    // hitboxTip inherits as an ordinary descendant. Using localScale here meant the computed extra reach
    // actually shrank as Size grew (xRatio's own formula divides by rootZ), the opposite of what a bigger
    // weapon's tip needs — confirmed against a screenshot where the hitbox/range indicator fell increasingly
    // short of the visual tip. lossyScale captures the tip's true world-space size, Size included.
    protected float GetHitboxTipExtraReach() =>
        ComputeTipExtraReach(hitboxTip, hitboxTipOffset, _hitboxTipBaseScale, hitboxTipScalesWithX, hitboxTipScalesWithY, hitboxTipScalesWithZ);

    // Same measurement as GetHitboxTipExtraReach above, mirrored to hitboxBackTip — see its own field comment
    // (a back-of-hilt decoration, e.g. Bladeweaver's own back spike) for why a separate pair of fields/method
    // exists rather than reusing the front tip's. Consumed by WeaponMeleePD's own hit-detection box to extend
    // it backward, NOT by GetOuterReach (which only measures forward reach for the range indicator — a back
    // decoration isn't part of "how far forward this weapon reaches").
    protected float GetHitboxBackTipExtraReach() =>
        ComputeTipExtraReach(hitboxBackTip, hitboxBackTipOffset, _hitboxBackTipBaseScale, hitboxBackTipScalesWithX, hitboxBackTipScalesWithY, hitboxBackTipScalesWithZ);

    private static float ComputeTipExtraReach(Transform tip, float offset, Vector3 baseScale, bool scalesX, bool scalesY, bool scalesZ)
    {
        if (tip == null || offset == 0f) return 0f;

        float ratio = 0f;
        int   axisCount = 0;
        if (scalesX && baseScale.x > 0f) { ratio += tip.lossyScale.x / baseScale.x; axisCount++; }
        if (scalesY && baseScale.y > 0f) { ratio += tip.lossyScale.y / baseScale.y; axisCount++; }
        if (scalesZ && baseScale.z > 0f) { ratio += tip.lossyScale.z / baseScale.z; axisCount++; }
        return offset * (axisCount > 0 ? ratio / axisCount : 1f);
    }
}
