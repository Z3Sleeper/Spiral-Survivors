using System;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Which spawn-cap bucket an enemy counts against — decided entirely by whichever spawner created it
// (which list it pulled the prefab from), never by the prefab/class itself. This is what lets a boss just
// be an EnemyBasePD spawned via SpawnCategory.Boss instead of needing a dedicated BossBasePD subclass.
public enum EnemySpawnCategory { Normal, Elite, Pleb, Boss }

// Claimed is the gap between "an owner has selected this enemy to grab" (IEnemyPD.ClaimForOrbit — frozen in
// place, invulnerable/kinematic/movement-disabled all already applied, IsBeingHeld already true so a SECOND
// owner's own grab-scan correctly excludes it) and "the actual pull motion has started" (IEnemyPD.OrbitTarget
// — only called once this specific pleb's own windup finishes). LateSimulate() does nothing during Claimed
// (the freeze is entirely SetKinematic's job); Pulling/WaitingToOrbit/Orbiting share one continuous position
// formula there (straight-line lerp toward the live slot while Pulling, frozen at the slot — angle held
// fixed, no rotation — while WaitingToOrbit, angle advances at a fixed rate while Orbiting).
//
// WaitingToOrbit (2026-08-17) sits between Pulling and Orbiting specifically because OrbiterGloomPD/
// KingOrbiterPD now pull multiple plebs in on a STAGGERED schedule (see each one's own BeginPull/
// DispatchPull doc comment) — if a pleb started actually rotating (Orbiting) the instant ITS OWN pull
// finished, plebs that landed earlier would have been rotating longer than ones that landed later by the
// time the owner considers the whole group "in," visibly skewing their intended even angular spacing
// (confirmed live: "orbit angles look a little off"). A pleb that finishes pulling now just sits frozen at
// its slot (WaitingToOrbit) until the owner explicitly calls IEnemyPD.BeginOrbiting() on every pleb sharing
// that same orbit radius/ring at once (once ALL of them have arrived), so the whole group starts rotating
// together, in sync, preserving the intended spacing — see OrbiterGloomPD/KingOrbiterPD's own Pulling case
// for exactly where that release fires.
public enum EnemyOrbitPhase { None, Claimed, Pulling, WaitingToOrbit, Orbiting }

// Shared attack-indicator sizing math — not enemy-specific (RemoteSpikeBurstPD, a standalone hazard, is
// not an EnemyBasePD<TState> at all), so this lives as a static class rather than a protected method on
// EnemyBasePD<TState>, same reasoning as WeaponDamageMath sitting outside WeaponBasePD<TState>. Project
// convention: line indicators are planes, radial indicators are cylinders — see each method for why the
// axis math differs between the two.
public static class IndicatorMath
{
    // Sizes/positions a line indicator (a plane) along its own local Z so it visually spans from its
    // origin out to `distance` — Z scale is 10% of distance, Z position is half of it (since the plane
    // pivots at its own center, moving it forward by half its own new length makes it start at the origin
    // and reach exactly `distance` away). X/Y of both scale and position are left untouched — they're the
    // indicator's own authored width/thickness/offset.
    public static void ApplyLineTransform(Transform t, float distance)
    {
        if (t == null) return;
        var scale = t.localScale;
        var pos   = t.localPosition;
        t.localScale    = new Vector3(scale.x, scale.y, distance * 0.1f);
        t.localPosition = new Vector3(pos.x, pos.y, distance * 0.5f);
    }

    // Sizes a radial indicator (a cylinder) so its footprint matches `radius` — x2 because a cylinder's
    // localScale.x/z map to diameter, not radius. Y is left untouched (the indicator's own authored
    // flatness).
    public static void ApplyRadialTransform(Transform t, float radius)
    {
        if (t == null) return;
        float diameter = radius * 2f;
        var scale = t.localScale;
        t.localScale = new Vector3(diameter, scale.y, diameter);
    }

    // BoxCollider.size/center are separate from Transform.localScale/localPosition — size is a literal
    // local-space length, not a scale multiplier off some baseline mesh, so unlike ApplyLineTransform this
    // sets Z size to `distance` directly (no 0.1 ratio) and centers it the same way (half of distance, so
    // the near edge sits at the origin and the far edge reaches exactly `distance` away). X/Y of both are
    // left untouched — the hitbox's own authored width/height. A single range field on the enemy script is
    // the source of truth — both this and ApplyLineTransform derive from it, so the indicator and the
    // actual hitbox can never drift out of sync with each other.
    public static void ApplyLineCollider(BoxCollider box, float distance)
    {
        if (box == null) return;
        var size   = box.size;
        var center = box.center;
        box.size   = new Vector3(size.x, size.y, distance);
        box.center = new Vector3(center.x, center.y, distance * 0.5f);
    }
}

public interface IEnemyPD
{
    bool _isDead { get; }
    float CurrentHealth { get; }
    float MaxHealth { get; }
    // Trivial passthrough of the enemy's own xpValue field (post-ConfigureSpawn, so already reflects any
    // xpMultiplier applied at spawn time — e.g. EnemySpawnerPD's own boss-wave plebs, spawned with
    // xpMultiplier 0 while a boss is alive) — added specifically so an OnEnemyDeath reaction can identify
    // and exclude a "worth nothing" enemy death from its own counting (see
    // OnEnemyDeathHealthStackingAugment's own doc comment for the first consumer) without needing every
    // future consumer to reach into EnemyBasePD directly.
    float XpValue { get; }
    // Same shape as XpValue directly above, but for upgrade points — deliberately a SEPARATE tracked value
    // (2026-08-29), not derived from XpValue at all beyond happening to start at similar author-tuned
    // numbers per enemy. See EnemyBasePD.pointsValue's own doc comment.
    float PointsValue { get; }
    // Whether the specific hit that killed this enemy was a crit — read from an OnEnemyDeath reaction so a
    // kill-triggered effect (e.g. OnKillLaunchEnemiesAugment) can inherit the killing hit's crit status
    // instead of rolling its own independent chance.
    bool WasKilledByCrit { get; }
    // Same as WasKilledByCrit above, for highroll (2026-09-14, extended to match) — whether the specific hit
    // that killed this enemy was itself a highroll.
    bool WasKilledByHighroll { get; }
    string EnemyName { get; }
    // 1 = full time remaining before enraging, 0 = enraged (or this enemy isn't a Boss — enrage is
    // Boss-only). Same generic-regardless-of-TState reasoning as every other member here.
    float EnrageProgress { get; }
    Transform transform { get; }
    // The smoothly render-frame-interpolated visuals child — see EnemyBasePD.Visuals's own doc comment.
    Transform Visuals { get; }
    // Generic per-enemy size scalar — see EnemyBasePD.size's own doc comment. Exposed on the interface
    // (2026-08-19) so DamageNumberManager.Spawn can straight-multiply its own spawn-height offset by this,
    // same "bigger target needs a taller offset" convention CharacterStatsPD.bonusSizePercent already
    // established for players.
    float Size { get; }
    // Exposed on the interface (2026-08-31) so generic cross-identity code can read one enemy's current
    // attack speed through another's own reference — added specifically for the shared held-enemy orbit
    // position code (EnemyBasePD.LateSimulate's own orbitAngleDeg advance), which resolves its owner only as
    // an IEnemyPD and needs the OWNER's (not the held enemy's own) EffectiveAttackSpeed to make orbit
    // rotation rate respond to a Slow landing on the holder — see EnemyBasePD.EffectiveAttackSpeed's own doc
    // comment for the full composition (base attackSpeed, already difficulty-scaled by ConfigureSpawn, times
    // any active Slow multiplier).
    float EffectiveAttackSpeed { get; }
    // Stable, rollback-safe reference to this enemy — safe to store in predicted state (e.g. a
    // PredictedEvent payload), unlike a raw IEnemyPD/Transform reference, since enemies are pooled and
    // reused for new spawns. Trivial passthrough — every PredictedIdentity already carries this.
    PredictedObjectID ObjectId { get; }
    // Increments every time this pooled slot starts a new logical enemy life (see EnemyBasePD.Simulate's
    // !b.initialized block). A raw Transform/IEnemyPD reference alone can't tell you whether the enemy it
    // originally pointed at is still alive — pooling means the same GameObject can get silently reused for
    // a completely different spawn, so anything that needs to track "is this SPECIFIC enemy still the one
    // I started tracking" (e.g. DamageNumber following an enemy that dies) must compare a captured
    // SpawnGeneration snapshot against this live value, not just null/active-check the Transform.
    int SpawnGeneration { get; }
    // False until this enemy's own first Simulate() tick for its CURRENT life has actually run (see
    // EnemyBasePD.Simulate's own !b.initialized block) — that block unconditionally resets
    // movementDisabled/invulnerable/externalKinematic/physicsColliderDisabled/hurtboxDisabled/
    // damageOverridden/orbitPhase back to their fresh-spawn defaults (and directly re-enables the hurtbox/
    // physics collider), since it has to assume nothing external has touched this life's state yet. A caller
    // that wants to externally freeze/claim/orbit ANOTHER enemy (e.g. OrbiterGloomPD/KingOrbiterPD's own grab
    // scan) MUST exclude candidates where this reads false — a freshly-spawned-this-tick enemy that gets
    // grabbed before its own first Simulate() tick runs would have that whole reset silently stomp the grab's
    // protective state (hurtbox re-enabled, invulnerable cleared, orbitPhase reset to None) the instant that
    // tick finally does run, leaving it non-invincible and no longer actually orbiting while the grabber
    // still thinks it's held — confirmed live (2026-08-12), not theoretical.
    bool HasInitialized { get; }
    // This enemy's own current damage output — a method, not a property, for interface consistency with
    // TakeDamage/AddKnockbackVelocity/etc. below. Returns the override value while SetDamageOverride has one
    // active, otherwise each subclass's own GetOwnDamage() (e.g. KingSpikePD's own Charging multiplier —
    // see that override's own doc comment). NOT virtual on EnemyBasePD<TState> itself — see GetOwnDamage's
    // own doc comment for why subclasses hook that instead of this.
    float GetDamage();
    // Forces GetDamage() to return `damage` instead of this enemy's own GetOwnDamage(), until reverted —
    // e.g. OrbiterGloomPD gives each held pleb its own damage (scaled by its own orbitDamageMultiplier) for
    // the duration of the hold, so "using a grabbed enemy like a weapon" hits as hard as its wielder. Generic,
    // not Orbit-specific — any future mechanic needing to temporarily substitute an enemy's own damage output
    // can reuse this instead of inventing its own GetDamage() override chain (which is exactly what the
    // orbit-coupled version of this method used to do, before this was split out). Same "small composable
    // primitive, caller decides when to revert" shape as SetInvulnerable/SetHurtboxDisabled — overridden=false
    // ignores whatever `damage` was last passed, so callers don't need to separately track/restore anything.
    void SetDamageOverride(bool overridden, float damage = 0f);
    // isHighroll: default false, trailing/optional (2026-09-14) — every existing caller compiles unchanged;
    // only a caller with a real isHighroll value in scope (a real weapon strike, or an onhit/onstrike effect
    // inheriting data.isHighroll) needs to pass it, so WasKilledByHighroll below reflects the actual killing
    // hit's status regardless of which source lands it.
    void TakeDamage(float damage, bool isCrit, PlayerID attackerOwner, bool isHighroll = false);
    void AddKnockbackVelocity(Vector3 impulse);
    void Stun(float duration);
    // multiplier is how much of normal move speed remains while active (e.g. 0.5 = half speed), not a
    // separate "50% slower" subtraction — see EnemyBasePD.Slow()/EffectiveMoveSpeed's own doc comments.
    void Slow(float multiplier, float duration);
    // Bleed status (2026-10-06, ExeBigAxeBleedAugment) — bucket model, adds damagePerSecond into a running
    // total and refreshes the shared duration — see EnemyBasePD.ApplyBleed's own doc comment for the full
    // mechanism.
    void ApplyBleed(float damagePerSecond, float duration, PlayerID attacker);
    // Cancels this enemy's current active-attack-sequence (if any) outright and returns it to Moving/idle —
    // see EnemyBasePD.Interrupt()'s own doc comment. Call this BEFORE a paired Stun()/AddKnockbackVelocity
    // on the same hit (e.g. AreaEffectCastHelper's own interrupt param), never after — see Interrupt()'s
    // own doc comment for why the ordering matters.
    //
    // Returns whether this call actually cancelled something (got past the isDead/CanBeInterrupted guard) —
    // e.g. DashThroughInterruptAugment reads this alone as its "did this land on something real" signal.
    // Reliable on its own now that CanBeInterrupted defaults to false and each subclass opts in only for the
    // specific states that are both committed (not just stunnable/CC-vulnerable) and meant to be cancelable —
    // see CanBeInterrupted's own doc comment.
    bool Interrupt();
    // Flat, crowdControlResist-immune true-stun granted on every successful interrupt (SI) — see
    // EnemyBasePD's own implementation doc comment for the exact "flat portion + optional CC-resist-scaled
    // tail" shape. Callers should go through CharacterStatsPD.NotifyInterrupted rather than calling this
    // directly — that's the one place this actually gets invoked from.
    void InterruptTrueStun(float additionalResistibleDuration = 0f);
    // Hard on/off switch for this enemy's own normal approach movement (HandleMovement) —
    // deliberately generic/public so ANOTHER entity (at least another enemy; nothing restricts it to that)
    // can freeze/unfreeze a DIFFERENT enemy's movement from the outside, e.g. one enemy anchoring/rooting
    // another as part of its own attack. Does NOT touch isStunnable, the rigidbody's kinematic state, attack
    // state machines, or anything else — an enemy with movement disabled still attacks/rotates/telegraphs
    // completely normally, it just never walks. Purely a movement gate; pair it with Stun()/Interrupt()
    // separately if a caller also wants those effects. See EnemyBasePD.HandleMovement's own doc comment for
    // exactly what this gates.
    void SetMovementDisabled(bool disabled);
    void TriggerHitFeedback(float damage, bool isCrit, bool isHighroll, CharacterStatsPD attacker, bool isPriority = false, bool overrideSfx = false, AudioClip sfxOverride = null);

    // Whether this enemy currently takes 0 damage — gates TakeDamage only (this enemy taking a hit), never
    // what this enemy itself deals to a player. Does NOT grant CC immunity — an invulnerable enemy is still
    // fully stunnable/slowable/targetable, matching CharacterStatsPD.IsInvincible's own identical
    // player-side contract. Also suppresses this enemy's own damage-number popups (see TriggerHitFeedback)
    // so a 0-damage hit never shows a "0" — does not suppress the hit-flash or SFX, an explicit, narrower
    // scope than a full "ignore this hit entirely" gate. Drives invulnerableShell (see UpdateView's own
    // viewB.invulnerable-driven toggle), independent of hurtbox state either way.
    //
    // Deliberately ONE-WAY with SetHurtboxDisabled below, not a symmetric coupling: an untargetable enemy
    // MUST also be invulnerable (SetHurtboxDisabled(true) forces this internally — see its own doc comment
    // for why), but an invulnerable enemy is NOT necessarily untargetable — calling this alone leaves the
    // hurtbox untouched, for the "takes no damage but is still interactable/stunnable/targetable" case.
    bool IsInvulnerable { get; }
    void SetInvulnerable(bool invulnerable);

    // Whether another entity currently controls this enemy's position (pulled/orbiting) — see OrbitTarget's
    // own doc comment. Read-only; callers don't set this directly, it's derived from orbit state.
    bool IsBeingHeld { get; }

    // True exactly while this held enemy has finished its own Pulling-phase MoveTowards (reached the orbit
    // slot exactly, see OrbitTarget's own doc comment) and is frozen there awaiting BeginOrbiting() — i.e.
    // it has genuinely ARRIVED, not just "enough time should have passed." Added (2026-08-31, back when
    // Pulling was still duration-based) so a holder's own per-ring release gate (KingOrbiterPD/
    // OrbiterGloomPD's Pulling case) can query this directly instead of independently re-deriving an
    // equivalent time threshold from the same constants and hoping the two stay in sync — that reasoning
    // held even more once Pulling became speed-based (2026-09-12), since a flat speed means different
    // pulls now genuinely take different amounts of real time, so there's no shared duration constant left
    // to derive an estimate from at all. A caller gating a ring's release on this should also treat a dead
    // member as non-blocking (IsDead) — a pleb that dies mid-Pulling never reaches WaitingToOrbit
    // (LateSimulate's own isDead/wiped early-return means it simply stops moving toward its slot), so
    // waiting on IsWaitingToOrbit alone would stall that ring's release forever.
    bool IsWaitingToOrbit { get; }

    // True only while orbitPhase is EXACTLY Pulling — 2026-09-16, added alongside the fix for a real bug this
    // gap enabled: KingOrbiterPD.UpdatePullLineIndicators used to exclude a pleb from its active-indicator
    // list via `enemy.IsWaitingToOrbit` (a strict equality against ONE specific enum value), intending "stop
    // showing the indicator once this pleb has arrived." But EnemyOrbitPhase has a THIRD value past that —
    // Orbiting — which a pleb's own ring releases it into (EndPullingRing -> BeginOrbiting) independently of
    // whether the KING's own overall phase is still "Pulling" for OTHER, still-in-progress rings. Once a
    // pleb moved from WaitingToOrbit to Orbiting, `IsWaitingToOrbit` read false again, silently re-admitting
    // it into the active list — using its now-genuinely-orbiting live position — until every other ring also
    // finished. Use this instead for any "is this pleb's pull movement still actually ongoing" check —
    // inclusive of the correct state, not exclusive of one specific wrong one.
    bool IsPulling { get; }

    // True exactly while this enemy is mid-flight from a LaunchAlong() call (EnemyBaseState.launchActive),
    // false once it's actually finished traveling its full distance. Added (2026-08-31) for the same reason
    // as IsWaitingToOrbit above — KingOrbiterPD's own ShootFlight release gate used to estimate flight time
    // via shootingRange/shotSpeed instead of checking whether the shot had actually finished. Same dead-
    // member caveat applies: LateSimulate's own isDead/wiped early-return means a pleb that dies mid-flight
    // never clears launchActive on its own, so a caller must also treat IsDead as non-blocking.
    bool IsLaunching { get; }

    // Live read of this enemy's own rigidbody kinematic flag — regardless of WHY it's kinematic (a
    // subclass's own windup/charge/leap phase, SetKinematic() forced ON by a holder, etc.). Read-only;
    // exists so generic cross-enemy code (e.g. EnemyBasePD.RecomputeSeparationPush) can exempt a kinematic
    // neighbor without needing to know which specific mechanism made it kinematic.
    bool IsKinematic { get; }

    // Hard on/off switch for this enemy's own rigidbody kinematic flag, forced ON by an outside caller (e.g.
    // an enemy that's grabbed this one) independent of whatever this enemy's own attack state machine would
    // otherwise set it to. Asymmetric on purpose: ON is a defensive per-tick re-assertion (never fights a
    // legitimate internal reason to also be kinematic); OFF is an immediate, real revert (isKinematic doesn't
    // spontaneously un-set itself, so the caller releasing this enemy MUST be the one to turn it back off) —
    // see EnemyBasePD's own implementation for the exact mechanics. Doesn't touch SetMovementDisabled,
    // SetInvulnerable, or anything else — pure kinematic gate, callers compose it with those separately.
    void SetKinematic(bool kinematic);

    // Hard on/off switch for this enemy's own physics (push) collider, forced OFF by an outside caller —
    // e.g. a grabbed enemy that's now frozen/kinematic shouldn't still physically block/push players or
    // other enemies. Same asymmetric shape as SetKinematic: ON (disabled=true) is a defensive per-tick
    // re-assertion, OFF is an immediate direct revert. Doesn't touch the hurtbox or contact-damage collider —
    // pure physics-push gate, callers compose it with SetKinematic/SetMovementDisabled/SetInvulnerable
    // separately.
    void SetPhysicsColliderDisabled(bool disabled);

    // Hard on/off switch for this enemy's own hurtbox, forced OFF by an outside caller — makes it
    // "untargetable" the same way LeaperGloomPD's own leap does (which now calls this instead of touching
    // _hurtbox directly): weapon hit-detection queries CombatLayerMasks.EnemyHurtboxMask directly, so with
    // this off nothing can register a hit here at all, not just a 0-damage one — otherwise homing/pierce/
    // cleave/ricochet effects would still waste themselves on an enemy that can't be meaningfully damaged.
    //
    // ALSO calls SetInvulnerable(disabled) internally — untargetable implies takes-no-damage, always,
    // enforced here so a caller can never disable the hurtbox while forgetting to also grant invulnerability
    // (which would just look like an invisible wall taking real damage nobody can see land). This is
    // deliberately ONE-WAY, not a symmetric coupling — SetInvulnerable itself never touches the hurtbox, so
    // "invulnerable but still targetable/stunnable/interactable" remains a valid, distinct state reachable
    // by calling SetInvulnerable alone. See that method's own doc comment for the full contrast.
    //
    // Same asymmetric ON/OFF shape as SetKinematic/SetPhysicsColliderDisabled: ON is a defensive per-tick
    // re-assertion, OFF is an immediate direct revert. Doesn't touch kinematic/movement-disabled — callers
    // compose those separately.
    void SetHurtboxDisabled(bool disabled);

    // Marks this enemy as claimed by `owner` the MOMENT it's selected to be grabbed — before any pull motion
    // starts, before the owner's own windup even finishes. Sets orbitPhase to Claimed (so IsBeingHeld reads
    // true immediately), letting IsBeingHeld actually protect against a second owner grabbing the same enemy
    // mid-windup — the race this exists to close. Deliberately separate from OrbitTarget (which only starts
    // the actual pull motion once the owner's windup ends) — this call does nothing to this enemy's position;
    // the caller is expected to ALSO call SetKinematic(true)/SetMovementDisabled(true)/SetInvulnerable(true)
    // in the same breath (same "small composable primitives" convention as everything else here) so the
    // enemy is frozen in place the instant it's claimed, not just marked unavailable to other owners.
    void ClaimForOrbit(PredictedObjectID owner);

    // Starts this enemy pulling toward, then orbiting, `owner` — used by an enemy that's grabbed this one
    // (e.g. Orbiter's pull attack). `targetAngleDeg` is the CALLER-assigned final orbit slot (e.g. Orbiter
    // evenly-spacing its held plebs around itself) — this enemy does NOT compute its own angle from whatever
    // bearing it happened to be grabbed at; the caller decides where every held enemy ends up and is
    // expected to use the SAME angle here as it shows in its own windup indicator (see OrbiterGloomPD.UpdateView
    // for that precedent). `ownerPosition` is read once, at the instant of the call, purely to seed
    // `targetAngleDeg`'s current-tick target position for the very first pull-position lerp step; every tick
    // afterward this enemy re-resolves `owner`'s LIVE position itself (see EnemyBasePD.LateSimulate()'s own
    // doc comment for why that has to happen in ITS OWN LateSimulate(), not be pushed in by the owner). Pull
    // phase: this enemy's OWN current world position is MoveTowards-stepped, at a flat `pullSpeed`
    // units/sec (2026-09-12, was a fixed-DURATION Cartesian Lerp — every held enemy took the same seconds
    // to arrive regardless of how far away it started, which read as inconsistent travel speed across
    // different grab distances; a flat speed instead means farther pulls just take proportionally longer,
    // matching KingOrbiterPD's own shotSpeed-driven LaunchAlong convention), toward `owner`'s live position
    // + the `targetAngleDeg`/`radius` orbit slot — a Cartesian MoveTowards, not a radius-only one, so it
    // visibly moves toward wherever it's actually going to end up rather than snapping sideways onto the
    // target angle first. MoveTowards itself never overshoots (clamps exactly to the destination once
    // within one tick's step), so arrival is just "did this tick's step reach the slot exactly," no
    // separate tolerance needed. Once the pull completes this enemy does NOT start orbiting automatically
    // anymore (2026-08-17) — it freezes at its slot (WaitingToOrbit — see EnemyOrbitPhase's own doc
    // comment) until the caller explicitly calls BeginOrbiting() below, once every pleb sharing that same
    // radius/ring has similarly arrived (see IsWaitingToOrbit's own doc comment for why release is gated on
    // real arrival, not an estimated duration). Once actually orbiting: radius fixed at `radius`, angle
    // advances from `targetAngleDeg` at `angularSpeedDegPerSec`. Does NOT itself set movement-disabled/
    // kinematic/invulnerable — the caller composes those separately (same "small composable primitives"
    // convention as Stun/Interrupt).
    void OrbitTarget(PredictedObjectID owner, Vector3 ownerPosition, float radius, float pullSpeed, float angularSpeedDegPerSec, float targetAngleDeg);
    // Releases a pleb that finished its own pull-in (WaitingToOrbit) into actually rotating (Orbiting) — see
    // EnemyOrbitPhase's own doc comment for why this hand-off exists. A no-op while not currently
    // WaitingToOrbit (e.g. still Pulling, or already Orbiting) — safe to call speculatively without checking
    // phase first.
    void BeginOrbiting();
    // Ends pulling/orbiting immediately — does not itself touch kinematic/movement-disabled/invulnerable
    // (the caller releasing this enemy is expected to also call SetKinematic(false)/SetMovementDisabled(false)/
    // SetInvulnerable(false) as separate, explicit steps).
    void StopOrbiting();

    // Directly overwrites this enemy's own live orbit angular speed (see OrbitTarget's own doc comment for
    // the pull->orbit mechanism this feeds) without touching orbitPhase, position, or radius at all — e.g. an
    // owner freezing a held enemy in place mid-orbit (0 speed) without dropping it, matching every other
    // "small composable primitive" setter in this interface (SetKinematic/SetMovementDisabled/etc.). A no-op
    // while not currently orbiting (nothing reads orbitAngularSpeedDegPerSec unless orbitPhase == Orbiting).
    void SetOrbitAngularSpeed(float angularSpeedDegPerSec);

    // Owner-driven, recomputed every tick from the OWNER's own live stun state (see OrbiterGloomPD/
    // KingOrbiterPD's own RefreshHeldDamageOverrides for the call site — `IsTrueStunned && isStunnable` on
    // the OWNER, meaning it's genuinely frozen by a true stun right now, not just carrying an unexpired timer
    // through a committed phase it can't be frozen during). While true: this held enemy's own LateSimulate()
    // skips the whole orbit-position recompute (sits still at wherever it already was), OnContactTrigger
    // blocks its contact damage, and UpdateView shows the yellow stun tint — three effects off one flag.
    // Never a one-shot transition (same reasoning as [[feedback_untracked_rigidbody_property_needs_per_tick_reassertion]]
    // even though this isn't a Rigidbody property — a predicted State bool still needs to self-correct every
    // tick rather than rely on a paired start/end call, so it can never desync or get stuck set if the owner's
    // own stun ends on a resimulated tick that took a different path) — reasserted unconditionally every tick
    // the pleb is held, true or false, not just toggled on stun-start/stun-end.
    void SetOrbitFrozen(bool frozen);

    // Independent, fixed-direction/fixed-distance straight-line launch — e.g. a boss "shooting" a held enemy
    // at a target. Deliberately NOT built on the shared isDashing/HandleDash dash primitives (several pleb
    // subclasses use that same isDashing/dashJustEnded plumbing for their own attack-chain bookkeeping — an
    // externally-injected dash risks corrupting it) and NOT coupled to orbitPhase/IsBeingHeld (see
    // EnemyBaseState.launchActive's own doc comment) — a launching enemy can still be legitimately "held" by
    // its own owner throughout the flight. speed-based (not duration-based) so retuning distance or speed
    // independently doesn't require the caller to also recompute the other. No damage/hit-detection of its
    // own — a launched enemy is still fully alive with its own contact-damage collider active throughout, so
    // it deals whatever GetDamage() currently returns (e.g. an owner's own SetDamageOverride) to anything it
    // physically touches via the existing, unmodified contact-damage system; this is pure movement.
    void LaunchAlong(Vector3 direction, float distance, float speed);

    // Which spawn-cap bucket this enemy counts against (see EnemySpawnCategory's own doc comment) — exposed
    // generically so code that doesn't know (or care about) this enemy's concrete TState can still filter by
    // it, e.g. an attack that only ever targets Pleb-tier enemies regardless of which prefab/tier spawned them.
    EnemySpawnCategory SpawnCategory { get; }

    // Lets a spawner mark a freshly hierarchy.Create()'d enemy with which category spawned it (deciding
    // which Active*Count bucket it counts against) and apply co-op/elite stat scaling, generically,
    // regardless of which concrete TState the enemy's subclass uses — same reason this interface exists
    // at all.
    // moveSpeedMultiplier added alongside the original 3 — same "generic co-op/elite/tier stat scaling"
    // purpose, just covering the one stat ConfigureSpawn didn't touch before (EnemySpawnerPD.cs's own
    // tier-scaling formulas need all 4).
    // tier — the raw tier NUMBER (not a multiplier), stored on EnemyBasePD.spawnTier for the rest of this
    // enemy's life. Needed because Die()'s own XP orb spawn has to know which tier this enemy was spawned
    // at, not whatever tier the spawner currently happens to be on (which may have advanced by the time this
    // specific enemy finally dies) — see spawnTier's own doc comment.
    // attackCooldownMultiplier — Threat Level 5 ("enemies attack twice as often"). Unlike the other 4
    // multipliers, EnemyBasePD has no generic attackCooldown field of its own to apply this to (every
    // enemy's own attack-cooldown field is declared independently on its own subclass) — ConfigureSpawn just
    // stores it on EnemyBasePD.attackCooldownMultiplier, and each subclass's own OnConfigureSpawn() applies
    // it to its own field, same "_authoredX snapshot" treatment as every other per-enemy tier-scaled field.
    // pointsMultiplier (2026-08-29) — separate from xpMultiplier, see pointsValue's own doc comment for why
    // upgrade points are no longer derived from XP at all.
    // attackSpeedMultiplier (2026-08-31, difficulty-only; tier component added 2026-09-14) — applied directly
    // to EnemyBasePD.attackSpeed (unlike attackCooldownMultiplier above, this generic field already exists on
    // the base class, same shape as moveSpeedMultiplier/moveSpeed) — see EnemySpawnerPD.
    // difficultyAttackSpeedGrowthPerLevel/attackSpeedTierGrowth's own doc comments for why this exists: a
    // player's own move-speed progression had been invalidating certain enemies' fixed-duration windups, since
    // every windup/attack-state timer across the roster already scales by EffectiveAttackSpeed (which this
    // feeds into). Deliberately kept separate from moveSpeedMultiplier/move speed scaling generally —
    // coincidentally the same per-level/per-tier rates today, not a true pairing, per explicit design.
    void ConfigureSpawn(EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier, float pointsMultiplier, float moveSpeedMultiplier, float attackCooldownMultiplier, float attackSpeedMultiplier, int tier);

    // Generic death-hold API — see EnemyBasePD.RequestDeathHold's own doc comment. Only safe to call from a
    // verified-state reaction (e.g. CharacterStatsPD.OnEnemyDeath), never from raw Simulate()-context code.
    void RequestDeathHold();
    void ReleaseDeathHold();
    // Starts the on-kill launch effect (OnKillLaunchEnemiesAugment) — see EnemyBasePD.StartKillLaunch's own
    // doc comment. Die() already reserved this effect's death-hold via the killer's killEventCount; the
    // caller doesn't need to request or track one itself, only trust HandleKillLaunch to release it.
    void StartKillLaunch(Vector3 direction, float speed, float distance, float damage, bool isCrit = false, bool isHighroll = false);

    // Called by EnemySpawnerPD (the enemy-targeting hub) whenever ANY friendly's taunt/invisible state
    // changes — generic regardless of concrete TState, same reason this interface exists at all.
    // IFriendlyPD (2026-08-30, was CharacterStatsPD) — EnemySpawnerPD now subscribes every
    // FriendlyTargetRegistryPD member, not just connected players, so a future non-player friendly can pull
    // enemy attention via the same Taunt mechanic (see IFriendlyPD.AddTauntChangedListener's own doc
    // comment). See EnemyBasePD<TState>'s own implementation for the actual reaction logic.
    void OnPlayerTauntChanged(IFriendlyPD player, bool isTaunting);
    void OnPlayerInvisibleChanged(IFriendlyPD player, bool isInvisible);

    // Difficulty-pacing wipe (e.g. EnemySpawnerPD clearing the field right before a boss spawns) —
    // deliberately NOT a real death: no XP orbs, no RaiseOnEnemyDeath (so no on-kill augment/effect ever
    // reacts to this), no death-hold/kill-launch interaction, no red death-flash. See EnemyBasePD's own
    // implementation for the full reasoning.
    void SilentDespawn();
}

// Per-target contact damage cooldown — contact damage repeats over time (unlike a one-shot attack),
// so each target touching the enemy needs their own independent cooldown, not a single shared one.
// target (2026-08-30, was PlayerID player) — rekeyed to PredictedObjectID so this works against any
// IFriendlyPD, not just a connected player (a future non-player ally has no PlayerID). Same rekey
// WeaponBasePD.AllyHitCooldownEntry already got for the identical reason.
public struct ContactCooldown
{
    public PredictedObjectID target;
    // Ticks, not seconds — stored as an int specifically for the bit-packing win: PurrNet's delta-packer
    // costs a full 33 bits for any float that changed since last snapshot, but this field changes almost
    // every tick while any player is in contact (it rarely gets the cheap "unchanged" 1-bit case a float
    // would), whereas a changed int only costs a zigzag-varint of the diff — a few bits per tick instead
    // of 33. Multiplied by every enemy, every tick, this class of field was the single largest
    // recoverable contributor to per-enemy network/CPU cost (confirmed via the PurrDiction profiler).
    public int remaining;
}

// Common enemy simulation fields; embedded inside each enemy's TState struct.
public struct EnemyBaseState : IDuplicate<EnemyBaseState>
{
    public float health;
    public bool isDead;
    public float stunTimer;
    // Separate from stunTimer above — stunTimer is the shared BEHAVIORAL gate (set by both a true Stun()
    // call and AddKnockbackVelocity's own derived micro-stun), but the yellow stun tint (EnemyBasePD.
    // UpdateView) should only show for a real Stun(), never for ordinary knockback. Always <= stunTimer by
    // construction (Stun() sets both from the same resisted duration via Mathf.Max, AddKnockbackVelocity's
    // ApplyStun call never touches this one) — see ApplyStun's own isTrueStun param.
    public float trueStunTimer;
    // Independent of stunTimer above — a slow reduces move speed rather than stopping it outright, so it
    // ticks down on its own timer regardless of whether the enemy is also currently stunned. 1 = full speed
    // (no slow); lower means slower, e.g. 0.5 = half speed — only meaningful while slowTimer > 0 (see
    // EnemyBasePD.EffectiveMoveSpeed/Slow()'s own doc comments for how these combine and merge with a new
    // application).
    public float slowTimer;
    public float slowMultiplier;
    // Bleed status (2026-10-06, ExeBigAxeBleedAugment) — the first ticking damage-over-time status in this
    // codebase. Bucket model, same "single shared timer, not independent per-stack decay" shape Slow above
    // already established for "can this enemy carry multiple sources of one effect at once": every new
    // stack ADDS its own already-fully-resolved per-second rate into bleedDamagePerSecond (never replacing
    // it, so stacks compound without limit) and REFRESHES bleedTimer to the fixed duration via Mathf.Max
    // (never extends/stacks the duration itself, same as slowTimer's own "never shortens what's already
    // active" semantics) — see EnemyBasePD.ApplyBleed's own doc comment for the full mechanism and why a
    // genuinely unbounded number of INDEPENDENTLY-expiring stacks isn't representable in fixed
    // PredictedIdentity State at all. The whole accumulator resets to 0 together once bleedTimer actually
    // expires — no per-stack independent decay, an accepted simplification (same class of tradeoff as
    // RogueSpinAttackSpeedAugment's own hard-capped stacking, just solved the opposite way: uncapped count,
    // simplified decay, instead of capped count with true independent decay).
    public float bleedDamagePerSecond;
    public float bleedTimer;
    // Kill-credit/attacker attribution for bleed tick damage — "most recent stack wins," same accepted
    // "last contributor" limitation as MortarGloomPD.pendingHazardId/ArcherArrowRainAbilityPD.activeChainId
    // — the bucket model merges potentially-different-players' stacks (4-player co-op) into one shared
    // rate with no per-stack attacker tracking, so there's no way to attribute each tick's damage to
    // "whichever player's stack happens to be ticking right now" even in principle.
    public PlayerID bleedAttacker;
    public bool atPreferredRange;
    // Rolled once per spawn (LateAwake, alongside separationCheckTimer/personalPushAngle above) via
    // GameRandom, not re-rolled per strafe — same "decide once, use for the whole lifetime" reasoning those
    // two already use. See HandleMovement's own radial-strafe doc comment for how this is consumed.
    public bool strafeClockwise;
    // Hard gate on HandleMovement — see IEnemyPD.SetMovementDisabled's own doc comment for the full
    // reasoning (why this exists, who's expected to call it, and what it does/doesn't affect).
    public bool movementDisabled;
    // See IEnemyPD.IsInvulnerable's own doc comment.
    public bool invulnerable;
    // See IEnemyPD.SetKinematic's own doc comment — the ON side lives in State (re-asserted every tick in
    // Simulate(), see that call site's own comment); the OFF side is an immediate direct write, never
    // reflected here (SetKinematic(false) clears this too, but by the time it returns the rigidbody has
    // already been un-kinematic'd directly, not waiting for next tick's re-assertion pass).
    public bool externalKinematic;
    // See IEnemyPD.SetPhysicsColliderDisabled's own doc comment — same asymmetric ON-in-State/OFF-immediate
    // shape as externalKinematic, for the same reason.
    public bool physicsColliderDisabled;
    // See IEnemyPD.SetHurtboxDisabled's own doc comment — same asymmetric shape again, same reason.
    public bool hurtboxDisabled;

    // See IEnemyPD.SetDamageOverride's own doc comment — generic, not Orbit-specific. While damageOverridden
    // is true, GetDamage() returns this instead of GetOwnDamage(). damageOverride is meaningless whenever
    // damageOverridden is false (not read, not reset independently — SetDamageOverride(false) leaves it
    // alone since nothing reads a stale value while the flag is off).
    public bool  damageOverridden;
    public float damageOverride;

    // See IEnemyPD.OrbitTarget's own doc comment for the full pull->orbit mechanism this drives.
    public int   orbitPhase; // EnemyOrbitPhase
    public PredictedObjectID orbitOwnerId;
    // See IEnemyPD.SetOrbitFrozen's own doc comment — recomputed fresh every tick by the OWNER's own
    // RefreshHeldDamageOverrides (never a one-shot transition), true only while the owner is genuinely
    // frozen by a true stun mid-Orbit. Read by this enemy's own LateSimulate() (skips the whole position
    // recompute), OnContactTrigger (blocks contact damage), and UpdateView (drives the yellow stun tint) —
    // three independent effects off one shared flag, same "recompute fully every tick, no edge-detection
    // state to track" shape RefreshHeldDamageOverrides itself already uses for damageOverride.
    public bool orbitFrozen;
    // Changes every tick while Orbiting — CompressedFloat for the same reason SpikeShieldPD.facingAngle/
    // CharacterStatsPD.weaponOrbitAngle already use it (a plain float here would pay PurrNet's full
    // "changed" cost every single tick instead of a quantized/compressed one).
    public CompressedFloat orbitAngleDeg;
    public float orbitRadiusTarget;
    // Flat MoveTowards speed (units/sec) for the Pulling phase (2026-09-12, was orbitPullStartPosition +
    // orbitPullDuration + orbitPullElapsed driving a fixed-DURATION Lerp instead — removed entirely, no
    // longer needed: LateSimulate() now MoveTowards-steps straight from this enemy's own live
    // transform.position every tick, so there's no separate "start position" to snapshot or "elapsed"
    // counter to track, just this one caller-assigned constant read every tick like
    // orbitAngularSpeedDegPerSec below). Irrelevant once Orbiting.
    public float orbitPullSpeed;
    public float orbitAngularSpeedDegPerSec;

    // See IEnemyPD.LaunchAlong's own doc comment — independent of orbitPhase above (checked first in
    // LateSimulate(), supersedes orbit positioning for its own duration without touching orbitPhase/
    // IsBeingHeld at all), so a caller composing this with an existing hold (e.g. a boss "shooting" a held
    // enemy at a target) doesn't need to separately re-claim/re-orbit it afterward.
    public bool    launchActive;
    public Vector3 launchDirection;
    public float   launchRemainingDistance;
    public float   launchSpeed;

    public DisposableList<ContactCooldown> contactCooldowns;
    public bool isDashing;
    public Vector3 dashDirection;
    public Vector3 dashDestination;
    public float    dashSpeed;
    // Sibling to isDashing above (2026-08-26) — for StartOffsetChase/HandleOffsetChase, which chases a
    // LIVE, target-relative offset point every tick instead of a destination snapshotted once at start
    // (see those methods' own doc comments for the full "why" — the old dash-to-a-fixed-point shape let a
    // fast player simply outrun it). Reuses dashSpeed above for its own speed; does NOT reuse
    // dashDirection/dashDestination, since this recomputes both fresh every tick from live target position
    // rather than persisting a stale snapshot across ticks.
    public bool  isOffsetChasing;
    public float chaseOffsetDistance;
    // Locked the SAME tick StartOffsetChase is called, never re-derived afterward (2026-08-26 fix — used to
    // be recomputed live every tick from the dasher's own CURRENT position relative to the target, which
    // becomes unstable as the dasher closes distance: two nearby points produce a wildly swinging direction
    // from small movements, collapsing the offset toward the target instead of holding a stable side — this
    // was confirmed live as the exact reason DaggerGloomPD kept rushing the player and getting stuck at/
    // inside them instead of ever reaching its intended far-side landing point). World-space unit direction
    // (not the raw angle — the angle is only ever needed once, at the moment of locking, see
    // StartOffsetChase's own doc comment) — HandleOffsetChase reads this directly every tick and applies it
    // against the target's LIVE position, so the destination still slides along with a moving target; only
    // the SIDE/direction of the offset itself is fixed, never the position it resolves to. 0° locks onto the
    // dasher->target bearing at that instant (continues straight past the target, e.g. DaggerGloomPD, ending
    // up on its far side); any other angle flanks to a side. Vector3.zero is a separate sentinel (2026-09-03,
    // the no-angle StartOffsetChase overload — e.g. SpearGloomPD/KingSpikePD) meaning "no locked side at
    // all, just get within chaseOffsetDistance of the target" — see HandleOffsetChase's own doc comment.
    public Vector3 chaseLockedOffsetDir;
    // Captured once at the start of an "ongoing" attack (dash-to-target, or windup entered directly because
    // already in range) via EnemyBasePD.CaptureAttackAnchor — see that method's own doc comment. Facing
    // during the ongoing window reads THIS, not a live _target, so a mid-attack retarget (taunt/invisible)
    // never redirects an attack that's already committed; it only affects the NEXT attack in a chain.
    public Vector3 attackAnchorPosition;
    // Always exactly one killer — overwritten unconditionally on every TakeDamage call (not just the lethal
    // one), so by the time Die() runs this already reflects whoever landed the actual final blow. Never a
    // list/multiple; there's no scenario in this game where more than one player is credited for one kill.
    public PlayerID killerOwner;
    // Whether the SPECIFIC hit that killed this enemy was a crit — captured at the exact TakeDamage call
    // that reduced health to <= 0 (see that method's own comment), not inferred later, since isCrit is only
    // in scope as that call's own parameter.
    public bool killedByCrit;
    // Same as killedByCrit above, for highroll (2026-09-14, extended to match).
    public bool killedByHighroll;
    public bool initialized;
    // Difficulty-pacing wipe — deliberately separate from isDead, never set alongside it. See
    // IEnemyPD.SilentDespawn's own doc comment for the full reasoning (no XP, no death effects/holds, no
    // red flash — just gone). Reset explicitly on every pooled reuse (Simulate()'s own !b.initialized
    // block), same as isDead, since GetInitialState() alone only covers a genuinely fresh instance.
    public bool wiped;
    // Live, ticking-down countdown — kept separate from EnemyBasePD.EnrageTimer (the configured starting
    // duration, a plain never-mutated field) for the same reason WeaponRangedPD's fireTimer isn't just a
    // plain field: anything that changes every tick during Simulate() has to live in predicted State to
    // resimulate correctly, a plain field mutated in-place wouldn't roll back and replay consistently.
    public float enrageCountdown;
    public bool  enraged;
    // Boss-only escalating Move Speed stacking (see EnemyBasePD.EnrageMoveSpeedStackPercent's own comment
    // for the full design reasoning) — how many stacks earned so far, and the countdown to the next one.
    // Both stay permanently 0 for every non-Boss enemy (never touched outside the spawnCategory ==
    // EnemySpawnCategory.Boss guard in Simulate()), so this is a total no-op elsewhere.
    public int   enrageMoveSpeedStacks;
    public float enrageMoveSpeedStackTimer;
    // Boss-only escalating Damage stacking (2026-08-19) — exact parallel to enrageMoveSpeedStacks/Timer
    // above (same shape, same cadence source), see EnemyBasePD.EnrageDamageStackPercent's own comment for
    // the full design reasoning. Both stay permanently 0 for every non-Boss enemy, same no-op guarantee.
    public int   enrageDamageStacks;
    public float enrageDamageStackTimer;

    // Generic "don't actually hide/delete yet" reference count — see EnemyBasePD.RequestDeathHold's own doc
    // comment. Independent scripts each request/release their own hold; hide+delete both wait until this
    // reaches 0 regardless of how many scripts are currently holding.
    public int deathHoldCount;

    // On-kill launch effect (e.g. OnKillLaunchEnemiesAugment) — generic on EnemyBasePD itself (not the
    // augment) since ANY enemy type can be the one killed, and the augment has no per-tick relationship
    // with the corpse once it fires StartKillLaunch(). See EnemyBasePD.HandleKillLaunch's own doc comment.
    public bool    killLaunchActive;
    public Vector3 killLaunchDirection;
    public float   killLaunchSpeed;
    public float   killLaunchRemainingDistance;
    public float   killLaunchDamage;
    // Locked in once at kill-time (same reasoning as PhantomBladePD: whether this hit crits is a property
    // of the whole launch, not re-rolled per enemy it passes through) and carried through to TakeDamage +
    // each hit's queued damage-number feedback — see HandleKillLaunch's own comment.
    public bool    killLaunchIsCrit;
    public bool    killLaunchIsHighroll;
    // Which enemies this launch has already damaged — travelling through a cluster shouldn't repeatedly
    // hit the same target every tick it stays overlapped.
    public DisposableList<PredictedObjectID> killLaunchHitIds;

    // Declumping — see EnemyBasePD.HandleSeparation's own doc comment for the full mechanism. Ticks, not
    // seconds (same reasoning as every other per-tick countdown in this codebase — cheap zigzag-varint diff
    // vs a float's flat 33-bit cost). Seeded with a random initial offset in LateAwake() so a whole pack
    // spawned at once doesn't all recompute on the same tick.
    public int separationCheckTimer;
    // Fixed per-instance fallback push direction (degrees) for the exact-same-coordinate case, where "push
    // away from the other guy" is mathematically undefined — see HandleSeparation's own comment. Randomized
    // once per spawn/reuse, not derived live, so it stays stable across this instance's whole life rather
    // than changing every time the check happens to fire.
    public float personalPushAngle;
    // In State, not a plain field — a rollback restores separationCheckTimer/personalPushAngle above
    // correctly (they're right here), but a plain field caching the LAST computed push vector would sit
    // stale (whatever the pre-rollback local prediction last computed) for up to SeparationCheckInterval
    // ticks after any correction, instead of being exactly reproducible from replayed state like every other
    // physics-affecting value in this struct. See feedback_ability_temp_buff_state_safety memory for the
    // same class of bug found elsewhere in the v0.3 audit this was raised alongside.
    public Vector3 separationPush;

    // Runtime-scaled combat stats — written by ConfigureSpawn (and, for scaledMoveSpeed, also
    // KingSpikePD's own Moving/ChargeWindup speed-boost logic) from the plain authored maxHealth/moveSpeed/
    // attackSpeed/damage fields below, which stay untouched read-only design-time baselines forever now
    // (never multiplied in place). These 4 live in State specifically because a plain field mutated outside
    // Simulate()/State only ever takes effect on whichever single machine actually ran that mutation — any
    // enemy instance created on another machine via PredictedHierarchy reconciling a misprediction (rather
    // than that machine's own local spawner call) would silently keep the prefab's unscaled defaults
    // forever otherwise. Being in State means normal PurrDiction State replication/rollback-correction
    // keeps every machine's copy correct regardless of which machine actually computed it — see
    // project_spikeshield_instance_id_collision_bug memory for a confirmed real-world case of exactly this
    // kind of reconciliation happening. MaxHealth/EffectiveMoveSpeed/EffectiveAttackSpeed/GetOwnDamage all
    // read these instead of the plain fields now (2026-09-22).
    public float scaledMaxHealth;
    public float scaledMoveSpeed;
    public float scaledAttackSpeed;
    public float scaledDamage;
    // Same reasoning as the 4 above (2026-09-22 follow-up pass) — xpValue/pointsValue are read directly at
    // the moment of death (Die()/the verified-death block), a plain field would silently stay unscaled on
    // any machine that never itself ran ConfigureSpawn for this instance.
    public float scaledXpValue;
    public float scaledPointsValue;
    // Which category/tier this enemy was spawned as — live in State, not a plain field (2026-09-22 follow-
    // up), for the same reconciliation reason as the fields above: ConfigureSpawn() only ever runs on
    // whichever machine actually made the spawn decision, so on a machine where this enemy was instead
    // created by PredictedHierarchy reconciling a misprediction, a plain field would silently stay at its
    // default (Normal/tier 1) forever. This one has a wider, more serious blast radius than the stat fields
    // above: EnrageMoveSpeedMultiplier/EnrageDamageMultiplier and the enrage-timer tick in Simulate() are
    // ALL gated on spawnCategory == Boss, so a reconciled boss would silently never enrage at all on that
    // machine — confirmed while investigating a user report that tier coloring wasn't replicating either
    // (ApplyTierMaterials/ApplyTierNamePrefix have the identical gap, see UpdateView's own re-apply fix).
    // Stored as int, cast at each read site — same convention orbitPhase/stateId-style fields already use
    // for enums in this struct.
    public int spawnCategoryRaw;
    public int spawnTier;

    // Lets the packer duplicate this struct via a direct field-copy during snapshot/history/rollback
    // operations instead of falling back to a full serialize/deserialize round-trip — see
    // purrnet.dev/docs/client-side-prediction/disposable-collections. With potentially 50-100+ enemies
    // alive at once, every one of them getting the slow fallback path every tick is a real, compounding CPU
    // cost; this is a plain value-copy (`this`) with just the two DisposableList fields replaced by their
    // own .Duplicate() — struct-copying a DisposableList instead of calling .Duplicate() on it would alias
    // the SAME pooled underlying list between the original and the "copy" (the documented anti-pattern),
    // not create an independent one.
    public EnemyBaseState Duplicate()
    {
        var copy = this;
        copy.contactCooldowns = contactCooldowns.Duplicate();
        copy.killLaunchHitIds = killLaunchHitIds.Duplicate();
        return copy;
    }
}

[RequireComponent(typeof(PredictedRigidbody))]
public abstract class EnemyBasePD<TState> : PredictedIdentity<TState>, IEnemyPD
    where TState : struct, IPredictedData<TState>
{
    [SerializeField] protected PredictedRigidbody _rigidbody;
    protected Collider _hurtbox;
    protected Collider _physicsCollider;
    protected Collider _contactDamageCollider;
    protected Animator _animator;
    private PredictedTransform _predictedTransform;

    // The smoothly render-frame-interpolated "Visuals" child PredictedTransform continuously tracks toward
    // this enemy's tick-accurate physics root (see KingSpikePD's own comments on chargeWindupIndicator for
    // the same reasoning) — falls back to the raw root transform if this enemy has no PredictedTransform,
    // rather than anything that visually follows this enemy jittering between discrete simulation ticks.
    public Transform Visuals => _predictedTransform != null && _predictedTransform.graphics != null
        ? _predictedTransform.graphics
        : transform;

    // Incremented once per real pooled reuse/first spawn (see the !b.initialized block in Simulate()) —
    // see IEnemyPD.SpawnGeneration's own doc comment for why this exists.
    private int _spawnGeneration;
    public int SpawnGeneration => _spawnGeneration;
    // See IEnemyPD.HasInitialized's own doc comment.
    public bool HasInitialized => B.initialized;

    [Header("Identity")]
    public string enemyName = "Enemy";

    [Header("Stats")]
    public float moveSpeed = 10f;
    [NonSerialized] public float baseMoveSpeed;
    // Does nothing on its own here — EnemyBasePD has no generic attack-cooldown system (every subclass owns
    // its own attack timing/state machine entirely independently), so a base
    // enemy never reads this at all. A subclass that wants attack speed to matter reads EffectiveAttackSpeed
    // itself wherever it currently uses a flat attack-cooldown/windup duration (e.g. divide by it, same shape
    // as any other rate stat), and how exactly attackSpeed shapes that cadence is left entirely up to it.
    // Hidden from the Inspector (still public + serialized, so every prefab's currently-tuned value is
    // preserved) — same "doesn't read as a real per-prefab setting" reasoning isStunnable's own [HideInInspector]
    // already uses just below: it's slow-interaction infrastructure (see EffectiveAttackSpeed's own doc
    // comment), not something meant to be hand-tuned per enemy in the Inspector.
    [HideInInspector] public float attackSpeed = 1f;
    public float maxHealth = 75f;
    [NonSerialized] public float baseHealth;
    public float preferredRange = 0f;
    public float rangeToleranceFar = 0.3f;
    public float rangeToleranceClose = 0.3f;
    // true (default): an enemy settled at preferred range radially strafes instead of standing fully still —
    // see HandleMovement's own doc comment. false: reverts to the pre-2026-09-30 "stand still" behavior,
    // per-prefab opt-out for enemies that should still hold position (e.g. a turret-like enemy where strafing
    // would look wrong).
    public bool canStrafe = true;
    public float damage = 10f;
    [NonSerialized] public float baseDamage;
    public float xpValue = 1f;
    [NonSerialized] public float baseXpValue;
    // Upgrade points this enemy is worth, separate from xpValue (2026-08-29 — was AwardUpgradePoints(xpValue),
    // a straight 1:1 with XP) — its own tier-scaling growth rate (EnemySpawnerPD.pointsTierGrowth) is
    // independently tunable from XP's own (xpTierGrowth), even though both happen to start from a similar
    // author-tuned base value at Tier 1. Default 5 (xpValue's own default is 1) — deliberately NOT matched to
    // xpValue's default, per explicit design ("the base points amount and enemy XP are coincidentally exactly
    // the same [per author-tuned prefab], but every tier beyond T1 they diverge"). Mirrors xpValue/baseXpValue's
    // entire lifecycle (Awake snapshot, LateAwake pooled-reuse reset, ConfigureSpawn tier scaling) exactly —
    // see each of those touch points' own doc comments for why that shape exists.
    public float pointsValue = 5f;
    [NonSerialized] public float basePointsValue;
    // Generic per-enemy size scalar — not tied to any one visual/physics system, just a shared number
    // other generic code (e.g. HandleKillLaunch's sphere-cast radius, IEnemyPD.Size for
    // DamageNumberManager's own spawn-height scaling) can read without needing its own separate per-enemy
    // tunable. Default 1, hand-tuned per enemy prefab.
    public float size = 1f;
    // IEnemyPD.Size — trivial passthrough, see the interface member's own doc comment.
    public float Size => size;

    // ── Declumping ────────────────────────────────────────────────────────────
    // With enemy-enemy collision deliberately disabled project-wide for performance, nothing was ever
    // pushing overlapping enemies apart — herding a pack onto one player is a totally valid, intended
    // strategy, but with nothing to separate them they can end up literally stacked at the exact same
    // coordinate, which looks wrong and makes them hard to read as individuals. Applies to every enemy type
    // uniformly (started as a SpikeGloomPD-only fix, generalized here once proven cheap and unobtrusive) —
    // none of these three are Inspector-tunable, deliberately: this is a shared correction, not a per-prefab
    // authoring choice. separationRadius scales with size (a bigger enemy needs a bigger personal-space
    // radius); separationForce deliberately does NOT (a harder shove isn't obviously "more correct" for a
    // bigger enemy the way a bigger detection radius is, and a flat force keeps the correction subtle
    // regardless of size).
    // Briefly tried 5 (~1.5x tick rate) on 2026-08-21 to catch residual TL4 clumping, then dialed back to 8
    // the same day — a higher tick RATE has an ongoing perf cost every enemy pays every tick regardless of
    // whether it's actually clumped. SeparationForce got the same "try to fully fix it" treatment instead
    // (see that field's own comment) and, after live testing, also landed back on its original value at the
    // time. Neither knob was ever going to fix that residual clumping — it wasn't a tuning problem at all,
    // it was targetless idle enemies applying the separation force with nothing counteracting it (fixed
    // 2026-09-01, see SeparationRadiusPerSize's own doc comment for the full postmortem). Left at 8 — no
    // reason to revisit purely for that symptom now that its actual cause is gone.
    private const int   SeparationCheckInterval = 8;
    // Reverted to 1f (2026-08-12), doubled to 2f (2026-08-15), reverted back to 1f again (2026-08-21, stated
    // reason at the time: "the 2f radius turned out to space enemies out more than actually wanted"). That
    // stated reason was WRONG, confirmed 2026-09-01 — the radius was never actually the problem. Two real,
    // separate bugs were compounding at the time and got misdiagnosed as "the radius itself":
    //   1. SeparationForce wasn't yet scaled by EffectiveMoveSpeed (see that field's own doc comment) — a
    //      flat force only stayed balanced against HandleMovement's own speed-proportional pull at exactly
    //      one baseline speed, so declumping visibly weakened at every other speed tier.
    //   2. HandleSeparation applied the push force to every enemy unconditionally, even ones with no valid
    //      target (every player invisible-and-not-taunting, or dead) — HandleMovement no-ops with nothing to
    //      chase, so an idle enemy had nothing counteracting the separation force, reading as random sliding-
    //      in-place rather than pack jostling. This is what a Rogue Vanish test would have actually shown
    //      back then — not the radius "spacing enemies out too much", but idle enemies visibly drifting with
    //      no target. See HandleSeparation's own _target == null exemption for the fix.
    // With both of those actually fixed first, doubling back to 2f here (2026-09-01) landed correctly on the
    // first try — confirmed live, keeping it. If this ever needs retuning again, rule out confounds in
    // OTHER systems (force scaling, targetless idling, anything else that changed alongside a knob like this
    // one) before blaming the radius itself again — that's exactly the mistake this whole history is a
    // record of.
    private const float SeparationRadiusPerSize = 2f;
    // Used instead of SeparationRadiusPerSize above whenever an alive player is within this enemy's own
    // near-player DETECTION range (NearPlayerDetectionRadiusPerSize below) — see RecomputeSeparationPush's
    // own doc comment for why. Deliberately much smaller than the normal radius: multiple enemies crowding a
    // player need to still be "allowed" to touch them, this only stops them overlapping too hard once that
    // close, rather than pushing them away from the player entirely.
    private const float SeparationRadiusPerSizeNearPlayer = 0.5f;
    // Independent from SeparationRadiusPerSize above (2026-09-01, split out) — "how far away can a player be
    // for this enemy to still get the crowd-friendly near-player treatment" used to just reuse whatever
    // SeparationRadiusPerSize's own value was, which meant retuning the general declump radius silently
    // moved this trigger distance too (doubling SeparationRadiusPerSize also doubled how far away a player
    // could be and still count as "nearby" for this purpose — an unintended side effect on crowd-around-
    // player behavior nobody actually wanted changed). Fixed at 1f — SeparationRadiusPerSize's own value
    // before this round's retune — so this stays exactly as it already behaved, decoupled from any future
    // general-radius tuning.
    private const float NearPlayerDetectionRadiusPerSize = 1f;
    // Round-tripped back to its original 8f (2026-08-21) after a same-day detour: briefly doubled to 16f
    // (when SeparationRadiusPerSize was still 2f, to cover that radius's bigger distance), then to 32f after
    // SeparationRadiusPerSize reverted to 1f but TL4 testing (Rogue's Vanish — go invisible, watch enemies
    // keep sliding into each other afterward) still showed residual clumping. Live-tested progressively
    // higher from there (up to 320f) and it got WORSE each step, not better — at 320f enemies visibly
    // bounced off each other. Landed back on the original 8f at the time as the best AVAILABLE result, with
    // the remaining post-Vanish sliding logged as "accepted as-is, not a bug to keep chasing."
    //
    // That sliding WAS a real bug after all, later actually found and fixed (2026-09-01, see
    // SeparationRadiusPerSize's own doc comment for the full postmortem) — HandleSeparation was applying the
    // push force to targetless idle enemies with nothing to counteract it. Cranking SeparationForce itself
    // higher was never going to fix that (it was making an idle enemy's own uncontested force STRONGER, the
    // opposite of the fix), which is exactly why higher values only ever made things visibly worse here. Left
    // at 8f — no reason to revisit this value now that the actual cause is gone — but noting this so a future
    // pass doesn't reach for this knob again for the same already-solved symptom. See
    // SeparationCheckInterval's own comment for why a higher tick RATE was also tried and reverted separately.
    private const float SeparationForce = 8f;
    // Baseline EffectiveMoveSpeed this SeparationForce was tuned against (2026-08-26) — HandleMovement's own
    // pull-toward-target force scales directly with EffectiveMoveSpeed (dir * EffectiveMoveSpeed * delta *
    // 5f), while SeparationForce above was a flat constant, so the two forces (both applied as
    // AddForce(..., ForceMode.VelocityChange) every tick — separation and movement are directly competing
    // accelerations on the same rigidbody) only stayed balanced at exactly this speed. A faster enemy's own
    // movement force grew proportionally while separation stayed pinned, so declumping visibly weakened the
    // faster an enemy got — confirmed live (user: "it naturally is less effective as enemies gain more
    // movespeed"). RecomputeSeparationPush now scales SeparationForce by (EffectiveMoveSpeed /
    // SeparationForceBaselineMoveSpeed) to keep the ratio between the two forces constant regardless of speed
    // tier or an active slow (EffectiveMoveSpeed is already slow-adjusted, same value HandleMovement itself
    // reads, so this stays correctly proportional while slowed too) — deliberately the flat, shared baseline
    // of 10 (not each enemy's own individually-authored baseMoveSpeed/moveSpeed), per explicit design: a
    // handful of enemies run slightly faster than 10 by default, and that's fine to ignore here.
    private const float SeparationForceBaselineMoveSpeed = 10f;
    // Cached between checks (in EnemyBaseState.separationPush, not a plain field — see that field's own doc
    // comment) and re-applied as a small continuous force every tick in between — smoother-looking than an
    // abrupt kick only on the tick the check itself runs, and self-decays to zero once neighbors are far
    // enough apart to stop overlapping on the next check.
    private readonly Collider[] _separationOverlapResults = new Collider[16];

    [Header("Contact Damage")]
    public bool contactColliderEnabled = true;
    // Identical across every enemy prefab — never tuned per-enemy, so it's a constant rather than an
    // Inspector field. Named ContactCooldownDuration (not ContactCooldown) to avoid colliding with the
    // ContactCooldown struct type used by B.contactCooldowns below.
    private const float ContactCooldownDuration = 0.5f;
    public bool stunResistant = false;
    // Reduces knockback velocity (AddKnockbackVelocity) AND reduces the duration of any other stun source
    // (Stun()) — except the stun knockback itself produces, which is deliberately exempt since that duration
    // is already derived from the already-reduced knockback velocity; applying this resist a second time on
    // top of that would double-dip the same resistance against the same hit. Slow() is the one exception to
    // the "reduces duration" rule — a slow's duration always lands in full; this instead reduces the slow's
    // POTENCY (how much speed it actually takes away), see Slow()'s own doc comment for the exact math.
    [Range(0f, 1f)] public float crowdControlResist = 0f;
    // Flat duration granted by InterruptTrueStun() below whenever an interrupt triggers FirstSuccessfulInterrupt
    // (FSI) — see that method's own doc comment. Deliberately NOT scaled by crowdControlResist (a "true TRUE
    // stun" — even a heavily CC-resistant enemy still eats the full flat duration), only stunResistant/death
    // still block it entirely, same as every other stun channel.
    // Identical across every enemy prefab — never tuned per-enemy, so it's a constant rather than an
    // Inspector field. 2 -> 5 (2026-09-30).
    private const float InterruptTrueStunDuration = 5f;
    public bool showDamageNumbers = true;
    // sizeYOffset/sizeZOffset (per-enemy manual damage-number spawn nudge) removed (2026-08-19) —
    // DamageNumberManager.Spawn now scales its own spawn height automatically off IEnemyPD.Size instead
    // (see that field's own doc comment), so a per-prefab manual Y tweak is no longer needed; the Z nudge
    // was a workaround for damage numbers rendering behind a model, which is moot now that numbers render
    // through the UI camera and are always drawn in front regardless of world geometry.

    // Set-in-stone across every Boss prefab — never tuned per-enemy, so these are constants rather than
    // Inspector fields. Fully generic (2026-08-20 — reworked away from per-boss custom behaviors, King
    // Spike's never-ending remote burst sequence and King Orbiter's doubled ring counts were both removed in
    // favor of this single shared mechanic, see each file's own history/comments): a stalled boss fight
    // (players kiting/avoiding combat entirely instead of engaging) was otherwise "beatable" forever, since a
    // per-boss custom enrage effect only actually threatened a player who was still in the fight. This
    // generic Move Speed + Damage escalation instead makes simply outrunning/kiting the boss (or just
    // tanking it passively) increasingly costly the longer the fight drags on, regardless of whether the
    // player is otherwise engaging at all. Gated entirely on spawnCategory == EnemySpawnCategory.Boss (see
    // Simulate()'s trigger check and EnrageProgress below) — every non-Boss enemy is unaffected.
    private const float EnrageTimer = 120f;

    // One-time bursts that land the INSTANT enrage triggers, on top of (not instead of) the ongoing per-
    // interval stacking below — e.g. 50% means the boss is immediately 1.5x as fast/hard-hitting the
    // moment EnrageTimer expires, then keeps climbing further via the ongoing stacks after that.
    private const float EnrageInitialMoveSpeedPercent = 0.5f;
    private const float EnrageInitialDamagePercent    = 0.5f;

    // Boss-only (spawnCategory == EnemySpawnCategory.Boss gate, see Simulate()'s own check) ongoing
    // escalation on top of the one-time initial burst above — an ever-escalating, UNCAPPED Move Speed stack
    // so a stalled fight keeps getting worse forever, with no ceiling. Additive per stack (1f + stacks *
    // this, not compounding) per explicit design — composes automatically into EffectiveMoveSpeed, so any
    // future subclass's own EffectiveMoveSpeed override would pick this up for free via
    // base.EffectiveMoveSpeed without needing its own code.
    private const float EnrageMoveSpeedStackPercent   = 0.1f;
    private const float EnrageMoveSpeedStackInterval  = 10f;
    // Boss-only escalating Damage stacking — parallel to EnrageMoveSpeedStackPercent/Interval above
    // (identical shape: ticks every EnrageDamageStackInterval seconds, forever uncapped — see the Move Speed
    // field's own comment for the full "why no ceiling" reasoning, same logic applies here). Added because
    // Move Speed alone still wasn't closing fights out decisively enough against healing/high-health builds
    // — a stalled fight now also hits harder over time, not just faster. Additive per stack (1f + stacks *
    // this, not compounding), composed via EnrageDamageMultiplier below — unlike EnrageMoveSpeedMultiplier
    // (which composes automatically into EffectiveMoveSpeed for every caller), this only composes
    // automatically into the base GetOwnDamage() — a subclass that computes its own damage separately from
    // that hook (KingSpikePD's SpikeBurst/remote burst, KingOrbiterPD's held-pleb damage overrides) needs
    // to multiply by EnrageDamageMultiplier explicitly at its own call site too.
    private const float EnrageDamageStackPercent  = 0.1f;
    private const float EnrageDamageStackInterval = 10f;

    protected Transform _target;
    // Mirrors _target — cached alongside it purely so Simulate() can cheaply check "is my current target
    // still alive" every tick (a null/bool check) without re-resolving a CharacterStatsPD from the
    // Transform. Always set together with _target in FindClosestTarget(); never assigned anywhere else.
    // Protected (not private) — subclasses with multi-stage attacks (Spear/Leaper/Dagger/Shooter/KingSpike/
    // Blitz) read this directly at their between-stage transitions to check IsInvisible before deciding
    // whether to reprioritize early.
    protected IFriendlyPD _targetStats;

    // Frozen-target chase reference for StartOffsetChase/HandleOffsetChase (2026-08-31) — captured once when
    // the chase starts, deliberately decoupled from the shared _target/_targetStats pair above, which taunt/
    // invisible retargeting (RetargetAwayFrom) is free to null or reassign at any moment, mid-dash included
    // (see RetargetAwayFrom's own doc comment — it assumes every ongoing attack reads its own frozen
    // reference instead of live _target, which used to be true everywhere EXCEPT this offset-chase). Without
    // this, RetargetAwayFrom nulling _target the instant a kited target went invisible made
    // HandleOffsetChase's own null-guard trip immediately, aborting the dash wherever the enemy happened to
    // be instead of coasting to the target's last known position — confirmed live: Spear/Dagger visibly
    // stopped short and attacked mid-air the instant a chased target went invisible. Same "commit once, keep
    // reading that same reference" contract every other windup-tracking mechanism in this codebase already
    // uses (KingSpikePD._chargeWindupTrackedTarget, BlitzGloomPD/ShooterGloomPD's own _windupTrackedTarget).
    private IFriendlyPD _offsetChaseTrackedTarget;
    // Companion to _offsetChaseTrackedTarget — refreshed every tick it's actually alive+visible (same shape
    // as ShooterGloomPD._windupTrackedTargetLastKnownPosition), frozen the instant it isn't. HandleOffsetChase
    // chases THIS, not a live _offsetChaseTrackedTarget.transform.position read.
    private Vector3 _offsetChaseTrackedLastKnownPosition;

    // Passive record only, no decision-making here (2026-08-22) — mirrors _targetStats.transform.position
    // every tick _targetStats is non-null, decoupled from whatever governs _target/_targetStats itself
    // (FindClosestTarget/FindRandomTarget, already invisibility-aware). Lets a subclass substitute this in
    // for "no valid target right now" scenarios instead of the whole mechanic just fizzling out (see
    // KingOrbiterPD's own ShootFlight usage) — this field alone doesn't change any existing behavior.
    // Defaults to Vector3.zero (world origin) on spawn/pooled reuse, same as any other unset Vector3 field —
    // see hasLastKnownTargetPosition below for the companion flag that distinguishes "genuinely tracked
    // someone at world origin" (real, if rare) from "never actually tracked anyone yet" (the degenerate case
    // a naive zero-check can't tell apart on its own).
    protected Vector3 lastKnownTargetPosition;
    // Sentinel companion to lastKnownTargetPosition above (2026-09-02) — false until the very first real
    // assignment below, never reset back to false except on a fresh spawn/pooled reuse. A consumer that
    // falls back to lastKnownTargetPosition when it has no live target of its own (e.g. KingSpikePD's own
    // post-charge reposition dash) should check THIS first — dashing to/aiming at a position that was never
    // actually anyone's real position (still sitting at its Vector3.zero default) reads as this enemy
    // suddenly beelining for the world origin, confirmed live as a real, if rare, bug: possible whenever an
    // enemy has never once had a valid target since spawning (e.g. every player invisible-and-not-taunting or
    // dead for this enemy's entire life so far).
    protected bool hasLastKnownTargetPosition;

    // Public for cross-script reads (AddKnockbackVelocity, Stun()), not because it's meant to be hand-
    // tuned — it's fully derived from stunResistant at Awake() and, for state-driven enemies, reassigned
    // every tick from current attack state. Hidden so it doesn't read as a real Inspector setting
    // alongside stunResistant, which actually is one.
    [HideInInspector] public bool isStunnable = true;
    public bool IsStunned => B.stunTimer > 0f;
    // True only for a real Stun() call, never AddKnockbackVelocity's own derived micro-stun (see
    // EnemyBaseState.trueStunTimer's own doc comment). A true stun always lands even mid-attack-sequence
    // without interrupting whatever's already committed (see ApplyStun/Simulate()'s own comments) — a
    // subclass with a multi-attack chain (e.g. BlitzGloomPD's blitzesRemaining, SpearGloomPD's
    // spearAttacksRemaining) reads this at its own "continue chaining into another attack, or stop"
    // checkpoint, so a true stun landing mid-sequence still lets the CURRENT attack action finish, but
    // skips chaining into the next one and falls through to its terminal/post-attack state instead — which
    // is what flips isStunnable back to true and lets Simulate()'s freeze actually take hold afterward.
    public bool IsTrueStunned => B.trueStunTimer > 0f;
    public bool IsSlowed => B.slowTimer > 0f;
    // Shared "should a multi-attack chain skip continuing into another attack" signal — HARD crowd control
    // only (2026-08-19 correction: previously also included IsSlowed, which was a misunderstanding). This
    // codebase's full CC taxonomy: HARD CC = Stun + Interrupt (immobilizing/action-cancelling — Interrupt
    // doesn't route through this property at all though, since it acts immediately via each enemy's own
    // OnInterrupted() rather than waiting for a chain-continuation checkpoint); SOFT CC = Knockback + Slow
    // (non-immobilizing, target stays mobile/active just slower or displaced) — neither was ever meant to
    // cancel/abort a committed multi-hit chain the way hard CC does, and Knockback's own derived micro-stun
    // was already excluded from IsTrueStunned for this same reason (see its own doc comment). Currently just
    // an alias for IsTrueStunned (see its own doc comment for the full "lands immediately, never interrupts
    // what's already committed, but stops chaining into the next hit" reasoning) — kept as its own named
    // property rather than having every call site read IsTrueStunned directly, so a FUTURE hard-CC type can
    // be added here in one place without touching every subclass's own chain-continuation checkpoint.
    public bool IsCrowdControlled => IsTrueStunned;
    // moveSpeed scaled down by whatever slow is currently active (1f = no active slow) — the only thing
    // HandleMovement's own AddForce calls should read instead of the raw moveSpeed field, so a slow actually
    // affects ordinary chase movement. Dash-style burst movement (BlitzGloomPD/DaggerGloomPD/
    // MortarGloomPD/ShooterGloomPD/SpearGloomPD's own dashSpeedMultiplier, KingSpikePD's ChargeSpeed) now
    // reads THIS too (2026-08-13 — used to bypass it entirely, reading baseMoveSpeed directly), baked in
    // once at dash start rather than live like HandleMovement's own per-tick read — see
    // StartDashToPoint/StartOffsetChase's own doc comments for why: EnemySpawnerPD's tier scaling already
    // multiplies baseMoveSpeed and moveSpeed in lockstep, but Boss enrage stacking ONLY ever lands on
    // moveSpeed (via this property), so a dash reading baseMoveSpeed directly never got any faster no
    // matter how enraged/stacked the boss got — confirmed live on King Spike's Charge, its actual threat.
    // virtual so a subclass can layer its own situational multiplier on top (e.g. OrbiterGloomPD's own move-speed
    // boost while holding orbited enemies, KingOrbiterPD's own Orbit-phase multiplier) via
    // base.EffectiveMoveSpeed * ..., without duplicating HandleMovement's own force/rotation code or
    // mutating the plain moveSpeed field directly every phase transition. Any such subclass override
    // automatically picks up EnrageMoveSpeedMultiplier below for free, since it's baked into the base call.
    public virtual float EffectiveMoveSpeed => B.scaledMoveSpeed * (B.slowTimer > 0f ? B.slowMultiplier : 1f) * EnrageMoveSpeedMultiplier;
    // 1f (no bonus) for every non-Boss enemy — B.enrageMoveSpeedStacks only ever increments under the
    // spawnCategory == EnemySpawnCategory.Boss gate in Simulate(). The b.enraged term is the one-time
    // "Initial" burst (EnrageInitialMoveSpeedPercent) that lands the instant enrage triggers, on top of
    // which the ongoing per-interval stacks keep climbing — see EnrageMoveSpeedStackPercent's own field
    // comment for the full design.
    public float EnrageMoveSpeedMultiplier => 1f + (B.enraged && spawnCategory == EnemySpawnCategory.Boss ? EnrageInitialMoveSpeedPercent : 0f) + B.enrageMoveSpeedStacks * EnrageMoveSpeedStackPercent;
    // 1f (no bonus) for every non-Boss enemy — B.enrageDamageStacks only ever increments under the
    // spawnCategory == EnemySpawnCategory.Boss gate in Simulate(). Same one-time "Initial" burst term as
    // EnrageMoveSpeedMultiplier above (EnrageInitialDamagePercent). See EnrageDamageStackPercent's own field
    // comment for the full design, including which call sites need to multiply by this explicitly (it isn't
    // universally automatic the way EnrageMoveSpeedMultiplier is).
    public float EnrageDamageMultiplier => 1f + (B.enraged && spawnCategory == EnemySpawnCategory.Boss ? EnrageInitialDamagePercent : 0f) + B.enrageDamageStacks * EnrageDamageStackPercent;
    // Same slow-multiply shape as EffectiveMoveSpeed — every slow now doubles as an attack-speed slow (per
    // the user's own call: one shared multiplier drives both), so a subclass just swaps its own flat
    // attackSpeed read for this one wherever it paces its attacks and gets the slow interaction for free.
    // No class-wide "immune" flag exists here on purpose — a phase that should ignore attack speed/slow
    // (e.g. KingSpikePD's SpikeBurstWindup) achieves that simply by never reading this property for that
    // phase's own timer, same as every other CC exception in this codebase (isStunnable toggling,
    // CanBeInterrupted overrides) is a concrete, situated decision rather than a cross-cutting bool.
    public float EffectiveAttackSpeed => B.scaledAttackSpeed * (B.slowTimer > 0f ? B.slowMultiplier : 1f);
    public bool _isDead => B.isDead;
    public bool WasKilledByCrit => B.killedByCrit;
    public bool WasKilledByHighroll => B.killedByHighroll;

    public static int ActiveCount = 0;
    public static int ActiveNormalCount = 0;
    public static int ActiveEliteCount = 0;
    public static int ActivePlebCount = 0;
    public static int ActiveBossCount = 0;
    // Which spawn-cap bucket this instance counts against — set by whichever spawner created it via
    // ConfigureSpawn(), not authored per-prefab (a single prefab can be spawned as different categories
    // by different spawn calls, e.g. the same enemy used as both a tier's normal and elite variant).
    // Backed by State (2026-09-22, was [NonSerialized] plain field) — see EnemyBaseState.spawnCategoryRaw's
    // own doc comment for why (boss enrage silently never activating on a reconciled client).
    public EnemySpawnCategory spawnCategory
    {
        get => (EnemySpawnCategory)B.spawnCategoryRaw;
        private set => B.spawnCategoryRaw = (int)value;
    }
    // The tier NUMBER this enemy was spawned at (1, 2, 3, ...), set alongside spawnCategory by
    // ConfigureSpawn() — read by Die()'s own XP orb spawn (XPOrbManagerPD.SpawnOrbs) to pick the right
    // tier's orb prefab. Deliberately captured at spawn time rather than re-derived from the spawner's
    // CURRENT tier at death time — this specific enemy could still be alive well after the spawner has
    // advanced past the tier it actually spawned in. Backed by State (2026-09-22, was [NonSerialized] plain
    // field) — same reasoning as spawnCategory above.
    public int spawnTier
    {
        get => B.spawnTier;
        private set => B.spawnTier = value;
    }

    // Threat Level 5 ("enemies attack 50% more often", nerfed 2026-09-01 from "twice as often") — set by
    // ConfigureSpawn() from EnemySpawnerPD's own threatLevel5AttackCooldownMultiplier (0.75f once difficulty
    // >= 5, else 1f). A subclass with its own attack-cooldown field multiplies it by this, once, in its own
    // OnConfigureSpawn() override — see ConfigureSpawn()'s own doc comment on the interface for why this
    // can't just live generically here.
    [NonSerialized] public float attackCooldownMultiplier = 1f;

    // Static fields never reset themselves — there's no domain reload in a built game (that's an Editor-
    // only Play Mode concept), and a player going Lobby -> Start -> Lobby -> Start within one launch is a
    // scene load, not a process restart. Worse, enemies still alive when a session ends get torn down by
    // scene unload rather than their own Die(), so the normal decrement never runs for them. Called from
    // every point a fresh game session could begin or a stale one could end — see EnemySpawnerPD.Awake(),
    // GameManagerPD.UpdateView() (game-start and game-end transitions), ReturnToLobbyManager (both the
    // server and client leave-game paths), and CharacterRegistry.OnApplicationQuit() — deliberately
    // redundant across all of them rather than trusting any single exit path to always fire cleanly.
    public static void ResetActiveCounts()
    {
        ActiveCount = 0;
        ActiveNormalCount = 0;
        ActiveEliteCount = 0;
        ActivePlebCount = 0;
        ActiveBossCount = 0;
    }

    // Reworked (2026-08-10) — no longer coupled to the Orbit system directly (see SetDamageOverride's own
    // doc comment for why: a caller-driven override is more generic/reusable than GetDamage() itself reaching
    // into B.orbitOwnerId). NOT virtual — this is the single, uniform gate every subclass's own damage
    // computation passes through before ever reaching a caller; subclasses hook GetOwnDamage() below instead,
    // which guarantees ANY current or future subclass's own damage logic automatically composes correctly
    // with an active override, without that subclass needing to remember to re-add its own check.
    public float GetDamage() => B.damageOverridden ? B.damageOverride : GetOwnDamage();

    // Override point for subclass-specific damage computation (e.g. KingSpikePD's own Charging multiplier)
    // — see GetDamage()'s own doc comment for why this exists as a separate method instead of just
    // overriding GetDamage() directly. EnrageDamageMultiplier (2026-08-19, Boss-only, 1f/no-op everywhere
    // else) composes automatically into this base implementation, but a subclass override REPLACES this
    // entirely rather than calling base — see KingSpikePD's own override for why it multiplies by
    // EnrageDamageMultiplier itself instead of inheriting it for free.
    protected virtual float GetOwnDamage() => B.scaledDamage * EnrageDamageMultiplier;

    // See IEnemyPD.SetDamageOverride's own doc comment. false ignores `damage` entirely (and zeroes the
    // stored value — nothing reads it while damageOverridden is false, but zeroing avoids a stale non-zero
    // value lingering in state for no reason) — same "OFF is an immediate, real revert" shape as
    // SetKinematic/SetHurtboxDisabled, just with no live component to also touch here.
    public void SetDamageOverride(bool overridden, float damage = 0f)
    {
        B.damageOverridden = overridden;
        B.damageOverride = overridden ? damage : 0f;
    }
    public virtual bool CanContactDamage => true;

    // Whether contact damage from THIS enemy, right now, is purely passive/ambient body-touch damage rather
    // than a genuine attack — see CharacterStatsPD.NotifyDodge's own doc comment for what this drives (a
    // player dashing/leaping/teleporting through invincibility only registers a "Dodge" for hits where this
    // is false; passive contact never counts). Default true — most enemies' contact damage is just "the
    // player walked into something," not a real attack, so it shouldn't feel like dodging a telegraphed hit.
    // Override false (optionally phase-gated, independent of CanContactDamage's own gating — see
    // KingSpikePD's own override for why those two can legitimately differ) for enemies whose actual attack
    // mechanism IS contact damage (BlitzGloomPD's Blitz dash, KingSpikePD's Charge).
    protected virtual bool ContactDamageIsPassive => true;

    // Cached at the very top of Simulate() every tick (before the paused early-return, since
    // OnContactTrigger can fire from the physics pass regardless of pause state) — OnContactTrigger itself
    // doesn't receive delta as a parameter (it's a PredictedPhysicsCallbacks event, not Simulate() itself),
    // but needs it to convert ContactCooldownDuration from seconds to ticks. Safe to read there: delta is a
    // constant tick duration now that nothing manipulates Time.timeScale, so it's identical throughout
    // Simulate() -> DoPhysicsPass() -> LateSimulate() for a given tick regardless of exactly when read.
    private float _currentDelta;

    // No _authoredMaxHealth/_authoredDamage/_authoredMoveSpeed/_authoredAttackSpeed/_authoredXpValue/
    // _authoredPointsValue snapshots anymore (2026-09-22) — maxHealth/damage/moveSpeed/attackSpeed/xpValue/
    // pointsValue themselves are all pristine read-only authored baselines now (ConfigureSpawn writes each
    // scaled result into State instead, see scaledMaxHealth's own doc comment), so there's nothing left for
    // a snapshot-and-reset to protect for any of them — baseHealth/baseDamage/baseMoveSpeed/baseXpValue/
    // basePointsValue reset straight off the now-pristine plain fields every pooled reuse instead (see
    // LateAwake()).
    //
    // enemyName is a different case — ApplyTierNamePrefix would otherwise stack another
    // tier prefix onto whatever the PREVIOUS life's prefixed name already was (e.g. "Doom Gloom Blitz") on a
    // pooled reuse, or attach the wrong tier's prefix if a slot's tier changed between lives.
    private string _authoredEnemyName;

    // The enemy's black body parts — hit-flash/stun-tint target ONLY (see CacheRenderers/TriggerFlash
    // below). Never touched by ApplyTierMaterials — the black body stays black across every tier, only the
    // accent/shell colors below change.
    [SerializeField] private Renderer[] _renderers;
    // The enemy's colored accent parts — the actual per-tier color identity (red/blue/yellow/purple for
    // tier1-4). ApplyTierMaterials writes each tier's accentMaterial here at spawn. Deliberately separate
    // from _renderers above (not hit-flash/stun-tint targets — no _originalColors tracking needed for these,
    // ApplyTierMaterials just assigns the material once per life and leaves it alone otherwise).
    [SerializeField] private Renderer[] _accentRenderers;
    // Optional per-prefab visual shown while IsInvulnerable — same shape/precedent as CharacterStatsPD's own
    // _invincibilityShell. Left unassigned on prefabs that don't need one (nothing breaks — see UpdateView's
    // own null-guarded toggle).
    [SerializeField] private GameObject invulnerableShell;
    // Cached from invulnerableShell in Awake() rather than a second serialized field — a Renderer reference
    // wired independently of the GameObject it lives on risks the two silently drifting apart if a prefab's
    // invulnerableShell is ever reassigned. Null whenever invulnerableShell itself is (or has no Renderer).
    // Also part of the per-tier color identity (matches accentMaterial's color) — ApplyTierMaterials writes
    // each tier's shellMaterial here.
    private Renderer _invulnerableShellRenderer;
    private Color[] _originalColors;
    // Edge-detects viewB.spawnTier in UpdateView() so ApplyTierMaterials/ApplyTierNamePrefix (both otherwise
    // only ever called once, from ConfigureSpawn(), which doesn't run on every machine — see
    // EnemyBaseState.spawnCategoryRaw's own doc comment) get re-applied on any machine where spawnTier's own
    // State-replicated correction lands after this GameObject already exists. int.MinValue sentinel (not 0
    // or 1) so even a genuinely-tier-1 spawn's first UpdateView call re-applies once, matching what
    // ConfigureSpawn already did there — harmless, just confirms the materials/name are already right.
    private int _lastAppliedTier = int.MinValue;
    private bool  _wasDeadLastFrame;
    // Same edge-detect role as _wasDeadLastFrame, for SilentDespawn's own ActiveCount/category decrement —
    // kept fully separate since isDead/wiped are mutually exclusive outcomes for a given life, never both.
    private bool  _wasWipedLastFrame;
    // Same two-way predicted (viewState, not verified) hide edge-detect as _hiddenOnDeath, for
    // SilentDespawn — always hidden the instant wiped goes true, no deathHoldCount gating (a wipe has no
    // hold concept at all).
    private bool  _hiddenOnWipe;
    // Plain view-layer bookkeeping, not predicted state — edge-detects the instant predicted hide/unhide
    // below (a two-way sync against viewState.isDead, not a one-way latch — see UpdateView's own comment
    // for why this has to be reversible), same pattern as PhantomBladePD/WeaponRangedProjectilePD's
    // _hiddenOnExpire.
    private bool  _hiddenOnDeath;
    // Same two-way edge-detect as _hiddenOnDeath, but for _physicsCollider/gameObject.layer specifically —
    // kept as a SEPARATE flag because these are gated on isDead alone (not deathHoldCount), unlike the
    // render/hurtbox/contactDamageCollider hide, which stays visible for the whole duration of an on-death
    // hold (kill-launch, etc.) — see UpdateView's own comment.
    private bool  _physicsDisabledOnDeath;
    // Edge-detected the same way _hiddenOnDeath is, just for OnDeathStop() instead — see that method's own
    // doc comment for why it fires earlier (immediately on death) and independently of _hiddenOnDeath.
    private bool  _deathStopped;
    private float _verifiedHealth;
    private float _verifiedEnrageCountdown;
    // Edge-detected the same way _hiddenOnDeath is — drives "stay red for as long as this enemy is dead"
    // below (kill-launch, any future on-death-hold effect, or just the ordinary instant between dying and
    // being hidden), rather than the normal brief per-hit flash (which would fight this: it reverts to
    // _originalColors after 0.08s regardless of whether this enemy is still dead).
    private bool  _wasDeadForColorLastFrame;
    // Same edge-detection role as _wasDeadForColorLastFrame, for the yellow true-stun tint below — tracks
    // whether THIS frame's revert-to-original should fire (only when transitioning from tinted to neither
    // dead, flashing, nor stunned; see that block's own comment).
    private bool  _wasStunTintedLastFrame;
    // Animator.speed is engine-level playback rate, untouched by PausePD's own logical isPaused — nothing
    // else freezes it, so a paused game would otherwise keep idle/attack clips animating in place. Edge-
    // detected purely to avoid an unnecessary property write every frame; UpdateAnimator() itself is already
    // safe to keep calling while paused since viewState stops changing (its own state-transition checks
    // no-op), so this only needs to gate the Animator's own internal clip advancement.
    private bool  _animatorPaused;

    // Swapped to on Die() (see its own comment) and restored on pooled reuse — captured once in Awake()
    // since that only ever runs once per GameObject lifetime, unlike the pooled-reuse init block which
    // reruns every reuse and needs the TRUE original to restore to, not whatever Die() last left it as.
    // NOT a static field initializer — LayerMask.NameToLayer isn't allowed to run in a MonoBehaviour's
    // static constructor (fires too early, e.g. during script import/domain reload, before the engine's
    // layer table is ready), so this is resolved lazily on first real use instead, from Awake().
    private static int _noneLayer = -1;
    private int _originalLayer;

    private readonly RaycastHit[] _killLaunchSweepResults = new RaycastHit[16];

    // ── Subclass contract ──────────────────────────────────────────────────
    protected abstract ref EnemyBaseState GetBase(ref TState state);

    // Shorthand for accessing base fields on currentState
    protected ref EnemyBaseState B => ref GetBase(ref currentState);

    public float CurrentHealth => _verifiedHealth;

    // Verified (not predicted) same reasoning as CurrentHealth — reading straight off B.enrageCountdown
    // would jitter/flicker on any misprediction rollback, since that's live predicted state, not confirmed.
    // 0 for any non-Boss enemy (enrage is Boss-only) — explicit "empty," not "full," so it doesn't visually
    // imply an enrage that will never happen. Moot in practice since the only consumer (BossHealthBarUI, via
    // LocalUIManagerPD.UpdateBossHealthBars) only ever reads this for actual bosses anyway.
    public float EnrageProgress => spawnCategory != EnemySpawnCategory.Boss ? 0f : Mathf.Clamp01(_verifiedEnrageCountdown / EnrageTimer);

    public float MaxHealth => B.scaledMaxHealth;

    public float XpValue => xpValue;
    public float PointsValue => pointsValue;

    public string EnemyName => enemyName;

    public PredictedObjectID ObjectId => id.objectId;

    // ── State ──────────────────────────────────────────────────────────────
    protected override TState GetInitialState()
    {
        var s = default(TState);
        ref var b = ref GetBase(ref s);
        b.health = maxHealth;
        // Tier-1/no-scaling baseline — ConfigureSpawn overwrites these with the real scaled values right
        // after Create() returns, on whichever machine actually spawns this. See scaledMaxHealth's own doc
        // comment for why these 4 need to be here at all.
        b.scaledMaxHealth   = maxHealth;
        b.scaledMoveSpeed   = moveSpeed;
        b.scaledAttackSpeed = attackSpeed;
        b.scaledDamage      = damage;
        b.scaledXpValue     = xpValue;
        b.scaledPointsValue = pointsValue;
        b.spawnCategoryRaw  = (int)EnemySpawnCategory.Normal;
        b.spawnTier         = 1;
        b.contactCooldowns = DisposableList<ContactCooldown>.Create(4);
        b.killLaunchHitIds = DisposableList<PredictedObjectID>.Create(4);
        b.enrageCountdown = EnrageTimer;
        return s;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────
    private void Awake()
    {
        // Hurtbox lives directly on the root (not a child, unlike PhysicsCollider/ContactDamageCollider
        // below) so hit-detection code can resolve it to IEnemyPD via a same-GameObject GetComponent()
        // instead of GetComponentInParent() — cheap either way here since the hierarchy was already
        // shallow, but this is checked on every physics-hit-result, every tick, across every active
        // weapon/projectile, so avoiding the wasted extra hop adds up.
        _hurtbox = GetComponent<Collider>();
        _predictedTransform = GetComponent<PredictedTransform>();
        _originalLayer = gameObject.layer;
        if (_noneLayer < 0) _noneLayer = LayerMask.NameToLayer("None");

        var physicsChild = transform.Find("PhysicsCollider");
        if (physicsChild != null) _physicsCollider = physicsChild.GetComponent<Collider>();

        var contactChild = transform.Find("ContactDamageCollider");
        if (contactChild != null)
        {
            _contactDamageCollider = contactChild.GetComponent<Collider>();
            _contactDamageCollider.enabled = contactColliderEnabled;
        }

        // Guaranteed pristine — Awake() runs exactly once per real GameObject, before hierarchy.Create() has
        // ever returned to a spawner, so nothing has scaled anything (or reassigned enemyName) yet.
        _authoredEnemyName = enemyName;
        isStunnable = !stunResistant;

        _invulnerableShellRenderer = invulnerableShell != null ? invulnerableShell.GetComponent<Renderer>() : null;
        CacheRenderers();
        _animator = GetComponentInChildren<Animator>();

        OnAwake();
    }

    // Extension point mirroring OnConfigureSpawn's own doc comment — called ONCE per real GameObject,
    // guaranteed pristine (same "before hierarchy.Create() has ever returned to a spawner, so nothing has
    // scaled anything yet" guarantee the 4 built-in _authoredX fields rely on, see their own comment just
    // above). A subclass with its own per-tier-scaled unique field (see OnConfigureSpawn's own doc comment
    // for the full pattern) captures its "_authoredX = X;" snapshot here — NOT in LateAwake(), which re-fires
    // on every pooled reuse and would just be re-capturing whatever the PREVIOUS life's already-scaled value
    // happened to be.
    protected virtual void OnAwake() { }

    // Resets renderer visibility + color the INSTANT this GameObject (re)activates — Unity guarantees
    // OnEnable() fires before this frame ever renders, but a pooled reactivation and this identity's own
    // Simulate() tick (whose !b.initialized block also resets this) aren't always perfectly synchronized,
    // especially under client-side replay/reconciliation. Without this, a pooled corpse that was still
    // showing a hit-flash or kill-launch red at the moment it got hidden could show that same stale red for
    // a real visible frame or two on its NEXT life, before Simulate() gets around to resetting it — this
    // was the "enemies spawning red" symptom. _originalColors is populated once in Awake(), which Unity
    // always runs before the very first OnEnable() and never again after, so it's already valid by the time
    // this fires on every subsequent pooled reactivation too. Left in Simulate()'s own reset block as well
    // (redundant, not harmful) rather than removed, in case some future pooling path re-enables the
    // GameObject without this ever having fired.
    private void OnEnable()
    {
        // Defensive — in case a previous life was hidden/pooled mid-flash (_isFlashing still true, timer
        // never got to expire). Without this, TriggerFlash()'s own `if (_isFlashing) return;` guard would
        // think this life is already flashing red and skip re-painting it on the very next hit, even though
        // the color was just reset to _originalColors right above.
        _isFlashing = false;
        if (_renderers == null) return;
        foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = true;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = _originalColors[i];
    }

    protected override void LateAwake()
    {
        // baseHealth/baseDamage/baseMoveSpeed/baseXpValue/basePointsValue reset straight off maxHealth/
        // damage/moveSpeed/xpValue/pointsValue themselves every pooled reuse (2026-09-22) — those 6 plain
        // fields are pristine read-only authored baselines forever now (see scaledMaxHealth's own doc
        // comment), so they're already exactly what a fresh reset needs, no _authoredX snapshot required.
        // enemyName is the one field here that genuinely still needs its own _authoredEnemyName snapshot —
        // it's not numeric scaling, ApplyTierNamePrefix reassigns it outright.
        baseHealth    = maxHealth;
        baseDamage    = damage;
        baseMoveSpeed = moveSpeed;
        baseXpValue   = xpValue;
        basePointsValue = pointsValue;
        enemyName     = _authoredEnemyName;

        ActiveCount++;
        // Hardcoded Normal, NOT the spawnCategory property (2026-09-23 fix — the actual cause of Pleb/all-
        // category spawn-cap counters silently leaking since the spawnCategory State migration). Framework
        // ordering, confirmed via PurrDiction source (PredictedIdentityStatefull.Setup() -> base.Setup(),
        // which is what fires LateAwake() -> THEN fullPredictedState.state = GetInitialState()): LateAwake()
        // runs BEFORE GetInitialState() resets currentState for this life. On a pooled reuse, spawnCategory
        // (now State-backed) still reads whatever this instance's PREVIOUS life left it at — for Spike,
        // always Pleb — so this used to bump ActivePlebCount directly using stale data. GetInitialState()
        // then resets spawnCategoryRaw back to Normal moments later, and ConfigureSpawn() (called by the
        // spawner right after) sees category != spawnCategory (Pleb != freshly-reset Normal) and does ITS
        // OWN correction on top — net +2 for the real category, only ever -1 on death: a permanent +1 leak
        // per pooled reuse, on every category (fastest/most visible on Pleb since it's spawned far more
        // often than anything else). Before the migration this was harmless — spawnCategory was a plain
        // field, equally stale at both read sites, so they always matched and ConfigureSpawn's own check
        // correctly no-op'd. Hardcoding Normal here restores that symmetry: this bump now always matches
        // what GetInitialState() is guaranteed to reset spawnCategoryRaw to right after, so ConfigureSpawn's
        // own "category != spawnCategory" check is correct again regardless of pooling history.
        AdjustCategoryCount(EnemySpawnCategory.Normal, +1);
        _verifiedHealth = maxHealth;

        if (_contactDamageCollider != null && _contactDamageCollider.TryGetComponent(out PredictedPhysicsCallbacks callbacks))
        {
            callbacks.onTriggerEnter += OnContactTrigger;
            callbacks.onTriggerStay += OnContactTrigger;
        }

        // Replaces the old _killLaunchFeedbackQueue + UpdateView()-drain pattern — see WeaponBasePD.cs's
        // HitFeedbackData doc comment for why. Constructed fresh every logical (re)spawn, same as every
        // other PredictedEvent in this codebase — no accumulation risk since nothing holds a stale
        // reference to a previous life's instance across pooled reuse.
        _onHitFeedback = new PredictedEvent<HitFeedbackData>(predictionManager, this);
        _onHitFeedback.AddListener(OnHitFeedback);

        // Same "constructed fresh every logical (re)spawn" reasoning as _onHitFeedback above — this enemy's
        // own status popups (Stun/Interrupted/Slowed) are purely self-referential, safe to own directly the
        // same way a player's own CharacterStatsPD owns its equivalent event.
        _onStatusPopup = new PredictedEvent<StatusPopupData>(predictionManager, this);
        _onStatusPopup.AddListener(OnStatusPopup);

        // Declumping seed — re-rolled on every real spawn AND every pooled reuse (LateAwake() re-fires on
        // both, unlike OnRespawnReset() which only some subclasses' own overrides chain back to base for) —
        // see EnemyBaseState.separationCheckTimer/personalPushAngle's own doc comments.
        ref var declumpState = ref GetBase(ref currentState);
        declumpState.separationCheckTimer = GameRandom.instance.Next(0, SeparationCheckInterval);
        declumpState.personalPushAngle    = GameRandom.instance.NextFloat(0f, 360f);
        // Radial strafe direction — rolled once here too, same "decide once, use for the whole lifetime"
        // reasoning, so every enemy doesn't orbit the same way (looked unnatural when it was fixed). See
        // HandleMovement's own doc comment.
        declumpState.strafeClockwise = GameRandom.instance.NextFloat() < 0.5f;

        EnemySpawnerPD.instance?.RegisterEnemy(this);
    }

    private PredictedEvent<HitFeedbackData> _onHitFeedback;

    private void OnHitFeedback(HitFeedbackData data)
    {
        if (data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy))
            enemy.TriggerHitFeedback(data.damage, data.isCrit, data.isHighroll, data.attacker, data.isPriority, data.overrideSfx, data.sfx);
    }

    // Enemy-side counterpart to CharacterStatsPD's own _onStatusPopup/StatusPopupData — reuses the exact same
    // struct/kind system (StatusPopupKind/DamageNumberManager's own Resolve* methods), just relayed to
    // SpawnStatusOnEnemy instead of SpawnStatusOnPlayer. Only Stun()/Slow()/Interrupt() call this today (see
    // each method's own doc comment) — no external Add/RemoveListener wrappers needed, same "no one outside
    // this class needs to subscribe" shape as OnHitFeedback above.
    private PredictedEvent<StatusPopupData> _onStatusPopup;

    private void OnStatusPopup(StatusPopupData data) =>
        DamageNumberManager.instance?.SpawnStatusOnEnemy(data.kind, this, data.customMessage, data.colorOverride);

    // Plebs never show status popups (Executed/Stun/Slowed/Interrupted) — explicit user request, damage
    // numbers for them stay untouched (a completely separate path, TriggerHitFeedback/ShowDamageNumber, never
    // routes through this method at all). Gated here, not at each of the 4 call sites, so this stays correct
    // regardless of which status ever gets added later. Skips firing _onStatusPopup entirely rather than
    // gating inside OnStatusPopup's own listener — no point paying for a PredictedEvent invoke nothing will
    // ever visually react to.
    private void NotifyStatusPopup(DamageNumberManager.StatusPopupKind kind)
    {
        if (spawnCategory == EnemySpawnCategory.Pleb) return;
        _onStatusPopup.Invoke(new StatusPopupData { kind = kind, customMessage = null, colorOverride = null });
    }

    private void OnContactTrigger(GameObject other)
    {
        if (!CanContactDamage) return;
        if (IsStunned) return;
        ref var b = ref B;
        if (b.isDead) return;
        // Held-orbit freeze (see IEnemyPD.SetOrbitFrozen's own doc comment) — separate from IsStunned above,
        // since this enemy's OWN stunTimer is untouched here; it's a derived freeze off the OWNER's stun.
        if (b.orbitFrozen) return;

        // IFriendlyPD (2026-08-30, was CharacterStatsPD + the !stats.owner.HasValue guard) — targets any
        // registered friendly, not just a connected player. The old guard is dropped entirely rather than
        // replaced: IsDead already excludes genuinely invalid targets, and ObjectId is never "unclaimed" the
        // way a PlayerID? could be, so there's no equivalent state left to guard against (confirmed via
        // AskUserQuestion — matches every other migrated attack, none of which have this kind of guard).
        var friendly = other.GetComponent<IFriendlyPD>();
        if (friendly == null || friendly.IsDead) return;

        var target = friendly.ObjectId;
        var cooldowns = b.contactCooldowns;
        int ticks = Mathf.Max(1, Mathf.RoundToInt(ContactCooldownDuration / _currentDelta));

        // Only a pleb actively IN MOTION toward/at a player — being Pulled in, or LaunchAlong'd during King
        // Orbiter's Shot/Persistent Pleb Shot — counts as a dodgeable attack (2026-08-24, narrowed from the
        // broader IsBeingHeld — Orbiting and the stationary Claimed/WaitingToOrbit sub-phases don't warn/
        // telegraph a specific incoming hit the way Pulling/a launch does, "if it has an indicator, it can be
        // dodged" per explicit design). Orbiting a claimed pleb around the boss stays ordinary passive
        // contact — same exclusion a free-roaming pleb's own touch gets. launchActive is deliberately
        // uncoupled from orbitPhase (see LaunchAlong's own doc comment), so it's checked independently here,
        // not folded into the Pulling check.
        bool isDodgeableAttack = b.orbitPhase == (int)EnemyOrbitPhase.Pulling || b.launchActive;
        bool isPlebForDodge = spawnCategory == EnemySpawnCategory.Pleb && !isDodgeableAttack;
        bool isPassiveForDodge = ContactDamageIsPassive && !isDodgeableAttack;

        for (int i = 0; i < cooldowns.Count; i++)
        {
            if (!cooldowns[i].target.Equals(target)) continue;
            if (cooldowns[i].remaining > 0) return;
            cooldowns[i] = new ContactCooldown { target = target, remaining = ticks };
            friendly.TakeDamage(GetDamage(), false, ObjectId, isPlebForDodge, isPassiveForDodge);
            return;
        }

        cooldowns.Add(new ContactCooldown { target = target, remaining = ticks });
        friendly.TakeDamage(GetDamage(), false, ObjectId, isPlebForDodge, isPassiveForDodge);
    }

    protected override void Destroyed()
    {
        KillGraph();
        EnemySpawnerPD.instance?.UnregisterEnemy(this);
    }

    // ── Simulation ─────────────────────────────────────────────────────────
    protected override void Simulate(ref TState state, float delta)
    {
        _currentDelta = delta;

        if (PausePD.instance != null && PausePD.instance.IsPaused)
        {
            // DoPhysicsPass() isn't gated by IsPaused — it keeps integrating whatever velocity the
            // Rigidbody already has every tick regardless of the visual pause. Without this, an enemy
            // mid-knockback or mid-dash-to-target when pause hits would keep sliding from residual
            // momentum while "paused" (same root cause as the equivalent player-side fix).
            // Guarded by !isKinematic — SpearGloomPD/KingSpikePD's Windup/Stabbing-style kinematic windows
            // mean this rigidbody can be kinematic when pause hits, and PredictedRigidbody's velocity
            // setters throw ("not supported") if attempted on a kinematic body.
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            return;
        }
        ref var b = ref GetBase(ref state);

        // Pooled reuse never calls OnRemovedFromPool() (PredictedHierarchy's pool path re-registers
        // with reset:false), so first-tick setup has to be driven off State instead, since
        // GetInitialState() is the one thing reliably re-run on every reuse.
        if (!b.initialized)
        {
            // On the client, this tick can be replayed before the pooled object has actually been
            // reactivated yet, which throws when touching the Animator and — worse — would otherwise
            // mark this spawn as already-reset without ever having rebound the animator, permanently
            // leaving it on whatever pose it was frozen in during its previous pooled life. Don't commit
            // to initialized until we can actually do the reset; retry next tick instead.
            if (!gameObject.activeInHierarchy) return;

            b.initialized = true;
            _spawnGeneration++;
            _target = null;
            _targetStats = null;
            lastKnownTargetPosition = default;
            hasLastKnownTargetPosition = false;
            _offsetChaseTrackedTarget = null;
            _offsetChaseTrackedLastKnownPosition = default;
            // Forces the very first UpdateView() this life to re-apply tier materials/name regardless of
            // what _lastAppliedTier happened to be left at by whatever PREVIOUS life last used this exact
            // pooled GameObject — without this, a fresh life that coincidentally shares its predecessor's
            // tier number would silently skip re-application, which matters specifically for a machine where
            // ConfigureSpawn() itself never runs this life (see _lastAppliedTier's own doc comment).
            _lastAppliedTier = int.MinValue;
            _wasDeadLastFrame = false;
            b.wiped = false;
            _wasWipedLastFrame = false;
            _hiddenOnWipe = false;
            _hiddenOnDeath    = false;
            _physicsDisabledOnDeath = false;
            _deathStopped     = false;
            _verifiedHealth   = maxHealth;
            gameObject.layer  = _originalLayer;

            // Same "previous life died mid-something and never got a chance to clean up after itself"
            // reasoning as killLaunchActive's own reset below — a pooled slot reused before an owning
            // Orbiter (or any future caller) ever released it would otherwise silently hand a brand new
            // spawn a stuck movementDisabled/invulnerable/externalKinematic/physicsColliderDisabled/
            // orbitPhase from its PREVIOUS life, with nothing left around to ever call the matching
            // SetX(false)/StopOrbiting() again to clear it.
            b.movementDisabled        = false;
            b.invulnerable             = false;
            b.externalKinematic        = false;
            b.physicsColliderDisabled  = false;
            b.hurtboxDisabled          = false;
            b.damageOverridden         = false;
            b.damageOverride           = 0f;
            b.orbitPhase               = (int)EnemyOrbitPhase.None;
            b.orbitFrozen              = false;

            // Defensive — in case a previous life died mid-kill-launch (still kinematic, physics collider
            // still disabled) and got pooled before that ever got a chance to clean itself up.
            if (_rigidbody != null) _rigidbody.isKinematic = false;
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

            // Same "previous life died mid-kill-launch" scenario as above, but for killLaunchActive itself
            // — this was the actual gap. HandleKillLaunch only ever clears it when killLaunchRemainingDistance
            // naturally reaches 0 (StartKillLaunch/HandleKillLaunch's own comments); if this pooled slot ever
            // got hidden/reused before that finished, killLaunchActive stayed stuck true forever, silently
            // inherited by every future life from this slot — Simulate()'s own `if (b.killLaunchActive)
            // HandleKillLaunch(...)` check would then incorrectly treat a brand new, fully alive spawn as
            // still mid-launch. This used to ALSO cause the "some enemies stay permanently red" bug back
            // when the color-revert was gated on killLaunchActive specifically (a stuck flag meant the
            // revert bailed out forever) — no longer possible now that the stay-red/revert logic keys off
            // isDead instead (see UpdateView's own comment), which GetInitialState() already resets safely
            // on every reuse regardless of whether killLaunchActive itself gets reset here. Still resetting
            // it here regardless, for the HandleKillLaunch mis-triggering reason above.
            b.killLaunchActive = false;
            b.killLaunchRemainingDistance = 0f;
            b.killLaunchHitIds.Clear();

            foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = true;
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = _originalColors[i];

            if (_hurtbox != null) _hurtbox.enabled = true;
            if (_physicsCollider != null) _physicsCollider.enabled = true;
            if (_contactDamageCollider != null) _contactDamageCollider.enabled = contactColliderEnabled;

            if (_animator != null)
            {
                _animator.enabled = true;
                // Force back to each layer's default state, regardless of whatever pose it was
                // frozen in when pooled (e.g. mid-attack) — enabled alone doesn't reset this.
                _animator.Rebind();
                _animator.Update(0f);
            }

            OnRespawnReset();
        }

        if (b.isDead)
        {
            // The one thing still allowed to run post-death — see HandleKillLaunch's own doc comment for
            // why this lives here (generic on EnemyBasePD) rather than on whatever augment started it.
            if (b.killLaunchActive) HandleKillLaunch(ref b, delta);
            return;
        }

        // A wipe (SilentDespawn — see its own doc comment) previously left every subclass's own
        // movement/attack-sequence logic running untouched, since nothing here checked it — only the
        // predicted-instant render hide (UpdateView) and the eventual verified-gated hierarchy.Delete()
        // actually reacted to it. A wiped-but-not-yet-deleted enemy could keep chasing/attacking/spawning
        // hazards for however many ticks that deletion took to confirm, invisibly — directly contradicting
        // SilentDespawn's own "just gone" intent. No kill-launch equivalent to preserve here (a wipe has no
        // kill-launch interaction at all, unlike a real death) — a plain, unconditional return.
        if (b.wiped) return;

        // Unconditional, ahead of the stun-freeze return below — see lastKnownTargetPosition's own doc
        // comment. Runs regardless of stun/attack-lock, same reasoning as HandleSeparation right below: a
        // passive record, not an action.
        if (_targetStats != null)
        {
            lastKnownTargetPosition = _targetStats.transform.position;
            hasLastKnownTargetPosition = true;
        }

        // Unconditional, ahead of the stun-freeze return below — declumping is a passive spatial correction,
        // not an "action" a stun/attack-lock should block, and it should keep working even for an enemy
        // that's currently stunned or mid-attack. See HandleSeparation's own doc comment.
        HandleSeparation(ref b, delta);

        // Independent of stunTimer below — a slow still counts down while stunned (see the field's own doc
        // comment), unlike trueStunTimer which only ticks inside the stun branch itself.
        if (b.slowTimer > 0f) b.slowTimer -= delta;

        // Bleed tick (2026-10-06, ExeBigAxeBleedAugment) — see State.bleedDamagePerSecond's own doc
        // comment for the bucket mechanism. Continuous, delta-scaled damage every tick rather than
        // discrete periodic pulses — mathematically the same total damage over the bleed's lifetime,
        // simpler (no second tick-interval timer needed), and safe to call TakeDamage directly here since
        // this is a same-identity mutation (the enemy damaging itself from its own Simulate()), not a
        // cross-identity call — none of the deferred-delta/UpdateView dance a cross-identity DoT-style
        // accumulator would need. Independent of stunTimer/slowTimer — bleed is pure damage, not a CC
        // effect, so it ticks through a stun exactly like it would through anything else.
        if (b.bleedTimer > 0f)
        {
            b.bleedTimer -= delta;
            if (b.bleedDamagePerSecond > 0f)
            {
                float tickDamage = b.bleedDamagePerSecond * delta;
                TakeDamage(tickDamage, isCrit: false, b.bleedAttacker);

                // Damage number popup — same "resolve the PlayerID to a live CharacterStatsPD, then fire
                // THIS enemy's own _onHitFeedback" pattern the kill-launch splash damage above already uses
                // (see its own comment) — attribution is whichever player b.bleedAttacker currently holds,
                // i.e. "most recent stack wins," same accepted limitation bleedAttacker's own doc comment
                // already describes (the bucket model has no per-stack attacker tracking to attribute to
                // instead). Not a weapon hit, so isPriority stays false. ShowDamageNumber's own
                // per-(enemy,attacker) stacking key means every tick just climbs one running number instead
                // of spamming a new popup per tick.
                if (CharacterStatsPD.All.TryGetValue(b.bleedAttacker, out var bleedAttackerStats))
                    _onHitFeedback.Invoke(new HitFeedbackData { target = ObjectId, damage = tickDamage, isCrit = false, isHighroll = false, attacker = bleedAttackerStats, isPriority = false });
            }
            if (b.bleedTimer <= 0f) b.bleedDamagePerSecond = 0f;
        }

        if (b.stunTimer > 0f)
        {
            b.stunTimer -= delta;
            // Always <= stunTimer (see the field's own doc comment), so this never outlives the branch above.
            // Ticks down here regardless of isStunnable below — a true stun lands (and starts counting down)
            // the instant it's applied even mid-attack (see ApplyStun's own doc comment), not just once the
            // freeze below actually starts suppressing movement.
            if (b.trueStunTimer > 0f) b.trueStunTimer -= delta;

            // Only freezes everything else below (movement, attack-sequence progression, enrage,
            // target-scan) while NOT currently protected by an attack-sequence (isStunnable — see each
            // subclass's own per-tick assignment, e.g. BlitzGloomPD.HandleActiveState). A true stun landing
            // mid-attack is still recorded/ticking above (and tints yellow via trueStunTimer), but
            // HandleActiveState/HandleDash below keep running as normal so whatever's already committed
            // finishes uninterrupted — see IsTrueStunned's own doc comment for where each subclass's own
            // multi-attack chain-check plugs in to skip chaining into ANOTHER attack instead of interrupting
            // the current one. Once the subclass's own terminal/post-attack state flips isStunnable back to
            // true, this freeze takes hold on that later tick if the stun is still active.
            if (isStunnable) return;
        }

        // Enrage is Boss-only — explicit spawnCategory gate, never ticks for any other enemy. Same "don't
        // tick while stunned" behavior as old BossBase. !b.enraged guards it to firing exactly once.
        if (!b.enraged && spawnCategory == EnemySpawnCategory.Boss)
        {
            b.enrageCountdown -= delta;
            if (b.enrageCountdown <= 0f)
            {
                b.enraged = true;

                // The immediate "Initial" Move Speed/Damage jump is handled entirely by
                // EnrageMoveSpeedMultiplier/EnrageDamageMultiplier reading b.enraged directly (see their own
                // doc comments) — no state to seed for it here. Ongoing per-interval stacks start from 0 and
                // land AFTER the first interval elapses, on top of that initial jump.
                b.enrageMoveSpeedStackTimer = EnrageMoveSpeedStackInterval;
                b.enrageDamageStackTimer = EnrageDamageStackInterval;
            }
        }
        // Uncapped — deliberately never stops accumulating for the rest of this boss's life, see
        // EnrageMoveSpeedStackPercent's own field comment for why (the whole point is that stalling the
        // fight keeps getting worse, forever, with no ceiling). Damage stacks tick on their own independent
        // timer/interval, in parallel — see EnrageDamageStackPercent's own field comment.
        else if (b.enraged && spawnCategory == EnemySpawnCategory.Boss)
        {
            b.enrageMoveSpeedStackTimer -= delta;
            if (b.enrageMoveSpeedStackTimer <= 0f)
            {
                b.enrageMoveSpeedStacks++;
                b.enrageMoveSpeedStackTimer = EnrageMoveSpeedStackInterval;
            }

            b.enrageDamageStackTimer -= delta;
            if (b.enrageDamageStackTimer <= 0f)
            {
                b.enrageDamageStacks++;
                b.enrageDamageStackTimer = EnrageDamageStackInterval;
            }
        }

        // No periodic re-scan anymore — constantly re-scanning CharacterStatsPD.All on a timer, from every
        // single enemy, every tick or two, was needless cost at swarm scale for a horde game where target
        // reprioritization doesn't need to be that reactive. Instead: acquire once (this covers spawn, and
        // covers "was null last tick" generally), then keep that target until it's gone (null or dead) —
        // cheap, since this is just a null/bool check, not a scan. Subclasses with a discrete attack-routine
        // state machine additionally call FindClosestTarget() once themselves right as they return to their
        // idle/Moving state (see e.g. SpearGloomPD.EnterMoving), to reprioritize without re-scanning every
        // tick throughout the routine itself. Taunt/invisibility changes are handled separately and
        // immediately via OnPlayerTauntChanged/OnPlayerInvisibleChanged below (pushed by EnemySpawnerPD the
        // instant either flag flips), not by polling here.
        //
        // Reassigning _target here (including nulling it, explicitly rather than relying on
        // FindClosestTarget's own "leave untouched if nothing found" behavior) is always safe to do
        // immediately, mid-attack included — it never interrupts anything. An attack already underway reads
        // its own captured attackAnchorPosition (see CaptureAttackAnchor), not this live _target/_targetStats
        // pair, so reassigning who's "next" here has no effect on whatever's already in flight. Each
        // subclass's own attack-routine state machine is what decides, at its own natural between-attacks
        // checkpoint, whether _target being null (or changed) means the chain ends here or continues onto a
        // new target — never forced from this generic point.
        if (_target == null || (_targetStats != null && _targetStats.IsDead))
        {
            _target = null;
            _targetStats = null;
            FindClosestTarget();
        }

        for (int i = b.contactCooldowns.Count - 1; i >= 0; i--)
        {
            var entry = b.contactCooldowns[i];
            entry.remaining--;
            if (entry.remaining <= 0)
                b.contactCooldowns.RemoveAt(i);
            else
                b.contactCooldowns[i] = entry;
        }

        bool wasDashing = b.isDashing;
        if (b.isDashing)
            HandleDash(ref state, delta);

        // Sibling to isDashing above, additive — see EnemyBaseState.isOffsetChasing's own doc comment.
        // Every enemy that never calls StartOffsetChase leaves this permanently false, so this reduces to
        // exactly the original isDashing-only behavior for them — zero risk to every other dasher.
        bool wasOffsetChasing = b.isOffsetChasing;
        if (b.isOffsetChasing)
            HandleOffsetChase(ref state, delta);

        if (!b.isDashing && !b.isOffsetChasing)
            HandleActiveState(ref state, delta, wasDashing || wasOffsetChasing);

        // See IEnemyPD.SetKinematic's own doc comment for why this is a defensive re-assertion here rather
        // than an immediate write inside SetKinematic(true) itself — applied AFTER HandleActiveState so it
        // wins if this same tick's subclass logic tried to set isKinematic to something else, never forces
        // false (that's SetKinematic(false)'s own job, an immediate direct write, not this).
        if (b.externalKinematic && _rigidbody != null && !_rigidbody.isKinematic)
        {
            // Zeroed on the entering edge only, same convention every other kinematic-toggle site in this
            // codebase already uses (e.g. Interrupt()'s own revert, every subclass's own Windup/Stabbing
            // kinematic block) — a kinematic Rigidbody's velocity setters throw if attempted AFTER this
            // flips true, and leftover velocity would otherwise still be sitting there (silently reapplied)
            // the instant something un-kinematics it later.
            _rigidbody.velocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
            _rigidbody.isKinematic = true;
        }

        if (b.physicsColliderDisabled && _physicsCollider != null && _physicsCollider.enabled)
            _physicsCollider.enabled = false;

        if (b.hurtboxDisabled && _hurtbox != null && _hurtbox.enabled)
            _hurtbox.enabled = false;
    }

    protected virtual void HandleActiveState(ref TState state, float delta, bool dashJustEnded = false)
    {
        if (_target != null) HandleMovement(ref state, delta);
    }

    private static float WrapOrbitAngle(float angle) => ((angle % 360f) + 360f) % 360f;

    // Drives a currently-held enemy's own pull->orbit position (see IEnemyPD.OrbitTarget's own doc comment
    // for the full mechanism and why this lives here rather than being pushed in by the owner). Runs from
    // THIS identity's own LateSimulate() specifically — by the time any identity's LateSimulate() runs for a
    // given tick, every identity's Simulate() (including whichever owner just called OrbitTarget() on this
    // enemy, or moved itself) has already landed for that same tick, per the tick-order guarantee _currentDelta's
    // own doc comment already establishes. Reading the owner's transform.position from here is therefore
    // always fresh, with no ordering race — the same reasoning SpikeShieldPD's own orbit-around-owner design
    // already relies on, just generalized onto the base class since any enemy can now be the one orbiting.
    protected override void LateSimulate(ref TState state, float delta)
    {
        ref var b = ref GetBase(ref state);
        // wiped added alongside isDead — see Simulate()'s own identical addition/doc comment; a wiped enemy
        // shouldn't keep orbiting/launching either.
        if (b.isDead || b.wiped) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        // Checked FIRST, independent of orbitPhase below — see EnemyBaseState.launchActive/IEnemyPD.
        // LaunchAlong's own doc comments. Supersedes orbit positioning for its own duration; orbitPhase is
        // left completely untouched (a launching enemy can still be legitimately "held").
        if (b.launchActive)
        {
            float step = Mathf.Min(b.launchSpeed * delta, b.launchRemainingDistance);
            Vector3 newPos = transform.position + b.launchDirection * step;
            newPos.y = transform.position.y;
            if (_rigidbody != null)
            {
                _rigidbody.MovePosition(newPos);
                _rigidbody.MoveRotation(Quaternion.LookRotation(b.launchDirection));
            }
            b.launchRemainingDistance -= step;
            if (b.launchRemainingDistance <= 0f) b.launchActive = false;
            return;
        }

        // None: not held at all. Claimed: held, but SetKinematic(true) is what's actually freezing this
        // enemy in place — there's no position math to run yet, OrbitTarget() hasn't been called (see
        // IEnemyPD.ClaimForOrbit's own doc comment for why these are deliberately separate calls).
        if (b.orbitPhase == (int)EnemyOrbitPhase.None || b.orbitPhase == (int)EnemyOrbitPhase.Claimed) return;

        var ownerGo = hierarchy.GetGameObject(b.orbitOwnerId);
        if (ownerGo == null || !ownerGo.TryGetComponent(out IEnemyPD owner) || owner._isDead)
        {
            // Defensive only — the owner's own Die()/OnInterrupted() (or equivalent) are expected to
            // proactively StopOrbiting() every enemy it's holding the instant it goes away. This just guards
            // against some future teardown path that skips that call, so a held enemy never gets stuck
            // orbiting a GameObject that no longer resolves.
            b.orbitPhase  = (int)EnemyOrbitPhase.None;
            b.orbitFrozen = false;
            return;
        }

        // See IEnemyPD.SetOrbitFrozen's own doc comment — the owner's own RefreshHeldDamageOverrides sets
        // this fresh every tick, so skipping the whole recompute-and-move below here is enough on its own;
        // nothing else needs to know this enemy is currently owner-stun-frozen.
        if (b.orbitFrozen) return;

        // Orbiting at angularSpeedDegPerSec == 0 (2026-08-22 fix) skips this whole recompute-and-move —
        // nothing legitimately updates once the angle stops advancing (only Orbiting itself ever advances
        // orbitAngleDeg, right below), so re-deriving the "same" slot position from a fresh owner.transform.
        // position read every tick was pure downside: any sub-tick noise in the OWNER's own position (or the
        // angle-to-position trig round-trip) got reintroduced into this enemy's position every single tick,
        // even though this enemy is supposed to be frozen in place — visible as a small constant vibration on
        // anything reading this enemy's transform.position for its own purposes (e.g. KingOrbiterPD's own
        // shot-aim tracking during its KnockbackWindup/ShootFlight hold, the case this was confirmed live
        // against). Only the exact scenario a caller creates via SetOrbitAngularSpeed(0) while still
        // Orbiting — WaitingToOrbit's own separate "frozen at the slot until BeginOrbiting() releases it"
        // wait is untouched here, not confirmed safe to skip the same way.
        if (b.orbitPhase == (int)EnemyOrbitPhase.Orbiting && b.orbitAngularSpeedDegPerSec == 0f)
            return;

        Vector3 ownerPos = owner.transform.position;

        // Slot position is always computed the same way regardless of phase — the caller-assigned angle
        // (fixed during Pulling, advancing during Orbiting) at orbitRadiusTarget from the owner's current
        // position. Pulling only differs in that the enemy hasn't arrived there yet (see the Lerp below).
        // owner.EffectiveAttackSpeed (2026-08-31, was plain delta) — a Slow landing on the HOLDER now
        // correctly slows the orbit rotation itself too, not just its own movement/attack timing. Reads the
        // OWNER's attack speed, not this held enemy's own (irrelevant — it isn't the one attacking; it's
        // being spun by whoever grabbed it). orbitAngularSpeedDegPerSec itself stays the caller-assigned BASE
        // rate (set once via OrbitTarget/SetOrbitAngularSpeed) — this multiply is a live, per-tick factor on
        // top, so it reacts immediately to the owner's Slow starting/ending without the owner needing to
        // re-push a new angular speed value itself.
        if (b.orbitPhase == (int)EnemyOrbitPhase.Orbiting)
            b.orbitAngleDeg = WrapOrbitAngle(b.orbitAngleDeg + b.orbitAngularSpeedDegPerSec * delta * owner.EffectiveAttackSpeed);

        float rad = b.orbitAngleDeg * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
        Vector3 slotPos = ownerPos + dir * b.orbitRadiusTarget;

        Vector3 pos;
        if (b.orbitPhase == (int)EnemyOrbitPhase.Pulling)
        {
            // Flat-speed MoveTowards (2026-09-12, was a fixed-DURATION Lerp from a snapshotted start
            // position) — straight-line (Cartesian) toward the live slot position, NOT a radius-at-fixed-
            // angle move, so it visibly travels toward where it's really going instead of snapping sideways
            // onto the target angle the instant the pull begins. See IEnemyPD.OrbitTarget's own doc comment
            // for why this reads transform.position directly (last tick's own MovePosition result) instead
            // of a separately-tracked start point. owner.EffectiveAttackSpeed (2026-08-31, kept from the
            // old duration-based version) — a Slow/haste on the HOLDER still speeds up or slows down the
            // pull-in motion itself, not just when it started.
            float step = b.orbitPullSpeed * delta * owner.EffectiveAttackSpeed;
            pos = Vector3.MoveTowards(transform.position, slotPos, step);
            // WaitingToOrbit, not Orbiting directly (2026-08-17) — see EnemyOrbitPhase's own doc comment.
            // Freezes here at the slot until the caller explicitly releases it via BeginOrbiting().
            // MoveTowards never overshoots (clamps exactly to slotPos once within one step), so reaching it
            // exactly IS arrival — no separate tolerance needed.
            if (pos == slotPos) b.orbitPhase = (int)EnemyOrbitPhase.WaitingToOrbit;
        }
        else
        {
            pos = slotPos;
        }
        pos.y = transform.position.y;

        if (_rigidbody != null)
        {
            Vector3 facing = pos - ownerPos;
            facing.y = 0f;
            if (facing.sqrMagnitude < 0.0001f) facing = -dir;
            _rigidbody.MovePosition(pos);
            _rigidbody.MoveRotation(Quaternion.LookRotation(-facing.normalized)); // faces inward, toward the owner
        }
    }

    protected override void UpdateView(TState viewState, TState? verified)
    {
        // Hoisted out of the animator block below — the flash-decay block further down needs the same
        // paused check, and UpdateView()/plain Update() run at the same per-frame cadence (confirmed via
        // PredictionManager's UpdateViewMode), so there's no reason to compute this twice.
        bool paused = PausePD.instance != null && PausePD.instance.IsPaused;

        if (_animator != null)
        {
            if (paused != _animatorPaused)
            {
                _animatorPaused = paused;
                _animator.speed = paused ? 0f : 1f;
            }
            UpdateAnimator(viewState);
        }

        // Instant, predicted hide/unhide — doesn't wait for server verification, unlike the state-critical
        // one-shot side effects below. Same fix as PhantomBladePD/WeaponRangedProjectilePD: viewState is
        // this machine's own already-simulated prediction (identical deterministic simulation runs on
        // every machine), available immediately, whereas verified only populates for non-owner clients
        // after a network round-trip — that round-trip was the visible "corpse lingers, still visible and
        // still hittable, after it should already be dead" delay. A TWO-WAY sync against viewB.isDead, not
        // a one-way latch — was previously one-way on the (documented, since-disproven) assumption that
        // isDead reverting to false was never a real scenario. It is: a mispredicted-lethal hit (isDead
        // true for one predicted frame, then corrected back to false once the real, non-lethal server
        // state resolves) used to leave the enemy permanently hidden/uncollidable despite still being
        // alive — the "invisible enemy" bug. Edge-detected both ways now so a correction actually restores
        // visibility instead of leaving a ghost.
        ref var viewB = ref GetBase(ref viewState);

        // Self-healing tier material/name-prefix application — see _lastAppliedTier's own doc comment for
        // why this can't just be the one ConfigureSpawn()-time call it used to be alone.
        if (viewB.spawnTier != _lastAppliedTier)
        {
            _lastAppliedTier = viewB.spawnTier;
            ApplyTierMaterials(viewB.spawnTier);
            ApplyTierNamePrefix(viewB.spawnTier);
        }

        // Unconditional, no edge-detect — SetActive on an already-matching value is a cheap no-op, same
        // precedent as CharacterStatsPD._invincibilityShell's own identical toggle.
        if (invulnerableShell != null) invulnerableShell.SetActive(viewB.invulnerable);

        // PhysicsCollider + layer are gated on isDead ALONE (not deathHoldCount) — a dead body's physical
        // presence (can't be re-hit, doesn't collide) should end immediately regardless of whether the
        // render-hide below is being held up for an on-death effect (kill-launch, etc.) — moved here from
        // Die() (which used to set these as a one-shot, unrecoverable side effect) so the same misprediction
        // correction above also restores these instead of leaving the corpse visually alive but permanently
        // untargetable/wrong-layer.
        bool viewDead = viewB.isDead;
        if (viewDead != _physicsDisabledOnDeath)
        {
            _physicsDisabledOnDeath = viewDead;
            if (_physicsCollider != null) _physicsCollider.enabled = !viewDead;
            gameObject.layer = viewDead ? _noneLayer : _originalLayer;
        }

        // Stops attack telegraphs immediately on death OR wipe — see OnDeathStop's own doc comment for why
        // this is deliberately NOT gated on deathHoldCount the way the render-hide block below is (a wipe
        // has no hold concept regardless). Deliberately NOT gated on activeInHierarchy either (previously
        // was, briefly) — a windup indicator that's been unparented to world root (LeaperGloomPD's, during
        // Leaping) is NOT a child of this GameObject, so it doesn't get implicitly hidden when this enemy
        // itself is deactivated/pooled; if this enemy gets hidden and pooled before ever becoming active
        // again this life (entirely possible — hide only waits on deathHoldCount, not on this instance
        // passing through an active frame first), gating OnDeathStop() here meant it could simply never
        // fire, leaving that detached indicator visible forever. Individual overrides that call
        // Animator.Play() guard THAT specific call's own activeInHierarchy instead (seeing SpearGloomPD's own
        // OnDeathStop for why), so the indicator-hide half of OnDeathStop always runs immediately while only
        // the animator snap can harmlessly skip.
        // wiped added (2026-08-12) — a difficulty-pacing wipe (EnemySpawnerPD.WipeAllEnemies, right before a
        // boss spawns) previously never called OnDeathStop() at all, so any enemy wiped mid-attack left its
        // own windup indicator/fill stuck showing forever — confirmed live via a Leaper wiped mid-Leaping,
        // whose indicator (already unparented to world root, see above) had nothing left to ever hide it.
        // isDead/wiped are mutually exclusive for one life (see EnemyBaseState.wiped's own doc comment), so
        // sharing _deathStopped as the edge-detect latch for both is safe — never double-fires for the same
        // life.
        if ((viewB.isDead || viewB.wiped) && !_deathStopped)
        {
            _deathStopped = true;
            OnDeathStop();
        }

        bool shouldHide = viewB.isDead && viewB.deathHoldCount <= 0;
        if (shouldHide != _hiddenOnDeath)
        {
            _hiddenOnDeath = shouldHide;
            if (_renderers != null)
                foreach (var r in _renderers)
                    if (r != null) r.enabled = !shouldHide;
            if (_hurtbox != null) _hurtbox.enabled = !shouldHide;
            // Not a flat !shouldHide like _hurtbox — contactColliderEnabled is a per-enemy authored toggle
            // (some enemies never deal contact damage at all), so restoring on an un-hide has to respect it
            // rather than forcing this back on unconditionally.
            if (_contactDamageCollider != null) _contactDamageCollider.enabled = shouldHide ? false : contactColliderEnabled;
        }

        // Same instant/predicted hide as the isDead block above, for SilentDespawn — no deathHoldCount
        // gating at all (a wipe has no hold concept), and deliberately no red-paint/flash to go with it
        // (see SilentDespawn's own doc comment — this should read as a clean vanish, not a death).
        if (viewB.wiped != _hiddenOnWipe)
        {
            _hiddenOnWipe = viewB.wiped;
            if (_renderers != null)
                foreach (var r in _renderers)
                    if (r != null) r.enabled = !viewB.wiped;
            if (_hurtbox != null) _hurtbox.enabled = !viewB.wiped;
            if (_contactDamageCollider != null) _contactDamageCollider.enabled = viewB.wiped ? false : contactColliderEnabled;
        }

        // Stay red for as long as this enemy is dead — not just kill-launch specifically, and not just a
        // brief per-hit flash. Tied to viewB.isDead broadly (not viewB.killLaunchActive) so ANY current or
        // future on-death-hold effect (kill-launch, or anything else that later uses the same generic
        // RequestDeathHold()/deathHoldCount mechanism to linger a visible corpse) gets the same "stay red the
        // whole time" treatment automatically, with no per-effect-specific check needed here. Same instant/
        // predicted timing as the hide block above (viewState, not verified) since this is purely cosmetic.
        // The "paint red" side is continuously enforced every frame isDead is true, NOT just once on the
        // edge — the flash-revert block below can occasionally lose a same-tick ordering race against
        // OnEnemyDeath's dispatch (see its own comment) and revert to _originalColors while still dead;
        // painting red only once on the edge would leave that mistake uncorrected until this enemy is
        // eventually hidden/deleted. Continuous painting here means any such mistake self-corrects on the
        // very next frame instead. The revert side stays edge-detected (fires once, when isDead actually
        // goes false — a misprediction correction, see the isDead-hide block's own comment) since nothing
        // else contends for this enemy's color while it's alive.
        if (viewB.isDead)
        {
            if (_renderers != null)
                foreach (var r in _renderers)
                    if (r != null && r.material != null) r.material.color = Color.red;
        }
        // !_isFlashing here too — without it, a brand new hit landing on the exact same frame a death
        // misprediction gets corrected (isDead flips back to false) could have its fresh TriggerFlash() red
        // silently overwritten by this revert later the same frame (TriggerFlash() runs earlier, from the
        // attacking weapon's LateSimulate; this runs later, in this enemy's own UpdateView). If a flash is
        // already active, it already owns the color and will revert itself correctly on its own schedule
        // (its own revert check now correctly sees !viewB.isDead once this fires) — so just leave it alone.
        else if (_wasDeadForColorLastFrame && !_isFlashing && _renderers != null)
        {
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = _originalColors[i];
        }
        _wasDeadForColorLastFrame = viewB.isDead;

        // Folded in from a separate plain Update() — see TriggerFlash()'s own doc comment for the shared-
        // timer reasoning. Real Time.deltaTime, not tick-based delta: pure cosmetic flash, doesn't need to
        // be resimulation-safe since TriggerFlash() itself is only ever called once per real hit (gated by
        // the attacker's own PredictedEvent, not by anything in here). Runs from UpdateView instead of a
        // separate Update() purely for consolidation — same per-frame cadence either way, and this keeps
        // every cosmetic view-sync for this enemy in one place.
        if (_isFlashing && !paused)
        {
            _flashTimer -= Time.deltaTime;
            if (_flashTimer <= 0f)
            {
                _isFlashing = false;

                // Skip the revert if this enemy is dead now — TriggerHitFeedback's own !B.isDead guard
                // stops a NEW flash from ever starting once dead, but a flash from an earlier (non-lethal)
                // hit can still be mid-decay when a LATER hit kills this enemy before that timer runs out
                // (ordinary multi-hit-in-quick-succession, e.g. a multi-shot/AoE landing several hits within
                // one ~0.08s window). Since isDead is real predicted state (not gated behind server
                // verification the way killLaunchActive specifically is), this check sees the death
                // immediately, no ordering ambiguity to worry about here. Without this, that stale in-flight
                // flash would revert the corpse back to its original color moments after it died, fighting
                // the continuous isDead-driven red-paint above — which repaints every frame regardless, so
                // even if this guard were somehow wrong the mistake would self-correct next frame anyway,
                // but this avoids the flicker outright. Reads viewB (not B, which the old separate Update()
                // used) to match every other isDead check in this same method.
                if (!viewB.isDead && _renderers != null)
                    for (int i = 0; i < _renderers.Length; i++)
                        if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = _originalColors[i];
            }
        }

        // Yellow "true stun" tint — see EnemyBaseState.trueStunTimer's own doc comment for why this is
        // separate from ordinary knockback (which also ticks the shared stunTimer, but never trueStunTimer).
        // Dead-red and a fresh hit-flash both take priority: this only paints yellow when neither of those
        // is already claiming the color this frame, checked AFTER the flash-timer block above so a flash
        // that just expired this same tick is already reflected in _isFlashing. Continuously repainted every
        // frame it's active (not edge-triggered), same self-correcting reasoning as the dead-red paint above.
        // orbitFrozen (2026-09-21) also paints this tint — a held pleb visually reads as "stunned" while its
        // OWNER's own stun is what's actually freezing it (see IEnemyPD.SetOrbitFrozen's own doc comment);
        // this enemy's own trueStunTimer is untouched by that, so it needs the explicit OR here.
        bool wantStunTint = !viewB.isDead && !_isFlashing && (viewB.trueStunTimer > 0f || viewB.orbitFrozen);
        if (wantStunTint)
        {
            if (_renderers != null)
                foreach (var r in _renderers)
                    if (r != null && r.material != null) r.material.color = Color.yellow;
        }
        // Only reverts once the enemy is neither dead nor flashing — if either of those just started this
        // same frame, that block already owns the color (red) and this must not stomp it back to original.
        else if (_wasStunTintedLastFrame && !viewB.isDead && !_isFlashing && _renderers != null)
        {
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = _originalColors[i];
        }
        _wasStunTintedLastFrame = wantStunTint;

        // Death is state-critical — only act on server-confirmed dead.
        if (verified.HasValue)
        {
            var v = verified.Value;
            ref var vb = ref GetBase(ref v);

            if (vb.isDead && !_wasDeadLastFrame)
            {
                // See Die()'s own comment for why these moved here — plain static fields, only safe to
                // mutate off a fact that's settled exactly once (server-confirmed dead, edge-detected),
                // never from Simulate()-context where a replay would re-run this same tick's Die() call
                // again and re-decrement.
                ActiveCount--;
                AdjustCategoryCount(spawnCategory, -1);

                CharacterStatsPD.All.TryGetValue(vb.killerOwner, out var killer);
                CharacterStatsPD.RaiseOnEnemyDeath(killer, this);
                if (GameManagerPD.instance != null) GameManagerPD.instance.AwardUpgradePoints(vb.scaledPointsValue);
            }

            // Deletion re-checked every tick once verified-dead (not just on the edge above) so it
            // naturally waits out however long any real hold takes to clear, however many ticks that is.
            if (vb.isDead && vb.deathHoldCount <= 0)
                hierarchy.Delete(this);

            // Mirrors the isDead block above exactly, minus every consequence — see SilentDespawn's own
            // doc comment. No death-hold concept at all, so deletion isn't gated on anything beyond wiped
            // itself once verified.
            if (vb.wiped && !_wasWipedLastFrame)
            {
                ActiveCount--;
                AdjustCategoryCount(spawnCategory, -1);
            }
            if (vb.wiped)
                hierarchy.Delete(this);
            _wasWipedLastFrame = vb.wiped;

            _wasDeadLastFrame = vb.isDead;
            _verifiedHealth   = vb.health;
            _verifiedEnrageCountdown = vb.enrageCountdown;
        }
    }

    // ── Movement ───────────────────────────────────────────────────────────

    // Closest-living-player scan — O(players), not O(enemies), so cheap even called from every enemy at
    // once, but still not something to run every tick for every enemy (see Simulate()'s own comment on
    // when this gets called). Subclasses with a discrete attack-routine state machine call this again
    // themselves right as they return to idle/Moving (e.g. SpearGloomPD.EnterMoving), so a routine's tail
    // end reprioritizes onto whoever's closest now instead of sitting on the target it started against.
    // Invisible players are never valid candidates at all. Among the rest, a taunting player is always
    // preferred over a non-taunting one (closest-taunting wins if multiple are taunting); only falls back to
    // closest-overall when nobody valid is taunting. Leaves _target/_targetStats untouched (not nulled) if
    // no valid candidate exists at all, same as before this change — callers already handle a stale/dead
    // _targetStats by calling this again next tick.
    protected virtual void FindClosestTarget()
    {
        // Skip the distance-check loop below entirely when nobody in the whole game is even a legal
        // candidate right now (e.g. the last player standing is invisible-and-not-taunting) — this is the
        // common case a null-target enemy's every-tick retry (see EnemyBasePD.Simulate()'s own comment on
        // that fallback) would otherwise burn a full scan on for nothing, every tick, for every such enemy,
        // for however long that window lasts. See CharacterStatsPD.AnyValidTargetExists's own doc comment.
        // NOTE (2026-08-30): this gate is still player-only — it only tracks CONNECTED PLAYERS' own
        // death/invisibility state, not FriendlyTargetRegistryPD.All generically. Harmless today (no
        // non-player IFriendlyPD exists), but once one does, this could wrongly skip the scan below in a
        // scenario where every player is dead/invisible but a valid NPC ally is still alive — would need
        // its own generalized refresh (mirroring RefreshAnyValidTargetExists but over the full registry)
        // before that's a real concern.
        if (!CharacterStatsPD.AnyValidTargetExists) return;

        float nearestAny = float.MaxValue;
        Transform anyTarget = null;
        IFriendlyPD anyStats = null;

        float nearestTaunting = float.MaxValue;
        Transform tauntingTarget = null;
        IFriendlyPD tauntingStats = null;

        // FriendlyTargetRegistryPD.All (2026-08-30, was CharacterStatsPD.All.Values) — targets any
        // IFriendlyPD, not just connected players.
        foreach (var p in FriendlyTargetRegistryPD.All)
        {
            // Taunt trumps invisible — an invisible-but-taunting player is still a valid (in fact preferred)
            // candidate, only a non-taunting invisible player is excluded entirely.
            if (p.IsDead || (p.IsInvisible && !p.IsTaunting)) continue;
            float dist = Vector3.Distance(transform.position, p.transform.position);
            if (dist < nearestAny)
            {
                nearestAny = dist;
                anyTarget = p.transform;
                anyStats = p;
            }
            if (p.IsTaunting && dist < nearestTaunting)
            {
                nearestTaunting = dist;
                tauntingTarget = p.transform;
                tauntingStats = p;
            }
        }

        if (tauntingTarget != null)
        {
            _target = tauntingTarget;
            _targetStats = tauntingStats;
        }
        else if (anyTarget != null)
        {
            _target = anyTarget;
            _targetStats = anyStats;
        }
    }

    // Scratch buffer for FindRandomTarget below — cleared and reused every call, never left holding
    // references between calls, same zero-alloc convention as ShooterGloomPD's own _multishotAngles. Static
    // (not per-instance) is safe since each call runs synchronously start-to-finish within one enemy's own
    // Simulate()-phase call, never reentrantly. One buffer per closed generic TState (a C# generic-statics
    // quirk — each concrete enemy type gets its own copy of any static field declared on this generic base),
    // not a single buffer shared across every enemy type — harmless, just a handful of small lists instead
    // of exactly one.
    private static readonly List<IFriendlyPD> _randomTargetScratch = new();

    // Alternative to FindClosestTarget() above for enemies that should pick randomly instead of by
    // proximity — bosses, per design (a boss shouldn't feel "leashed" to whoever's nearest the way a normal
    // enemy does), but available to any future non-boss enemy that wants the same random-pressure feel too.
    // No range restriction, matching FindClosestTarget's own unrestricted scan. Still respects invisibility
    // (excluded unless taunting, same rule as FindClosestTarget) and taunting (a taunting player is always
    // eligible, overriding BOTH invisibility AND the "not the last target" exclusion below — matches
    // FindClosestTarget's own "taunt trumps everything" convention). Deliberately does NOT keep
    // FindClosestTarget's own "closest" tie-break for multiple simultaneous taunters either — picks randomly
    // among them too, for consistency with this method's whole "random, not closest" philosophy. This is a
    // separate method, not an override of FindClosestTarget — callers explicitly opt into it (e.g.
    // KingSpikePD.FireSpikeBurst) wherever they'd otherwise call FindClosestTarget; both remain available
    // side by side, and calling this doesn't change what FindClosestTarget itself does for anyone else.
    protected void FindRandomTarget()
    {
        if (!CharacterStatsPD.AnyValidTargetExists) return;

        // FriendlyTargetRegistryPD.All (2026-08-30, was CharacterStatsPD.All.Values) — targets any
        // IFriendlyPD, not just connected players.
        _randomTargetScratch.Clear();
        foreach (var p in FriendlyTargetRegistryPD.All)
            if (!p.IsDead && p.IsTaunting) _randomTargetScratch.Add(p);

        if (_randomTargetScratch.Count == 0)
        {
            // Nobody taunting to override anything — exclude whoever we were just targeting (the actual
            // "not the last target" request), still respecting invisibility since nothing here overrides it.
            foreach (var p in FriendlyTargetRegistryPD.All)
                if (!p.IsDead && !p.IsInvisible && p != _targetStats) _randomTargetScratch.Add(p);
        }

        if (_randomTargetScratch.Count == 0)
        {
            // Excluding the last target left nobody — if THEY'RE still valid, they're the only option left,
            // so re-pick them rather than going targetless. Matches FindClosestTarget's own "leave untouched
            // if nothing found" fallback for the case even this comes up empty.
            if (_targetStats != null && !_targetStats.IsDead && !_targetStats.IsInvisible)
                _randomTargetScratch.Add(_targetStats);
        }

        if (_randomTargetScratch.Count == 0) return;

        var chosen = _randomTargetScratch[GameRandom.instance.Next(_randomTargetScratch.Count)];
        _target = chosen.transform;
        _targetStats = chosen;

        // TODO: signal to `chosen` that this enemy is now targeting them, once the planned player-side
        // "boss is targeting you" indicator exists.
    }

    // Called by EnemySpawnerPD (the enemy-targeting hub) the instant any friendly's taunt state changes —
    // immediate reaction, not polled. Taunt starting overrides everything (even that friendly's own
    // invisibility) — every enemy snaps straight onto them. Taunt ending only matters if that friendly is
    // ALSO currently invisible: they lose their taunt-granted immunity, so anyone targeting them needs to
    // retarget away right now instead of waiting to notice later. IFriendlyPD (2026-08-30, was
    // CharacterStatsPD) — see IEnemyPD.OnPlayerTauntChanged's own doc comment.
    public void OnPlayerTauntChanged(IFriendlyPD player, bool isTaunting)
    {
        if (isTaunting)
        {
            if (player.IsDead) return;
            _target = player.transform;
            _targetStats = player;
            return;
        }

        if (player.IsInvisible) RetargetAwayFrom(player);
    }

    // Called by EnemySpawnerPD the instant any friendly's invisible state changes. Becoming visible again
    // never needs a forced reaction (nobody was wrongly avoiding them). Becoming invisible only matters to
    // enemies currently targeting them, and only if they're not ALSO taunting (taunt trumps invisible).
    // IFriendlyPD (2026-08-30, was CharacterStatsPD) — see IEnemyPD.OnPlayerInvisibleChanged's own doc comment.
    public void OnPlayerInvisibleChanged(IFriendlyPD player, bool isInvisible)
    {
        if (!isInvisible || player.IsTaunting) return;
        RetargetAwayFrom(player);
    }

    // Shared by both handlers above — only acts if THIS enemy is actually targeting the now-invalid player.
    // Explicitly nulls _target/_targetStats first (unlike FindClosestTarget's own "leave untouched if
    // nothing found" behavior) so that if no valid replacement exists, the target genuinely becomes null.
    // Never interrupts an attack already in progress — see CaptureAttackAnchor's own doc comment for why
    // reassigning _target here is always safe to do immediately, mid-attack included. HandleMovement already
    // stops moving on a null target for anyone idling in Moving; a subclass mid-attack-routine picks up the
    // null/changed _target at its own next between-attacks checkpoint, not here.
    private void RetargetAwayFrom(IFriendlyPD invalidPlayer)
    {
        if (_targetStats != invalidPlayer) return;
        _target = null;
        _targetStats = null;
        FindClosestTarget();
    }

    // Radial strafe (2026-09-30) — an enemy at OR CLOSER than preferred range orbits its target (direction
    // rolled once per spawn — see EnemyBaseState.strafeClockwise's own doc comment; a fixed direction for
    // every enemy looked unnatural) instead of standing fully still — replaces the old backpedal/retreat
    // logic entirely (see the "too close" comment above HandleMovement's own preferredRange block), most
    // visible on long-lived enemies like King Orbiter. Per-prefab opt-out via canStrafe, for enemies that
    // should still hold position. A flat, LOCAL multiplier applied only to this force's own magnitude below —
    // deliberately NOT written back into scaledMoveSpeed/EffectiveMoveSpeed itself, so nothing else that
    // reads EffectiveMoveSpeed (dash speed multipliers, etc.) is affected by the strafe being slow. Not
    // exposed for per-prefab tuning — same "hidden movement infrastructure constant" convention
    // SeparationForceBaselineMoveSpeed already uses.
    private const float StrafeSpeedPercent = 0.25f;

    protected virtual void HandleMovement(ref TState state, float delta)
    {
        if (_target == null) return;

        ref var b = ref GetBase(ref state);
        // See IEnemyPD.SetMovementDisabled's own doc comment — a hard gate on the normal approach movement
        // this method drives (both the facing-rotation and the AddForce calls below), set by
        // another entity (typically a different enemy) rather than this enemy's own state machine. Attack
        // sequences already go kinematic/collider-disabled through their own separate mechanisms during a
        // windup/dash/etc., so this is specifically for "keep this enemy otherwise fully alive and attacking
        // normally, just don't let it walk" — a different, narrower effect than Stun()/Interrupt().
        if (b.movementDisabled) return;
        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0;
        Vector3 dir = toTarget.normalized;

        // Facing always tracks the real target direction, completely unaffected by bounds-restriction below
        // — only the movement FORCE (approachDir/retreatDir, computed separately below) ever gets redirected.
        // An enemy that's currently forced to strafe still visibly faces its target throughout, same as it
        // always did.
        if (dir != Vector3.zero)
        {
            Quaternion targetRotation = Quaternion.LookRotation(dir);
            _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, targetRotation, 10f * delta));
        }

        float distToTarget = Vector3.Distance(transform.position, _target.position);

        // Bounds-restriction/strafe system removed entirely (2026-08-28) — the general-purpose "always find
        // a guaranteed way back to safety" version of this problem turned out to have no real fix that was
        // proportionate to build: the strafe formula's tangential+radial blend had no actual pull back toward
        // the boundary at all (confirmed live — an enemy could slide tangentially along a wall corner-to-
        // corner while staying outside the whole time, since neither the tangential term nor the target-
        // relative radial term were ever guaranteed to reduce distance from the wall). Rather than keep
        // hardening a system fighting arbitrary arena geometry, the call was to drop it project-wide — see
        // PlayableAreaPD.TryGetInwardSteering, the general-purpose steering primitive this used to lean on.
        // That method was itself removed entirely 2026-09-17 (confirmed dead, zero remaining callers,
        // alongside the whole innerObstructions system it was built to service — see PlayableAreaPD's own
        // class doc comment).
        //
        // Backpedal removed project-wide (2026-09-22) — the SAME root cause this bounds-restriction system
        // above was fighting (an enemy backing directly away from its target, with no awareness of what's
        // behind it, could walk itself out of bounds or into an obstacle) applied just as much to the
        // preferredRange "too close, back away" retreat force as it did to the old strafe system, just for
        // ranged/kiting enemies specifically rather than every enemy. Rather than reintroduce some bespoke
        // safety check just for retreating, the retreat force is gone entirely: an enemy that's too close to
        // its preferred range now simply stands still (still fully facing/tracking the target, still able to
        // attack) instead of backing away. "Too far, approach" is completely untouched. backpedalDisabled/
        // backpedalSpeedDivisor are removed too — standing still is now the only "too close" behavior, so
        // there's nothing left to opt in or out of per-prefab.
        Vector3 approachDir = dir;

        if (preferredRange > 0)
        {
            // Strafe now covers the WHOLE "at or closer than preferred range" zone (2026-09-30 correction —
            // originally only the narrow settled band, per a miscommunication; this is what actually replaces
            // the old backpedal/retreat logic the class doc comment above describes) — rangeToleranceClose no
            // longer creates a distinct "too close" behavior zone (left declared, just unused here, same
            // "leave it authored, drop the read" precedent other retired-but-not-deleted tier fields this
            // session already use). atPreferredRange is now PURELY a far-boundary hysteresis flag: tight
            // entry threshold (exactly preferredRange) while still closing in, wider exit threshold
            // (+rangeToleranceFar) once settled, so approach<->strafe doesn't flicker right at the edge — not
            // a gate on whether strafing happens at all.
            float approachThreshold = b.atPreferredRange ? preferredRange + rangeToleranceFar : preferredRange;

            if (distToTarget > approachThreshold)
            {
                b.atPreferredRange = false;
                _rigidbody.AddForce(approachDir * (EffectiveMoveSpeed * delta * 5f), ForceMode.VelocityChange);
            }
            else
            {
                if (!b.atPreferredRange && Mathf.Abs(distToTarget - preferredRange) <= 0.1f)
                    b.atPreferredRange = true;

                if (canStrafe)
                {
                    // Tangent: radiusDir (target -> enemy) crossed with Vector3.up gives the counterclockwise
                    // direction; the clockwise case just negates it. b.strafeClockwise is rolled once per
                    // spawn (LateAwake) via GameRandom, not per-tick — a fixed direction for every enemy
                    // looked unnatural (everyone orbiting the same way), and re-rolling live would need to be
                    // simulation-safe anyway. Pure function of the current radius vector otherwise —
                    // deterministic, no per-machine state, safe inside Simulate (see
                    // feedback_no_race_conditions_or_outside_simulation_state memory).
                    Vector3 radiusDir = -dir;
                    Vector3 strafeDir = Vector3.Cross(radiusDir, Vector3.up).normalized;
                    if (b.strafeClockwise) strafeDir = -strafeDir;
                    _rigidbody.AddForce(strafeDir * (EffectiveMoveSpeed * StrafeSpeedPercent * delta * 5f), ForceMode.VelocityChange);
                }
            }
        }
        else
        {
            _rigidbody.AddForce(dir * (EffectiveMoveSpeed * delta * 5f), ForceMode.VelocityChange);
        }
    }

    // ── Declumping ────────────────────────────────────────────────────────────

    // Ticks the per-instance stagger timer and, every SeparationCheckInterval ticks, recomputes this
    // enemy's own push-away-from-neighbors vector — cached in EnemyBaseState.separationPush and re-applied
    // here as a small continuous force every tick in between checks (see that field's own doc comment for
    // why it lives in State). Deliberately not every enemy re-checking every tick: with up to
    // maxNormalEnemies (plus elites/bosses) potentially alive and overlapping at once, that would be real,
    // avoidable physics-query cost that scales with total enemy count instead of staying flat — the random
    // per-instance seed on separationCheckTimer (see LateAwake()) spreads the population's checks evenly
    // across ticks instead of all landing on the same one.
    private void HandleSeparation(ref EnemyBaseState b, float delta)
    {
        // Kinematic enemies are exempt in both directions (2026-08-12) — as a PUSHEE, a kinematic window
        // (windup/charge/leap, held/orbiting, several subclasses have one) can't take a velocity-change
        // force at all, so skipping the whole recompute here is just cheaper than computing a push vector
        // that would never get applied anyway; separationCheckTimer simply pauses while kinematic and
        // resumes counting down from wherever it left off once this enemy stops being kinematic, same
        // "harmless staleness" tolerance the whole recompute-every-N-ticks design already accepts. As a
        // PUSHER, see RecomputeSeparationPush's own IEnemyPD.IsKinematic check below — a kinematic enemy is
        // skipped as a neighbor by everyone ELSE's own separation checks too.
        //
        // No valid target is exempt too, as a PUSHEE only (2026-09-01) — HandleMovement no-ops whenever
        // _target is null (every player currently invisible-and-not-taunting, or dead), so an idle enemy
        // with nothing to chase had nothing counteracting the separation force either, reading as aimless
        // sliding-in-place instead of natural pack jostling (misdiagnosed once already as
        // SeparationRadiusPerSize itself being the problem — see that field's own doc comment). Same
        // "pauses wherever it left off, resumes once a target exists again" staleness tolerance as the
        // kinematic case above. Deliberately NOT exempt as a neighbor other enemies push off of — an idle,
        // targetless enemy is still a real physical obstacle for a DIFFERENT enemy that still has a target
        // and is actively moving through the area; only this enemy's own reaction is suppressed, same
        // asymmetry precedent as IsBeingHeld/other partial exemptions elsewhere in this file.
        if (_rigidbody == null || _rigidbody.isKinematic || _target == null) return;

        b.separationCheckTimer--;
        if (b.separationCheckTimer <= 0)
        {
            b.separationCheckTimer = SeparationCheckInterval;
            RecomputeSeparationPush(ref b);
        }

        if (b.separationPush != Vector3.zero)
            _rigidbody.AddForce(b.separationPush * delta, ForceMode.VelocityChange);
    }

    // Any enemy counts as a neighbor to push away from here, not just this enemy's own type — pushing off of
    // every enemy type equally is the intended behavior, not something that needs filtering down to "my own
    // kind" specifically.
    private void RecomputeSeparationPush(ref EnemyBaseState b)
    {
        b.separationPush = Vector3.zero;

        float radius = SeparationRadiusPerSize * size;
        if (radius <= 0f) return;

        // Shrink to the tighter near-player radius if an alive player is within NearPlayerDetectionRadiusPerSize
        // — lets multiple enemies still cram in around that player (only preventing gross overlap once that
        // close) instead of the full radius pushing them away from the player entirely. Checked against its
        // OWN fixed detection distance (2026-09-01, was the live `radius` variable itself — see
        // NearPlayerDetectionRadiusPerSize's own doc comment for why that coupling was removed), not
        // whatever SeparationRadiusPerSize currently is, so retuning the general declump radius never again
        // silently shifts how far away a player can be and still count as "nearby" for this purpose.
        if (IsAlivePlayerWithinRange(NearPlayerDetectionRadiusPerSize * size)) radius = SeparationRadiusPerSizeNearPlayer * size;

        int n = Physics.OverlapSphereNonAlloc(transform.position, radius, _separationOverlapResults, CombatLayerMasks.EnemyHurtboxMask);
        if (n <= 0) return;

        Vector3 combined = Vector3.zero;
        bool anyNeighbor = false;

        for (int i = 0; i < n; i++)
        {
            var other = _separationOverlapResults[i].GetComponent<IEnemyPD>();
            // IsKinematic exemption (2026-08-12) — kinematic enemies are skipped as pushers too, not just
            // as pushees (see HandleSeparation's own comment for the pushee side). A windup/charge/held
            // enemy shouldn't be shoving its non-kinematic neighbors around just because it's still
            // physically present.
            if (other == null || other._isDead || other.transform == transform || other.IsKinematic) continue;

            Vector3 away = transform.position - other.transform.position;
            away.y = 0f;
            float dist = away.magnitude;

            Vector3 pushDir;
            float weight;
            if (dist < 0.001f)
            {
                // Exact/near-exact overlap — "away from them" is undefined (zero-length vector), so fall
                // back to this enemy's own fixed personal angle instead. Without this, a perfect N-way stack
                // would have every member computing the same undefined zero vector forever and never
                // actually separate.
                pushDir = Quaternion.Euler(0f, b.personalPushAngle, 0f) * Vector3.forward;
                weight = 1f;
            }
            else
            {
                pushDir = away / dist;
                // Stronger the closer/more-overlapping they are (full weight at 0 distance, fading to 0 right
                // at radius) — the worst case (identical coordinates) needs to resolve FASTEST, not slowest.
                weight = Mathf.Clamp01(1f - dist / radius);
            }

            combined += pushDir * weight;
            anyNeighbor = true;
        }

        if (!anyNeighbor) return;
        // Clamped, not left to grow with neighbor count — an enemy buried in the middle of a deep pile
        // touches many neighbors at once, and summing every one of their unit push vectors uncapped would
        // scale the resulting force with however many are found instead of staying a bounded "how hard am I
        // being pushed" value.
        if (combined.sqrMagnitude > 1f) combined.Normalize();
        // Scaled by EffectiveMoveSpeed/SeparationForceBaselineMoveSpeed — see that field's own doc comment
        // for why (keeps this force proportional to HandleMovement's own competing pull-toward-target force,
        // regardless of this enemy's speed tier or an active slow).
        b.separationPush = combined * SeparationForce * (EffectiveMoveSpeed / SeparationForceBaselineMoveSpeed);
    }

    // Alive-only (a dead player's corpse shouldn't shrink the radius — nothing left to "allow crowding
    // around"), plain flat distance check regardless of invisibility/taunt — this is about physical crowding,
    // not targeting eligibility. Cheap: at most 4 players, called once every SeparationCheckInterval ticks
    // per enemy, same cost class as every other per-enemy CharacterStatsPD.All iteration in this file.
    private bool IsAlivePlayerWithinRange(float radius)
    {
        float sqrRadius = radius * radius;
        // FriendlyTargetRegistryPD.All (2026-08-30, was CharacterStatsPD.All.Values).
        foreach (var p in FriendlyTargetRegistryPD.All)
        {
            if (p.IsDead) continue;
            if ((p.transform.position - transform.position).sqrMagnitude <= sqrRadius) return true;
        }
        return false;
    }

    // ── Dash helpers (for subclasses) ──────────────────────────────────────
    // speed here is EffectiveMoveSpeed * a subclass's own dashSpeedMultiplier (see EffectiveMoveSpeed's own
    // doc comment for why this reads EffectiveMoveSpeed and not baseMoveSpeed as of 2026-08-13) — dash speed
    // scales with move speed ONLY now (2026-09-23 fix — HandleDash/HandleOffsetChase used to ALSO multiply
    // this live by EffectiveAttackSpeed every tick, dual-scaling dash speed against both stats at once; per
    // explicit design, windup duration is the one that scales with attack speed, dash speed scales with
    // move speed, and neither should scale with both). Baked in once at dash-start — a slow or a new enrage
    // stack landing MID-dash doesn't retroactively change an already-launched dash's speed, only the next
    // one (this was already true for the move-speed portion before this fix; now it's true for the whole
    // speed value, since there's no live-reactive attack-speed term left to be the exception).
    //
    // StartDashToTarget (dash to a point snapshotted from the target's position ONCE at dash-start) was
    // removed here 2026-08-26 — its one caller, SpearGloomPD, migrated to StartOffsetChase below, which
    // continuously re-tracks the target instead of snapshotting a point a fast player could just outrun.
    // StartDashToPoint stays — genuinely different shape, a caller-computed FIXED world point (retreat
    // points, charge endpoints), still actively used by MortarGloomPD/KingSpikePD/ShooterGloomPD/
    // BlitzGloomPD.
    protected void StartDashToPoint(ref TState state, float speed, Vector3 destination)
    {
        ref var b = ref GetBase(ref state);
        Vector3 dir = destination - transform.position;
        dir.y = 0f;
        dir.Normalize();
        b.isDashing = true;
        b.dashDirection = dir;
        b.dashDestination = destination;
        b.dashSpeed = speed;
        if (_physicsCollider != null) _physicsCollider.enabled = false;
    }

    protected void HandleDash(ref TState state, float delta)
    {
        ref var b = ref GetBase(ref state);
        Vector3 toDestination = b.dashDestination != Vector3.zero
            ? b.dashDestination - transform.position
            : (_target != null ? _target.position - transform.position : Vector3.zero);
        toDestination.y = 0f;

        if (Vector3.Dot(toDestination, b.dashDirection) <= 0f || b.isDead)
        {
            b.isDashing = false;
            _rigidbody.velocity = Vector3.zero;
            if (_physicsCollider != null && !b.isDead) _physicsCollider.enabled = true;
            return;
        }

        // Baked in at dash-start, move-speed-only — see StartDashToPoint's own doc comment.
        _rigidbody.velocity = b.dashDirection * b.dashSpeed;
        _rigidbody.MoveRotation(Quaternion.LookRotation(b.dashDirection));
    }

    // Chases a target-relative offset POSITION, live-tracked while the committed target stays valid and
    // frozen at its last-known position the instant it isn't (dead, or invisible-without-taunting) — see
    // _offsetChaseTrackedTarget's own doc comment for why this reads a frozen reference captured here,
    // decoupled from the shared _target/_targetStats pair (which the old dash-to-a-fixed-point shape's own
    // problem — see EnemyBaseState.isOffsetChasing's own doc comment — was ALSO about, just for the position
    // rather than the reference: a fast player could simply outrun a snapshotted destination). speed follows
    // the same move-speed-only convention as StartDashToPoint's own speed param above (2026-09-23 — see its
    // own doc comment for why this no longer also applies EffectiveAttackSpeed).
    //
    // offsetDirectionDegrees is measured relative to the dasher→target bearing AT THIS EXACT MOMENT, rotated
    // around the world-up axis, and LOCKED into b.chaseLockedOffsetDir from here on (2026-08-26 fix — see
    // that field's own doc comment for the live-recompute instability this replaced). 0° locks onto
    // continuing straight past the target (e.g. DaggerGloomPD, ending up on its far side from wherever it
    // approached from); any other angle flanks to a side. Only the DIRECTION is locked here —
    // HandleOffsetChase still re-applies it against the tracked target's position every tick, so the
    // destination itself keeps tracking a moving target.
    //
    // Spear/King Spike's own "stop short, between dasher and target" case used to be the 180° case here
    // (dashed to `target.position - dir * stopDistance`, dir = dasher→target) but has since moved to the
    // no-angle overload just below (2026-09-03) — explicit design: since both are already dashing straight
    // at the target, they naturally end up roughly between their own approach path and the target
    // regardless, so locking a specific bearing was never actually necessary for that case. This
    // angle-locked overload now exists purely for DaggerGloomPD's 0° "continue past to the far side" case,
    // which genuinely does need a specific side (anywhere-within-radius isn't enough — it must be past).
    protected void StartOffsetChase(ref TState state, float speed, float offsetDistance, float offsetDirectionDegrees)
    {
        if (_target == null) return;
        ref var b = ref GetBase(ref state);
        b.isOffsetChasing = true;
        b.dashSpeed = speed;
        b.chaseOffsetDistance = offsetDistance;

        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f) toTarget = transform.forward;
        toTarget.Normalize();
        b.chaseLockedOffsetDir = Quaternion.AngleAxis(offsetDirectionDegrees, Vector3.up) * toTarget;

        // Commit to the current target for this whole chase — see _offsetChaseTrackedTarget's own doc
        // comment. _targetStats is guaranteed non-null here alongside _target (FindClosestTarget/
        // FindRandomTarget always set both together).
        _offsetChaseTrackedTarget = _targetStats;
        _offsetChaseTrackedLastKnownPosition = _targetStats.transform.position;

        if (_physicsCollider != null) _physicsCollider.enabled = false;
    }

    // No-angle variant (2026-09-03) — for callers that only care about ending up SOMEWHERE within
    // arriveRadius of the target, not any particular side/bearing (e.g. SpearGloomPD/KingSpikePD's own
    // "stop short, between dasher and target" 180° case — explicit design: since the dasher is already
    // moving straight at the target, it naturally ends up roughly between its own approach path and the
    // target regardless, so locking a specific angle was never actually necessary there). Sets
    // b.chaseLockedOffsetDir to Vector3.zero as a sentinel HandleOffsetChase reads to skip the angle offset
    // entirely and use chaseOffsetDistance as the arrival radius instead of the fixed
    // OffsetChaseArriveTolerance — safe/unambiguous since the angle-locked overload above always produces a
    // genuine unit vector, never zero. DaggerGloomPD's own 0° "continue past to the far side" case still
    // needs the angle-locked overload — that one's whole point IS ending up on a specific side.
    protected void StartOffsetChase(ref TState state, float speed, float arriveRadius)
    {
        if (_target == null) return;
        ref var b = ref GetBase(ref state);
        b.isOffsetChasing = true;
        b.dashSpeed = speed;
        b.chaseOffsetDistance = arriveRadius;
        b.chaseLockedOffsetDir = Vector3.zero;

        _offsetChaseTrackedTarget = _targetStats;
        _offsetChaseTrackedLastKnownPosition = _targetStats.transform.position;

        if (_physicsCollider != null) _physicsCollider.enabled = false;
    }

    // Small, not exposed — the only 3 params either caller needs are speed/distance/direction (above);
    // arrival tolerance is an implementation detail, not something either enemy needs to tune per-attack.
    private const float OffsetChaseArriveTolerance = 0.25f;

    protected void HandleOffsetChase(ref TState state, float delta)
    {
        ref var b = ref GetBase(ref state);
        if (_offsetChaseTrackedTarget == null || b.isDead)
        {
            b.isOffsetChasing = false;
            _rigidbody.velocity = Vector3.zero;
            if (_physicsCollider != null && !b.isDead) _physicsCollider.enabled = true;
            return;
        }

        // Refresh the tracked position while still valid; freeze (skip this) the instant it isn't — same
        // live-then-freeze shape every other windup-tracking mechanism in this codebase already uses.
        // !IsDashingOrLeaping added 2026-09-03: a dashing/leaping target moves fast enough that this
        // chase's own speed can never converge on it (constantly re-aiming at a destination that outruns
        // every step), producing visible jitter/never-arriving behavior until the burst ends. Freezing
        // here instead means the chase commits to a plausible intercept point instead — same resume-on-
        // condition-clearing behavior as the invisible case, since this re-evaluates every tick.
        if (!_offsetChaseTrackedTarget.IsDead && (!_offsetChaseTrackedTarget.IsInvisible || _offsetChaseTrackedTarget.IsTaunting) && !_offsetChaseTrackedTarget.IsDashingOrLeaping)
            _offsetChaseTrackedLastKnownPosition = _offsetChaseTrackedTarget.transform.position;

        // b.chaseLockedOffsetDir was fixed once, back at StartOffsetChase — never re-derived here, see that
        // field's own doc comment. Only the POSITION this resolves to tracks the target live (below).
        // Vector3.zero is the no-angle sentinel (see the 3-param StartOffsetChase overload's own doc
        // comment) — destination collapses to the tracked target's own position directly, and
        // chaseOffsetDistance is read as an arrival RADIUS below instead of a directional offset.
        bool noAngle = b.chaseLockedOffsetDir == Vector3.zero;
        Vector3 liveDestination = noAngle
            ? _offsetChaseTrackedLastKnownPosition
            : _offsetChaseTrackedLastKnownPosition + b.chaseLockedOffsetDir * b.chaseOffsetDistance;

        Vector3 toDestination = liveDestination - transform.position;
        toDestination.y = 0f;
        float dist = toDestination.magnitude;

        float arriveTolerance = noAngle ? b.chaseOffsetDistance : OffsetChaseArriveTolerance;
        if (dist <= arriveTolerance)
        {
            b.isOffsetChasing = false;
            _rigidbody.velocity = Vector3.zero;
            if (_physicsCollider != null) _physicsCollider.enabled = true;
            return;
        }

        Vector3 moveDir = toDestination / dist;
        // Move-speed-only, baked in at chase-start — see StartOffsetChase's own doc comment (2026-09-23 fix).
        // Still a local `speed` variable, not read straight off b.dashSpeed below, since the overshoot clamp
        // right after needs to modify THIS tick's value without touching the stored one for next tick.
        float speed = b.dashSpeed;
        // Capped so this tick's step never overshoots liveDestination (2026-08-31 fix — the "Spear/Dagger
        // vibrate in place after their target goes invisible mid-dash" bug). Unlike HandleDash (fixed
        // direction, stops via a Dot()-sign "already passed it" check — structurally overshoot-proof), this
        // chase re-aims moveDir at the live/frozen destination every single tick, so a full-speed step can fly
        // straight past a destination that isn't moving — then the very next tick re-aims moveDir the OTHER
        // way and does the same thing in reverse, repeating every tick until fp noise happens to land inside
        // OffsetChaseArriveTolerance. A live, still-visible target rarely triggers this since liveDestination
        // itself keeps drifting each tick, giving the chase somewhere new to converge toward instead of
        // bouncing off a fixed point — but the instant the tracked target goes invisible mid-dash,
        // liveDestination freezes at their last-known position (see _offsetChaseTrackedLastKnownPosition's own
        // doc comment) and the chase starts oscillating around that exact spot. Clamping the SPEED USED THIS
        // TICK (never the stored b.dashSpeed itself, which stays untouched for the next tick) so the step
        // lands exactly AT the destination instead of past it removes the oscillation without touching arrival
        // detection at all, which stays magnitude-based and fires cleanly the very next tick either way.
        float maxStep = speed * delta;
        if (maxStep > dist) speed = dist / Mathf.Max(delta, 0.0001f);
        _rigidbody.velocity = moveDir * speed;
        _rigidbody.MoveRotation(Quaternion.LookRotation(moveDir));
    }

    protected Vector3 SnapFaceTarget(ref TState state)
    {
        if (_target == null) return Vector3.zero;
        Vector3 dir = _target.position - transform.position;
        dir.y = 0;
        if (dir == Vector3.zero) return Vector3.zero;
        dir.Normalize();
        _rigidbody.MoveRotation(Quaternion.LookRotation(dir));
        return dir;
    }

    // Overload taking an EXPLICIT target (2026-08-24) — for the "reset facing before starting a rate-capped
    // windup chase" primitive every rotation-rate-capped windup needs (BlitzGloomPD/KingSpikePD's own
    // ChargeWindup/Windup entry points): without an instant, uncapped re-face the MOMENT windup starts, the
    // rate cap alone often can't finish turning the body around in time if the target's now roughly behind
    // it (confirmed live — this boss visibly kept charging/blitzing in roughly the same stale direction
    // instead of properly re-aiming). Two things the plain (ref TState state) overload above can't do,
    // which is why this is a separate overload rather than an optional-arg change to it: (1) takes the
    // target explicitly instead of reading this enemy's own live _target — a caller using the "commit once,
    // keep reading that same reference" contract (CaptureAttackAnchor's own doc comment; e.g.
    // BlitzGloomPD._windupTrackedTarget/KingSpikePD._chargeWindupTrackedTarget) needs to snap toward its own
    // FROZEN snapshot, not whatever _target might have been reassigned to mid-windup via taunt/invisible-
    // retarget; (2) applies the same IsDead/IsInvisible-without-Taunting guard every other targeting check in
    // this codebase already uses, which the live-_target overload never had — respectInvisibility defaults
    // true, pass false only if a caller genuinely wants to ignore that (no current caller does). Returns
    // Vector3.zero on any guard failure/degenerate direction — same "caller only writes into its own
    // direction state field on a non-zero return" contract as the other overload.
    protected Vector3 SnapFaceTarget(IFriendlyPD target, bool respectInvisibility = true)
    {
        if (target == null) return Vector3.zero;
        if (respectInvisibility && (target.IsDead || (target.IsInvisible && !target.IsTaunting))) return Vector3.zero;

        Vector3 dir = target.transform.position - transform.position;
        dir.y = 0f;
        if (dir == Vector3.zero) return Vector3.zero;
        dir.Normalize();
        _rigidbody.MoveRotation(Quaternion.LookRotation(dir));
        return dir;
    }

    protected void FaceTargetSmooth(float delta)
    {
        if (_target == null) return;
        Vector3 dir = _target.position - transform.position;
        dir.y = 0;
        if (dir == Vector3.zero) return;
        _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir.normalized), 10f * delta));
    }

    // Call at the exact moment an "ongoing" attack begins (dash-to-target, or a windup entered directly
    // because already in range) — snapshots _target's CURRENT position so the rest of that attack can keep
    // reading it even if _target itself gets reassigned mid-attack (taunt override, or an invisible-retarget
    // finding a replacement). A taunt/invisible change during an ongoing attack still updates _target
    // immediately (so the NEXT attack in a chain uses it) but must never redirect the CURRENT one — see the
    // "ongoing attack must complete" requirement this exists for.
    protected void CaptureAttackAnchor(ref TState state)
    {
        if (_target != null) GetBase(ref state).attackAnchorPosition = _target.position;
    }

    // Companion to CaptureAttackAnchor, for the one call site that needs it: refreshing attackAnchorPosition
    // right as an offset-chase dash ends (SpearGloomPD/DaggerGloomPD's own dashJustEnded handling), so
    // Windup/EnterStab aim at wherever the dash actually chased to. Deliberately does NOT re-read live
    // _target/_targetStats the way CaptureAttackAnchor does — by the time a dash ends, RetargetAwayFrom may
    // have already nulled _target (a plain CaptureAttackAnchor call would then silently no-op, leaving the
    // STALE pre-dash anchor) or reassigned it to a brand new target (which would incorrectly redirect this
    // already-resolving attack onto them) — see _offsetChaseTrackedTarget's own doc comment. Reading the
    // chase's own frozen/live-tracked position instead sidesteps both failure modes: it's always exactly
    // where this specific dash was chasing, live if the target stayed valid the whole time, frozen at their
    // last-known position otherwise.
    protected void CaptureAttackAnchorFromOffsetChase(ref TState state)
    {
        GetBase(ref state).attackAnchorPosition = _offsetChaseTrackedLastKnownPosition;
    }

    // Anchor-based counterparts to SnapFaceTarget/FaceTargetSmooth — use these instead, everywhere facing
    // happens DURING an ongoing attack (after CaptureAttackAnchor already ran for it). The plain _target-
    // based versions above remain correct for facing that happens OUTSIDE an ongoing window (e.g. Spear's
    // BetweenStabs/PostStab facing, which is deliberately preparing for whatever the NEXT attack should use).
    protected Vector3 SnapFaceAnchor(ref TState state)
    {
        Vector3 anchor = GetBase(ref state).attackAnchorPosition;
        Vector3 dir = anchor - transform.position;
        dir.y = 0;
        if (dir == Vector3.zero) return Vector3.zero;
        dir.Normalize();
        _rigidbody.MoveRotation(Quaternion.LookRotation(dir));
        return dir;
    }

    protected void FaceAnchorSmooth(ref TState state, float delta)
    {
        Vector3 anchor = GetBase(ref state).attackAnchorPosition;
        Vector3 dir = anchor - transform.position;
        dir.y = 0;
        if (dir == Vector3.zero) return;
        _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir.normalized), 10f * delta));
    }

    protected bool TargetInRange()
    {
        if (_target == null) return false;
        return Vector3.Distance(transform.position, _target.position) <= preferredRange + rangeToleranceFar;
    }

    // Same validity rule FindClosestTarget uses to decide who's even a candidate (dead, or invisible without
    // taunt, is never valid) — but checked against the CURRENTLY-locked target instead of scanning for a new
    // one. Every multi-hit attack chain's own "Remaining > 0, keep going" continuation check (e.g.
    // LeaperGloomPD.EnterPostLand, SpearGloomPD's BetweenStabs gate) should also gate on this: an attack
    // already resolving must still complete uninterrupted (see CaptureAttackAnchor's own doc comment for that
    // contract — untouched by this), but deciding whether to chain into ANOTHER hit against a target that's
    // gone invisible-and-non-taunting since the last hit is exactly the same decision FindClosestTarget would
    // already make for a fresh acquisition, so a chain shouldn't get to skip it just because it's already
    // mid-routine. Without this, invisibility only ever stopped a chain from STARTING against someone, never
    // from continuing an already-started one against them.
    protected bool IsTargetStillValid =>
        _targetStats != null && !_targetStats.IsDead && (!_targetStats.IsInvisible || _targetStats.IsTaunting);

    // ── Combat ─────────────────────────────────────────────────────────────
    public virtual void TakeDamage(float damage, bool isCrit = false, PlayerID attackerOwner = default, bool isHighroll = false)
    {
        ref var b = ref B;
        if (b.isDead) return;
        if (b.health <= 0) return;
        // See IEnemyPD.IsInvulnerable's own doc comment — mirrors CharacterStatsPD.TakeDamage's own
        // identical invincible guard placement, before any mutation.
        if (b.invulnerable) return;

        b.killerOwner = attackerOwner;
        b.health      -= damage;

        // Execute — CharacterStatsPD.TakeDamage is the single choke point every damage source (any weapon,
        // Phantom Blade, onhit effect, AOE augment, etc.) already funnels through, so hooking it here
        // covers a crit (or, in the future, ANY hit — see CharacterStatsPD.executeThreshold's own doc
        // comment) from anything uniformly, with no per-call-site changes needed anywhere else. Bosses use
        // separately reduced thresholds (BossExecuteThreshold/BossCritExecuteThreshold, -80% each) instead
        // of the raw player-facing ones, so a boss doesn't get finished off nearly as easily as a normal/
        // elite enemy — each scaled down individually BEFORE combining below, not the other way around.
        // critExecuteThreshold combines with executeThreshold multiplicatively on a crit (2026-08-14 — was
        // a flat sum), not an independent trip-wire — same "never reach 100%" diminishing-returns shape as
        // AddExecuteThresholdPercent's own stacking (see its own doc comment): e.g. 25% execute + 15%
        // crit-execute means non-crits execute at 25%, crits execute at 1-(1-0.25)*(1-0.15) = 36.25%, not a
        // flat 40%. A non-crit hit only ever sees the plain executeThreshold.
        if (b.health > 0f && CharacterStatsPD.All.TryGetValue(attackerOwner, out var attackerStats) && attackerStats.Execute)
        {
            bool isBoss = spawnCategory == EnemySpawnCategory.Boss;

            float executeThresh = isBoss ? attackerStats.BossExecuteThreshold : attackerStats.executeThreshold;
            if (isCrit)
            {
                float critBonus = isBoss ? attackerStats.BossCritExecuteThreshold : attackerStats.critExecuteThreshold;
                executeThresh = 1f - (1f - executeThresh) * (1f - critBonus);
            }

            if (executeThresh > 0f && b.health <= b.scaledMaxHealth * executeThresh)
            {
                b.health = 0f;
                NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Executed);
            }
        }

        if (b.health <= 0)
        {
            b.health = 0;
            // Captured here, not inferred later — Die()/OnEnemyDeath need to know whether the specific hit
            // that actually killed this enemy was a crit/highroll (e.g. OnKillLaunchEnemiesAugment inheriting
            // both instead of rolling its own), and isCrit/isHighroll are only in scope as this call's own
            // parameters.
            b.killedByCrit = isCrit;
            b.killedByHighroll = isHighroll;
            Die();
        }
    }

    // isPriority: true from actual weapon hits, false (default) from onhit-effect/Phantom Blade sources —
    // jumps this hit's damage number to the front of DamageNumberManager's queue for this enemy+attacker
    // ahead of whatever's already waiting, so real weapon damage always reads first regardless of how many
    // onhit effects also fired off the same hit.
    public void TriggerHitFeedback(float damage, bool isCrit, bool isHighroll, CharacterStatsPD attacker, bool isPriority = false, bool overrideSfx = false, AudioClip sfxOverride = null)
    {
        // PD weapons (WeaponMeleePD, WeaponRangedProjectilePD) queue hit feedback in Simulate()/
        // LateSimulate() but only drain and fire it here in UpdateView(), at render rate — by the time it
        // fires, this pooled enemy instance can already have been reused for a new spawn (isDead reset to
        // false) while transiently still inactive, same as the respawn-reset/animator cases elsewhere in
        // this class.
        //
        // The damage number/SFX calls below are gated on !_hiddenOnDeath, not !B.isDead — a held corpse
        // (kill-launch or any future similar on-death-delay effect) is still dead (isDead is already true
        // for the killing blow itself, and stays true for every hit it takes while held) but stays visible/
        // renderer-enabled the whole time it's held, so it should still show damage numbers/SFX for every
        // hit it takes during that window, including the killing blow. Once actually hidden there's nothing
        // left to show feedback on.
        //
        // TriggerFlash() itself, though, is skipped entirely for ANY dead enemy (not just kill-launch
        // specifically — !B.isDead, not !B.killLaunchActive) — UpdateView's own continuous, isDead-driven
        // red-paint (see its own comment) already owns this enemy's color for its entire remaining
        // lifetime the instant it dies, including the killing blow's own hit (Die() already ran by the time
        // this method processes that same hit's feedback), so a separate normal flash here would just be
        // redundant color-painting fighting the same value.
        if (!_hiddenOnDeath && !B.isDead && gameObject.activeInHierarchy) TriggerFlash();
        // !B.invulnerable — a 0-damage hit against an invulnerable enemy never shows a "0" popup. Scoped
        // tightly to just the number (per explicit design) — TriggerFlash()/SFX above/below are untouched.
        if (showDamageNumbers && !B.invulnerable) ShowDamageNumber(damage, attacker.owner ?? default, isCrit, isHighroll, isPriority);
        if (attacker.isOwner && SFXManager.instance != null)
        {
            AudioClip clip = overrideSfx ? sfxOverride : attacker.hitSfx;
            SFXManager.instance.Play(clip, transform.position, 1f, 0.05f);
        }
    }

    protected virtual void Die()
    {
        B.isDead = true;

        // ActiveCount/AdjustCategoryCount used to be decremented right here — but unlike B.isDead/
        // B.deathHoldCount (real TState, correctly restored to its pre-tick value by rollback before every
        // resimulation replay) or hierarchy.Create()'s own instance counter (verified via PredictedHierarchy
        // source: backed by PredictedHierarchyState, same rollback protection), these are plain static
        // fields with NO rollback awareness at all. A death tick that gets resimulated N times before
        // settling (ordinary network-latency-driven catch-up, not even a misprediction) would decrement
        // these N times for one real death, silently drifting them arbitrarily negative over a long match —
        // which would eventually defeat EnemySpawnerPD's own spawn-cap checks entirely (ActiveNormalCount <
        // maxNormalEnemies is always true once the count is negative enough). Moved to UpdateView's existing
        // verified-state, edge-detected block below (same one RaiseOnEnemyDeath/AwardUpgradePoints already
        // use) so this fires exactly once per real, server-confirmed death instead of once per replay.

        // PhysicsCollider disable + layer swap (keeps this corpse out of any layer-mask-based query —
        // weapon hitboxes, this same kill-launch's own sphere-cast against OTHER enemies, etc. — without
        // needing to separately toggle _hurtbox.enabled too) used to be set here directly, but that made
        // them an unrecoverable one-shot side effect: if this death later turned out to be a misprediction
        // (isDead reverting to false once the real, non-lethal server state resolves), nothing ever
        // reverted them. Moved to UpdateView's own isDead-edge-detected sync (see its own comment) instead,
        // which runs immediately on the very same tick either way but correctly reverses if corrected.

        // Reserves exactly enough death-holds up front, atomically, for every effect that's going to react
        // to this death — no dependence on OnEnemyDeath listener dispatch (in UpdateView, potentially many
        // ticks later) actually running before hide/delete gets a chance to check deathHoldCount. See
        // CharacterStatsPD.deathEventCount/killEventCount's own doc comment for the summed-vs-killer-only
        // split. This is a pure read of counts that are only ever incremented once each (at augment
        // OnApply()), so it's safe here even though Die() runs from Simulate()-context.
        int holds = 0;
        foreach (var stats in CharacterStatsPD.All.Values)
            holds += stats.deathEventCount;
        bool foundKiller = CharacterStatsPD.All.TryGetValue(B.killerOwner, out var killerStats);
        if (foundKiller)
            holds += killerStats.killEventCount;
        B.deathHoldCount += holds;

        if (XPOrbManagerPD.instance != null)
            XPOrbManagerPD.instance.SpawnOrbs(hierarchy, transform.position, B.scaledXpValue, spawnCategory, spawnTier);
    }

    // Difficulty-pacing wipe (EnemySpawnerPD, right before a boss spawns) — deliberately NOT a real death:
    // no XP orbs, no RaiseOnEnemyDeath (so no on-kill augment/effect ever reacts to this — see Die()'s own
    // XP/RaiseOnEnemyDeath handling for contrast), no death-hold reservation, no kill-launch interaction, no
    // red death-flash. Just a predicted-instant hide (see UpdateView's own wiped block) followed by a
    // verified-gated hierarchy.Delete() once confirmed — same deletion-safety convention as every other
    // spawned/despawned object in this codebase (never delete off raw predicted state), just with its own
    // separate edge-detection from isDead's, since the two are mutually exclusive outcomes for one life.
    // Guarded against an enemy already dying/dead/wiped — a wipe landing on the same tick as a real death
    // (e.g. a player's own hit lands the same instant the spawner wipes) should never double up; the real
    // death already in progress simply wins.
    public void SilentDespawn()
    {
        if (B.isDead || B.wiped) return;
        B.wiped = true;
    }

    // ── Death hold ─────────────────────────────────────────────────────────
    // Generic "don't hide/delete yet" API — any number of independent scripts can each request their own
    // hold and release it on their own schedule; hide+delete both wait until deathHoldCount reaches 0
    // regardless of how many are currently outstanding, so one script releasing never ends another's hold.
    // Safe to call from a verified-state reaction (e.g. CharacterStatsPD.OnEnemyDeath, which — like every
    // other "fires exactly once per real occurrence" event in this codebase — is safe to mutate predicted
    // state from), NOT from raw Simulate()-context code, which risks double-counting during resimulation.
    public void RequestDeathHold() => B.deathHoldCount++;
    public void ReleaseDeathHold() => B.deathHoldCount = Mathf.Max(0, B.deathHoldCount - 1);

    // Starts the on-kill launch effect (OnKillLaunchEnemiesAugment) — called from a verified OnEnemyDeath
    // reaction (safe, see RequestDeathHold's own comment). Does NOT request its own hold — Die() already
    // reserved exactly one for this effect via the killer's killEventCount (see Die()'s own comment), since
    // OnKillLaunchEnemiesAugment.OnApply() incremented that count. HandleKillLaunch releases that reserved
    // hold once the corpse finishes travelling.
    public void StartKillLaunch(Vector3 direction, float speed, float distance, float damage, bool isCrit = false, bool isHighroll = false)
    {
        ref var b = ref B;
        b.killLaunchActive = true;
        b.killLaunchDirection = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;
        b.killLaunchSpeed = speed;
        b.killLaunchRemainingDistance = distance;
        b.killLaunchDamage = damage;
        b.killLaunchIsCrit = isCrit;
        b.killLaunchIsHighroll = isHighroll;

        if (_rigidbody != null)
        {
            // Guarded by !isKinematic — same reason Simulate()'s own pause-handling does this (see its
            // comment): SpearGloomPD/KingSpikePD-style Windup/Stabbing sub-states can already have this
            // rigidbody kinematic when the killing blow lands mid-attack, and PredictedRigidbody's velocity
            // setters throw ("not supported") if attempted on an already-kinematic body.
            if (!_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.isKinematic = true;
        }
    }

    // Moves the corpse at a constant speed toward killLaunchRemainingDistance, sphere-casting each step
    // against other enemies (radius 0.5 * size) and dealing killLaunchDamage once per enemy hit — the
    // damage itself was already rolled once at kill-time (StartKillLaunch's caller), not re-rolled here.
    // Lives on EnemyBasePD itself, not the augment, since ANY enemy type can be the one killed and the
    // augment (a separate PredictedIdentity on the player) has no ongoing per-tick relationship with this
    // specific corpse once it fires StartKillLaunch() — the corpse has to track and end its own effect.
    // This enemy's own hurtbox/collider is already off the layer HandleKillLaunch's sphere-cast mask
    // targets (Die() swapped it to NoneLayer), so it can never sphere-cast-detect itself.
    private void HandleKillLaunch(ref EnemyBaseState b, float delta)
    {
        float step = Mathf.Min(b.killLaunchSpeed * delta, b.killLaunchRemainingDistance);
        Vector3 prevPos = transform.position;
        Vector3 newPos  = prevPos + b.killLaunchDirection * step;

        if (_rigidbody != null) _rigidbody.MovePosition(newPos);
        else transform.position = newPos;

        b.killLaunchRemainingDistance -= step;

        if (step > 0.0001f)
        {
            float radius = 0.5f * size;
            int n = Physics.SphereCastNonAlloc(prevPos, radius, b.killLaunchDirection, _killLaunchSweepResults, step, CombatLayerMasks.EnemyHurtboxMask);
            for (int i = 0; i < n; i++)
            {
                if (!_killLaunchSweepResults[i].collider.TryGetComponent(out IEnemyPD enemy)) continue;
                if (enemy._isDead) continue;
                if (b.killLaunchHitIds.Contains(enemy.ObjectId)) continue;
                b.killLaunchHitIds.Add(enemy.ObjectId);
                enemy.TakeDamage(b.killLaunchDamage, b.killLaunchIsCrit, b.killerOwner, b.killLaunchIsHighroll);
                // TakeDamage alone never shows a damage number (see TriggerHitFeedback's own doc comment on
                // IEnemyPD) — fired through _onHitFeedback rather than a queue (see HitFeedbackData's own
                // doc comment for why a plain queue here was resimulation-unsafe).
                if (CharacterStatsPD.All.TryGetValue(b.killerOwner, out var killLaunchAttacker))
                    _onHitFeedback.Invoke(new HitFeedbackData { target = enemy.ObjectId, damage = b.killLaunchDamage, isCrit = b.killLaunchIsCrit, isHighroll = b.killLaunchIsHighroll, attacker = killLaunchAttacker, isPriority = false });
            }
        }

        if (b.killLaunchRemainingDistance <= 0f)
        {
            b.killLaunchActive = false;
            if (_rigidbody != null) _rigidbody.isKinematic = false;
            ReleaseDeathHold();
        }
    }

    // Public stun entry point — every caller outside this class goes through here, so crowdControlResist
    // applies uniformly to any non-knockback CC source. AddKnockbackVelocity below calls ApplyStun()
    // directly instead, bypassing this reduction (see crowdControlResist's own doc comment for why).
    // isTrueStun: true — this is a real Stun() call, so the yellow stun tint (UpdateView) should track it,
    // unlike AddKnockbackVelocity's own derived micro-stun. A true stun ALWAYS lands (see ApplyStun's own
    // doc comment) — it just doesn't interrupt whatever attack is already committed; see Simulate()'s own
    // isStunnable-gated freeze and IsTrueStunned for where each subclass's own multi-attack chain-check
    // reacts to it instead.
    //
    // A caller that actually wants to CANCEL whatever attack is in progress (rather than merely stun once
    // it's over) pairs this with a preceding Interrupt() call instead of relying on Stun() alone — see
    // Interrupt()'s own doc comment for why the ordering matters. KnightBuffStunAbilityPD/
    // ShieldBreakBurstAugment do this today (via AreaEffectCastHelper's own interrupt param); not every
    // future true stun needs to.
    public void Stun(float duration)
    {
        // Rising edge only (before/after B.trueStunTimer), so re-stunning an already-true-stunned enemy
        // doesn't spam a repeat popup — also naturally suppressed if ApplyStun's own stunResistant/isDead
        // guard blocks this outright (trueStunTimer never actually rises). Deliberately NOT in
        // InterruptTrueStun() below — its own bonus duration shares this same trueStunTimer channel but
        // should never print "Stun" on its own, per explicit design (the "Interrupted" popup already covers
        // that case visually).
        bool wasTrueStunned = B.trueStunTimer > 0f;
        ApplyStun(duration * (1f - crowdControlResist), isTrueStun: true);
        if (!wasTrueStunned && B.trueStunTimer > 0f) NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Stun);
    }

    // Called (via CharacterStatsPD.NotifyInterrupted) on EVERY successful interrupt (SI) landed on THIS
    // enemy — every enemy an interrupt-capable effect lands on this activation gets this, not just the first.
    // Grants InterruptTrueStunDuration flat, UNSCALED by crowdControlResist (a "true TRUE stun"
    // per explicit design — even a heavily CC-resistant enemy eats the full flat duration; only
    // stunResistant/death still block it entirely, same as Stun() above).
    //
    // additionalResistibleDuration is for a caller that ALSO explicitly stuns whatever it interrupts on the
    // same hit (e.g. KnightBuffStunAbilityPD's own onEnemyHit Stun() call) — that duration gets tacked
    // onto the TAIL end of the flat portion instead of applied as a fully separate Stun() call, scaled by
    // crowdControlResist normally (unlike the flat portion). Passing 0 (default) — an interrupt with no
    // paired stun (DashThroughInterruptAugment, ExeAxeThrowProjectilePD) — just grants the flat portion alone.
    // Both portions land as one combined true-stun duration via ApplyStun's own Mathf.Max semantics, so this
    // can never produce a SHORTER total than a same-hit standalone Stun() call would have on its own.
    public void InterruptTrueStun(float additionalResistibleDuration = 0f) =>
        ApplyStun(InterruptTrueStunDuration + additionalResistibleDuration * (1f - crowdControlResist), isTrueStun: true);

    private void ApplyStun(float duration, bool isTrueStun = false)
    {
        if (_isDead || stunResistant) return;
        // A knockback-derived micro-stun (isTrueStun: false) still can't land mid-attack — unlike a true
        // stun, which always lands regardless (see Stun()'s own doc comment) — but this is effectively a
        // no-op guard in practice: AddKnockbackVelocity already independently requires isStunnable before
        // ever calling this, so re-checking it here only matters if some future non-knockback caller ever
        // reuses isTrueStun: false directly.
        if (!isTrueStun && !isStunnable) return;
        B.stunTimer = Mathf.Max(B.stunTimer, duration);
        if (isTrueStun) B.trueStunTimer = Mathf.Max(B.trueStunTimer, duration);
    }

    // Public slow entry point — unlike Stun(), crowdControlResist does NOT touch duration here; a slow
    // always lands for its full requested duration. Instead it reduces the slow's POTENCY: multiplier is
    // "how much of normal speed remains" (e.g. 0.35 = a 65% slow), so the amount actually being taken away
    // is (1 - multiplier) — that's the piece crowdControlResist shrinks, then the result is converted back
    // into a multiplier. This is the same "convert to amount-removed, scale, convert back" shape
    // ArcherArrowRainAbilityPD's own AreaEffectPower slow scaling uses, so resist stacks with it the same
    // intuitive way (a resist can only ever pull a slow BACK toward 1x, never past it).
    //
    // Also now doubles as an attack-speed slow — see EffectiveAttackSpeed's own doc comment — since this is
    // the one shared multiplier both EffectiveMoveSpeed and EffectiveAttackSpeed read.
    //
    // Same "always lands, even mid-attack-sequence" treatment as a true Stun() (per the user's own call — a
    // Slow no longer gets blocked by isStunnable/attack-state at all). It still never interrupts whatever
    // attack is already committed (nothing here touches HandleActiveState/HandleDash), it just means IsSlowed
    // reads true immediately regardless of attack state. Does NOT stop a multi-attack chain from continuing
    // (2026-08-19 correction — see IsCrowdControlled's own doc comment): Slow is this codebase's one SOFT CC,
    // non-immobilizing, and was never meant to cancel a committed chain the way a true Stun does.
    public void Slow(float multiplier, float duration)
    {
        // Rising edge only (before/after B.slowTimer), same reasoning as Stun()'s own popup gate — a fresh
        // slow landing while already slowed (even a stronger one, per ApplySlow's own "never waters down"
        // rule) just extends/strengthens the existing effect, not worth a repeat popup.
        bool wasSlowed = B.slowTimer > 0f;
        float slowAmount = 1f - multiplier;
        float resistedAmount = slowAmount * (1f - crowdControlResist);
        ApplySlow(1f - resistedAmount, duration);
        if (!wasSlowed && B.slowTimer > 0f) NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Slowed);
    }

    private void ApplySlow(float multiplier, float duration)
    {
        if (_isDead || stunResistant) return;

        // 90% is the max possible slow (10% speed floor) — clamped here, not at each call site, so any
        // caller can safely send an arbitrarily extreme value (e.g. a stacked 999% slow from AreaEffectPower
        // scaling) without needing its own clamp, and so the strength-comparison logic right below always
        // compares already-clamped values.
        multiplier = Mathf.Max(0.1f, multiplier);

        // A weaker slow (multiplier closer to 1) landing while a stronger one is still active doesn't water
        // it down early — only adopts the new multiplier if it's at least as strong (lower) as whatever's
        // currently active, or the previous slow has already expired. Duration always extends to the longer
        // of the two regardless, same "never shortens what's already active" reasoning as Stun's own
        // Mathf.Max — a fresh weak slow still tops up how much longer the existing strong one lasts.
        if (B.slowTimer <= 0f || multiplier <= B.slowMultiplier)
            B.slowMultiplier = multiplier;
        B.slowTimer = Mathf.Max(B.slowTimer, duration);
    }

    // Public bleed entry point (2026-10-06, ExeBigAxeBleedAugment) — see State.bleedDamagePerSecond's own
    // doc comment for the full bucket mechanism. damagePerSecond is the CALLER's already-fully-resolved
    // rate for just this one stack (Strength * ratePercent * onHitPower, luck-rolled — see the augment's
    // own doc comment for the exact formula) — ADDED into the running total, never replacing it, so
    // successive stacks compound without limit. duration REFRESHES the shared timer (Mathf.Max, same
    // "never shortens what's already active" semantics Slow's own duration uses) rather than extending it
    // additively — a bleed always lasts exactly as long as its longest-remaining contribution, not the sum
    // of every application. No crowdControlResist/stunResistant gating — unlike Slow/Stun, bleed is pure
    // damage-over-time, not a crowd-control effect, so nothing in this codebase's existing CC-resist
    // taxonomy applies to it.
    public void ApplyBleed(float damagePerSecond, float duration, PlayerID attacker)
    {
        if (_isDead || damagePerSecond <= 0f || duration <= 0f) return;
        B.bleedDamagePerSecond += damagePerSecond;
        B.bleedTimer = Mathf.Max(B.bleedTimer, duration);
        B.bleedAttacker = attacker;
    }

    // Cancels this enemy's current active-attack-sequence outright (as opposed to a true Stun(), which now
    // deliberately lets it finish — see IsTrueStunned's own doc comment). A no-op entirely if this specific
    // phase isn't interruptible (see CanBeInterrupted's own doc comment, e.g. LeaperGloomPD mid-Leaping,
    // KingSpikePD's SpikeBurstWindup) or if already dead.
    //
    // Un-kinematic/collider/hurtbox restore happens FIRST, before anything else (including OnInterrupted()
    // below) — a caller pairing this with a knockback/stun on the same hit (e.g. AreaEffectCastHelper's own
    // interrupt param, applied before its per-enemy damage/knockback/onEnemyHit block) needs the rigidbody
    // already non-kinematic by the time that knockback's own velocity assignment runs, or it gets silently
    // eaten by AddKnockbackVelocity's own "not supported on a kinematic body" guard — see that method's own
    // comment. isStunnable flips back to true here for the same reason: a paired Stun()/Slow() call right
    // after this one must not itself get blocked by this enemy still reading as attack-protected.
    //
    // Attack animations are NOT explicitly touched here — every subclass's own UpdateView already
    // declaratively re-derives them from its own stateId every view tick (see e.g. SpearGloomPD.UpdateView's
    // switch), so resetting that state machine via OnInterrupted() below already handles them for free.
    // Windup INDICATORS are the one exception each subclass's own OnInterrupted() override now handles
    // explicitly (predicted-instant SetActive(false)/ResetFill(), not left for UpdateView to catch up on its
    // own) — Interrupt() mutates currentState out-of-band, from a different identity's own Simulate() call
    // rather than this enemy's own HandleActiveState flow, which was leaving the indicator visibly stuck for
    // a stray tick before UpdateView's viewState caught up. See any of Spear/Dagger/Leaper/Blitz/
    // KingSpikePD's own OnInterrupted() for the pattern.
    public bool Interrupt()
    {
        if (_isDead || !CanBeInterrupted) return false;

        B.isDashing = false;
        // Sibling to isDashing above — without this, interrupting an enemy mid-offset-chase (e.g. Spear/
        // Dagger's own DashingToTarget state) would leave isOffsetChasing stuck true forever: Simulate()'s
        // own tick loop only ever calls HandleActiveState once BOTH isDashing and isOffsetChasing read
        // false, so a leaked true here would permanently block it from ever running again for this enemy.
        B.isOffsetChasing = false;

        if (_rigidbody != null)
        {
            _rigidbody.isKinematic = false;
            _rigidbody.velocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }
        if (_physicsCollider != null) _physicsCollider.enabled = true;
        if (_hurtbox != null) _hurtbox.enabled = true;

        isStunnable = !stunResistant;

        OnInterrupted();
        NotifyStatusPopup(DamageNumberManager.StatusPopupKind.Interrupted);
        return true;
    }

    // See IEnemyPD.SetMovementDisabled's own doc comment. No _isDead guard — harmless either way (a dead
    // enemy's HandleMovement never runs regardless), and a caller may legitimately want to pre-arm/clear
    // this before this specific enemy has even spawned into whatever chain called it, so refusing the write
    // here would just be an arbitrary restriction.
    public void SetMovementDisabled(bool disabled) => B.movementDisabled = disabled;

    public bool IsInvulnerable => B.invulnerable;
    public void SetInvulnerable(bool invulnerable) => B.invulnerable = invulnerable;

    public bool IsBeingHeld => B.orbitPhase != (int)EnemyOrbitPhase.None;
    public bool IsWaitingToOrbit => B.orbitPhase == (int)EnemyOrbitPhase.WaitingToOrbit;
    public bool IsPulling => B.orbitPhase == (int)EnemyOrbitPhase.Pulling;
    public bool IsLaunching => B.launchActive;

    public bool IsKinematic => _rigidbody != null && _rigidbody.isKinematic;

    // See IEnemyPD.ClaimForOrbit's own doc comment. Sets orbitOwnerId early so LateSimulate()'s own defensive
    // owner-resolve (if this somehow starts running before OrbitTarget() ever gets called — e.g. the owner
    // dying during the claimed-but-not-yet-pulling window) already has a valid reference to release against.
    public void ClaimForOrbit(PredictedObjectID owner)
    {
        B.orbitOwnerId = owner;
        B.orbitPhase   = (int)EnemyOrbitPhase.Claimed;
    }

    // See IEnemyPD.SetKinematic's own doc comment for why ON/OFF are asymmetric. ON just arms the flag —
    // actually applying it is Simulate()'s own job (see that call site's comment), a defensive per-tick
    // re-assertion rather than an immediate write here, so it never races whatever this same tick's
    // HandleActiveState is doing with the rigidbody. OFF can't wait for next tick's Simulate() pass the same
    // way — nothing else will ever un-kinematic a rigidbody left kinematic by this, so it has to be a real,
    // immediate revert right here, mirroring Interrupt()'s own explicit revert above.
    public void SetKinematic(bool kinematic)
    {
        B.externalKinematic = kinematic;
        if (!kinematic && _rigidbody != null)
        {
            _rigidbody.isKinematic = false;
            _rigidbody.velocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }
    }

    // See IEnemyPD.SetPhysicsColliderDisabled's own doc comment for why ON/OFF are asymmetric — same
    // reasoning as SetKinematic, just for the push collider instead of the rigidbody's kinematic flag.
    public void SetPhysicsColliderDisabled(bool disabled)
    {
        B.physicsColliderDisabled = disabled;
        if (!disabled && _physicsCollider != null) _physicsCollider.enabled = true;
    }

    public void SetHurtboxDisabled(bool disabled)
    {
        // One-way coupling — see this method's own IEnemyPD doc comment for why. SetInvulnerable is safe to
        // call unconditionally here even on the OFF path (disabled=false): a caller that independently
        // wants THIS enemy to stay invulnerable while merely re-enabling its hurtbox needs to call
        // SetInvulnerable(true) again itself, same as any other composed-primitives case elsewhere in this
        // codebase — this method has no way to know "should invulnerable survive past this hurtbox
        // re-enable" on its own, so it doesn't try to guess.
        SetInvulnerable(disabled);
        B.hurtboxDisabled = disabled;
        if (!disabled && _hurtbox != null) _hurtbox.enabled = true;
    }

    // See IEnemyPD.OrbitTarget's own doc comment for the full mechanism. ownerPosition is unused beyond this
    // call (LateSimulate() re-resolves owner's LIVE position every tick from here on) — kept as a parameter
    // for symmetry/because callers already have it on hand, not because this method needs it for anything
    // beyond the caller's own convenience.
    public void OrbitTarget(PredictedObjectID owner, Vector3 ownerPosition, float radius, float pullSpeed, float angularSpeedDegPerSec, float targetAngleDeg)
    {
        ref var b = ref B;

        b.orbitAngleDeg             = targetAngleDeg;
        b.orbitOwnerId              = owner;
        b.orbitRadiusTarget         = radius;
        b.orbitPullSpeed            = Mathf.Max(0.0001f, pullSpeed);
        b.orbitAngularSpeedDegPerSec = angularSpeedDegPerSec;
        b.orbitPhase                = (int)EnemyOrbitPhase.Pulling;
    }

    // See IEnemyPD.BeginOrbiting's own doc comment.
    public void BeginOrbiting()
    {
        if (B.orbitPhase == (int)EnemyOrbitPhase.WaitingToOrbit)
            B.orbitPhase = (int)EnemyOrbitPhase.Orbiting;
    }

    public void StopOrbiting()
    {
        B.orbitPhase   = (int)EnemyOrbitPhase.None;
        B.orbitOwnerId = default;
        B.orbitFrozen  = false;
    }

    // See IEnemyPD.SetOrbitAngularSpeed's own doc comment.
    public void SetOrbitAngularSpeed(float angularSpeedDegPerSec) => B.orbitAngularSpeedDegPerSec = angularSpeedDegPerSec;

    // See IEnemyPD.SetOrbitFrozen's own doc comment.
    public void SetOrbitFrozen(bool frozen) => B.orbitFrozen = frozen;

    // See IEnemyPD.LaunchAlong's own doc comment for the full contract; LateSimulate() does the actual
    // per-tick movement (checked ahead of the orbit-phase branch there).
    public void LaunchAlong(Vector3 direction, float distance, float speed)
    {
        ref var b = ref B;
        b.launchActive            = true;
        b.launchDirection         = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;
        b.launchRemainingDistance = distance;
        b.launchSpeed             = speed;
    }

    public EnemySpawnCategory SpawnCategory => spawnCategory;

    // Override to reset this enemy's own attack-sequence state machine back to Moving/idle (typically its
    // own EnterMoving(ref currentState), same terminal transition its own attack-chain already uses when it
    // naturally runs out of attacks) — see Interrupt()'s own doc comment. No-op by default.
    protected virtual void OnInterrupted() { }

    // Reworked (2026-08-10): false by default, not true — interrupting an enemy does nothing outside of
    // cancelling one of ITS OWN otherwise-CC-immune attacks, so "can be interrupted" should mean exactly
    // that, not "happens to not be in the one or two phases explicitly excluded." Each subclass now opts IN
    // per-state, flagging exactly the states that are both !isStunnable (committed/CC-immune) AND meant to
    // be cancelable — computed directly from each enemy's own existing isStunnable formula, e.g.
    // ShooterGloomPD's own override. This is also what makes Interrupt()'s own return value alone a reliable
    // "did this actually cancel something real" signal for callers like DashThroughInterruptAugment, without
    // needing a second IsStunnable check layered on top.
    protected virtual bool CanBeInterrupted => false;

    // Called by a spawner right after hierarchy.Create() returns, telling this instance which category
    // spawned it. spawnCategory is [NonSerialized] and always starts Normal — LateAwake() has already
    // bucketed this instance into ActiveNormalCount using that default by the time this runs, so the
    // bucket is corrected here rather than assumed, regardless of exactly when LateAwake() ran relative to
    // this call. Health scaling mutates B.health directly (not just maxHealth) since GetInitialState()
    // already baked b.health = maxHealth before the spawner got a chance to scale it — same "mutate
    // currentState right after Create()" pattern as XPOrbPD.SetValue(). damage doesn't need that trick
    // (nothing bakes it into predicted state at spawn time — it's read live off the field every use), but
    // its base* snapshot (captured in Awake(), before this ever runs) DOES need to be scaled in lockstep,
    // since subclasses can derive further values from it.
    public void ConfigureSpawn(EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier, float pointsMultiplier, float moveSpeedMultiplier, float attackCooldownMultiplier, float attackSpeedMultiplier, int tier)
    {
        if (category != spawnCategory)
        {
            AdjustCategoryCount(spawnCategory, -1);
            spawnCategory = category;
            AdjustCategoryCount(spawnCategory, +1);
        }
        spawnTier = tier;
        this.attackCooldownMultiplier = attackCooldownMultiplier;
        ApplyTierMaterials(tier);
        ApplyTierNamePrefix(tier);

        // xpValue/pointsValue themselves stay pristine read-only authored baselines now too (2026-09-22,
        // same shape as maxHealth/damage/moveSpeed/attackSpeed below) — baseXpValue/basePointsValue carry
        // the scaled result, written into State so Die()/the verified-death block stay correct on a machine
        // that never itself ran this ConfigureSpawn call.
        baseXpValue *= xpMultiplier;
        B.scaledXpValue = baseXpValue;
        basePointsValue *= pointsMultiplier;
        B.scaledPointsValue = basePointsValue;

        // Unconditional now (2026-09-22, was each guarded behind `if (xMultiplier != 1f)`) — maxHealth/
        // damage/moveSpeed/attackSpeed themselves are pristine read-only authored baselines now (see
        // scaledMaxHealth's own doc comment), never mutated in place, so recomputing baseX/scaledX fresh
        // here every time is always cheap and correct, multiplier == 1f included. Writes go into State
        // (B.scaledX), not a plain field, specifically so this takes effect on every machine via normal
        // PurrDiction replication even when THIS machine isn't the one that actually called ConfigureSpawn
        // (see scaledMaxHealth's own doc comment for the reconciliation scenario this fixes).
        baseHealth *= healthMultiplier;
        B.scaledMaxHealth = baseHealth;
        B.health = B.scaledMaxHealth;

        baseDamage *= damageMultiplier;
        B.scaledDamage = baseDamage;

        baseMoveSpeed *= moveSpeedMultiplier;
        B.scaledMoveSpeed = baseMoveSpeed;

        // attackSpeed (2026-08-31) — see IEnemyPD.ConfigureSpawn's own attackSpeedMultiplier doc comment. No
        // base* counterpart needed (unlike moveSpeed) — attackSpeed has no further per-enemy derivation
        // beyond EffectiveAttackSpeed itself.
        B.scaledAttackSpeed = attackSpeed * attackSpeedMultiplier;

        OnConfigureSpawn();
    }

    // Extension point for a subclass's OWN per-tier scaling on a field unique to it (e.g.
    // OrbiterGloomPD.maxGrabCount, ShielderGloomPD.rotationRateDegPerSecond) — spawnTier is already set by
    // the time this fires, so an override just reads it directly, same "1f + (tier - 1) * growthRate" shape
    // EnemySpawnerPD's own moveSpeedTierGrowth/damageTierGrowth formulas already establish for the 4 generic
    // stats, just localized to the one enemy script that actually owns the field instead of centralized in
    // the spawner. Called unconditionally at the END of ConfigureSpawn(), after every generic stat multiplier
    // above has already been applied and spawnCategory/spawnTier are both final for this life.
    //
    // IMPORTANT for anything an override here needs to READ MORE THAN ONCE across this instance's pooled
    // lifetime (i.e. anything that isn't simply overwritten fresh — a field this multiplies onto itself, or a
    // fixed-size pool/array sized off it): this runs on EVERY pooled reuse just like ConfigureSpawn itself
    // does, so the same "_authoredX snapshot, captured once, reset before each life's fresh scaling" pattern
    // this class uses for maxHealth/damage/moveSpeed/xpValue applies here too (see OnAwake's own doc comment
    // for where to capture that snapshot) — multiplying a field that already holds a PREVIOUS life's scaled
    // result would compound across reuses exactly like the old health/damage/moveSpeed bug this class's own
    // _authoredX fields were added to fix.
    protected virtual void OnConfigureSpawn() { }

    private static void AdjustCategoryCount(EnemySpawnCategory category, int delta)
    {
        switch (category)
        {
            case EnemySpawnCategory.Normal: ActiveNormalCount += delta; break;
            case EnemySpawnCategory.Elite:  ActiveEliteCount  += delta; break;
            case EnemySpawnCategory.Pleb:   ActivePlebCount   += delta; break;
            case EnemySpawnCategory.Boss:   ActiveBossCount   += delta; break;
        }
    }

    public void AddKnockbackVelocity(Vector3 impulse)
    {
        if (!isStunnable || _isDead) return;
        float effectiveSpeed = impulse.magnitude * (1f - crowdControlResist);
        // Guarded by !isKinematic — same reason StartKillLaunch/Simulate()'s pause-handling do this: a
        // Windup/Stabbing/Spinning-style kinematic sub-state (SpearGloomPD, KingSpikePD, DaggerGloomPD's
        // spin, etc.) means this rigidbody can already be kinematic when a knockback source (e.g.
        // ShieldBreakBurstAugment's AoE) hits, and PredictedRigidbody's velocity setter throws ("not
        // supported") if attempted on an already-kinematic body. The stun below still applies regardless —
        // this method's own isStunnable guard above already decided whether this knockback (and the derived
        // micro-stun it produces) should apply at all, independent of whether the physical push could happen.
        if (!_rigidbody.isKinematic)
        {
            // Zeroed first rather than added to, so knockback doesn't have to fight whatever velocity the
            // enemy's own movement AddForce was already carrying that tick.
            _rigidbody.velocity = impulse.normalized * effectiveSpeed;
        }
        const float drag      = 9f;
        const float threshold = 1f;
        float stunDuration = effectiveSpeed > threshold
            ? Mathf.Log(effectiveSpeed / threshold) / drag
            : 0f;
        // ApplyStun, not Stun — this duration is already derived from effectiveSpeed above (itself already
        // reduced by crowdControlResist), so routing it back through Stun()'s own resist multiplier would
        // apply the same resistance twice against the same knockback hit.
        ApplyStun(stunDuration);
    }

    // ── Visuals ────────────────────────────────────────────────────────────
    // Fires the instant this enemy is dead (viewB.isDead edge, predicted/instant timing, NOT gated on
    // deathHoldCount) — a held corpse (kill-launch, any future on-death effect) should already read as
    // fully "dead" behaviorally the whole time it's held: Simulate() already stops updating movement/attack
    // state once isDead, so this is the visuals-side counterpart — stop any attack telegraph (windup
    // indicator, charge fill, etc.) immediately rather than leaving it visibly active/frozen on a corpse
    // that's still flying around. Deliberately separate from the render-hide block (renderers/hurtbox/
    // physicsCollider disable), which still waits for deathHoldCount <= 0 — the model and healthbar are
    // meant to stay visible for the whole hold, only attack telegraphs stop early.
    protected virtual void OnDeathStop() { }
    protected virtual void UpdateAnimator(TState viewState) { }

    // Called once per (re)spawn, from within the guaranteed-reliable respawn block in Simulate().
    // Subclasses with their own pooled state (attack phases, hitboxes, visual-state caches, etc.)
    // should reset it here instead of OnRemovedFromPool(), which never fires for pooled reuse.
    protected virtual void OnRespawnReset() { }

    private void ShowDamageNumber(float amount, PlayerID attackerOwner, bool isCrit = false, bool isHighroll = false, bool isPriority = false)
    {
        // Passes this (IEnemyPD), not a bare Transform — DamageNumberManager/DamageNumber read .Visuals off
        // it for position (smoothly interpolated PredictedTransform graphics child, not the raw tick-
        // stepped physics root) and .SpawnGeneration to detect pooled reuse: a raw Transform reference
        // alone can't tell a still-alive enemy apart from this same pooled GameObject having since been
        // reused for a completely different spawn, which was causing already-tracking numbers to snap
        // away/disappear when their original enemy died and got recycled before their animation finished.
        // The passed stackKey (enemy + attacking player) is the STACK this hit's damage number belongs to —
        // DamageNumberManager itself combines it with `isPriority` to get the actual accumulation group; see
        // its own class doc comment for the full rule.
        if (DamageNumberManager.instance != null)
            DamageNumberManager.instance.Spawn(amount, this, HashCode.Combine(GetInstanceID(), attackerOwner.GetHashCode()), isCrit, isHighroll, attackerOwner, isPriority);
    }

    private const float FlashDuration = 0.08f;
    private float _flashTimer;
    private bool  _isFlashing;

    // Timer-based, not a StartCoroutine(...) per hit — a coroutine-per-hit meant N hits landing within the
    // same ~0.08s window (very much the normal case for "swarm scale" enemy types getting hit by AoE/multi-
    // hit weapons) spawned N independent coroutines, each on its OWN 0.08s clock. Once the first one's
    // timer expired it would revert to _originalColors[i] while later ones were still mid-flight — if hits
    // kept arriving often enough, there was always at least one still "just started" the moment another
    // reverted, so the enemy could visibly never stop flickering red even though each individual coroutine
    // was behaving correctly in isolation. A single shared timer just gets EXTENDED by every new hit
    // instead of stacking a fresh independent clock, so the revert only ever happens once, `FlashDuration`
    // after the LAST hit actually landed.
    private void TriggerFlash()
    {
        _flashTimer = FlashDuration;
        if (_isFlashing) return;
        _isFlashing = true;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null && _renderers[i].material != null) _renderers[i].material.color = Color.red;
    }

    // Assigns this life's tier-appropriate materials (EnemySpawnerPD.GetTierMaterials) to the two generic
    // per-tier-colored renderer slots every enemy exposes — _accentRenderers and _invulnerableShellRenderer.
    // Deliberately does NOT touch _renderers (the black body) at all — that stays black across every tier,
    // only flash/stun-tinted, never tier-recolored (see its own field comment). A tier with either material
    // left unset (TierMaterials' own doc comment — scaffolding, filled in incrementally) simply leaves that
    // slot's current material untouched. No-op entirely if EnemySpawnerPD.instance is null (an enemy placed
    // directly in a scene rather than spawned through it never gets ConfigureSpawn called at all, so this is
    // never reached for one anyway — see spawnTier's own default-1 fallback for that case).
    private void ApplyTierMaterials(int tier)
    {
        if (EnemySpawnerPD.instance == null) return;
        var materials = EnemySpawnerPD.instance.GetTierMaterials(tier);

        if (materials.accentMaterial != null && _accentRenderers != null)
            foreach (var r in _accentRenderers)
                if (r != null) r.material = materials.accentMaterial;

        if (materials.shellMaterial != null && _invulnerableShellRenderer != null)
            _invulnerableShellRenderer.material = materials.shellMaterial;
    }

    // Prepends this tier's name prefix (+ separating space) onto _authoredEnemyName (the plain, un-prefixed
    // name — NOT whatever enemyName currently holds, since LateAwake() already reset it back to
    // _authoredEnemyName earlier this same spawn, before ConfigureSpawn ever ran — see that field's own doc
    // comment for why reading off the live field here would be wrong). No-op if EnemySpawnerPD.instance is
    // null, same reasoning as ApplyTierMaterials above.
    private void ApplyTierNamePrefix(int tier)
    {
        if (EnemySpawnerPD.instance == null) return;
        string prefix = EnemySpawnerPD.instance.GetTierNamePrefix(tier);
        enemyName = string.IsNullOrEmpty(prefix) ? _authoredEnemyName : $"{prefix} {_authoredEnemyName}";
    }

    // Live lookup (not cached), for any subclass that spawns/colors its OWN separate object using this
    // enemy's tier identity — e.g. MortarGloomPD/ShooterGloomPD's projectile material, ShielderGloomPD's
    // SpikeShieldPD.readyMaterial — instead of that object hand-authoring its own per-tier material like
    // TierMaterials.accentMaterial replaced on this class's own _accentRenderers. Safe to read any time after
    // ConfigureSpawn has run for this life (spawnTier is stable from then on); null if EnemySpawnerPD.instance
    // is null or this tier hasn't had accentMaterial filled in yet (TierMaterials' own doc comment —
    // scaffolding, filled in incrementally).
    protected Material TierAccentMaterial =>
        EnemySpawnerPD.instance != null ? EnemySpawnerPD.instance.GetTierMaterials(spawnTier).accentMaterial : null;

    // Guards against both a null/empty _renderers array and individual entries lacking a material —
    // .material returns null (not a thrown exception, just a silent null) for a Renderer with no materials
    // assigned, which previously caused a NullReferenceException here and, more importantly, wherever
    // _originalColors[i] later got read against a color that never got cached (EnemyBasePD.Simulate's
    // pooled-reuse init block, FlashCoroutine above).
    private void CacheRenderers()
    {
        _renderers ??= Array.Empty<Renderer>();
        _originalColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
            _originalColors[i] = _renderers[i] != null && _renderers[i].material != null
                ? _renderers[i].material.color
                : Color.white;
    }

    private void KillGraph()
    {
        if (_animator == null) return;
        // Not destroying playableGraph here: Destroyed() fires on every pooled death (not just true
        // destruction), and a destroyed graph can't be recreated by Rebind() on the next reuse — it
        // permanently severs the Animator's connection to its controller. The GameObject is already
        // SetActive(false) while pooled, which already stops it from being ticked at all, so simply
        // disabling the component is sufficient and reuse-safe.
        _animator.enabled = false;
    }

    // ── Pool callbacks ─────────────────────────────────────────────────────
    protected override void OnAddedToPool()
    {
        KillGraph();
    }
}

// Concrete non-generic class for enemies that don't need extra state beyond the base.
[RequireComponent(typeof(PredictedRigidbody))]
public class EnemyBasePD : EnemyBasePD<EnemyBasePD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        // contactCooldowns was never disposed here (pre-existing gap, fixed in passing) — killLaunchHitIds
        // needs the same treatment as every other enemy subclass's own Dispose().
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;
}
