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
// — only called once the owner's own windup finishes). LateSimulate() does nothing during Claimed (the
// freeze is entirely SetKinematic's job); Pulling/Orbiting share one continuous position formula there
// (straight-line lerp toward the live slot while Pulling, angle advances at a fixed rate while Orbiting) —
// see IEnemyPD.OrbitTarget's own doc comment for why THOSE two are one mechanism, not two separate ones.
public enum EnemyOrbitPhase { None, Claimed, Pulling, Orbiting }

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
    // Whether the specific hit that killed this enemy was a crit — read from an OnEnemyDeath reaction so a
    // kill-triggered effect (e.g. OnKillLaunchEnemiesAugment) can inherit the killing hit's crit status
    // instead of rolling its own independent chance.
    bool WasKilledByCrit { get; }
    string EnemyName { get; }
    // 1 = full time remaining before enraging, 0 = enraged (or enrageTimer isn't configured at all — most
    // enemies). Same generic-regardless-of-TState reasoning as every other member here.
    float EnrageProgress { get; }
    Transform transform { get; }
    // The smoothly render-frame-interpolated visuals child — see EnemyBasePD.Visuals's own doc comment.
    Transform Visuals { get; }
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
    // This enemy's own current damage output — a method, not a property, matching the existing
    // EnemyBasePD.GetDamage() virtual it wraps (some subclasses, e.g. KingSpikePD, situationally compute
    // something other than the raw `damage` field — see that override's own doc comment). Exposed on the
    // interface so a held enemy's own base-class override (see EnemyBasePD.GetDamage()'s own doc comment)
    // can read a DIFFERENT enemy's live value through this same generic surface.
    float GetDamage();
    void TakeDamage(float damage, bool isCrit, PlayerID attackerOwner);
    void AddKnockbackVelocity(Vector3 impulse);
    void Stun(float duration);
    // multiplier is how much of normal move speed remains while active (e.g. 0.5 = half speed), not a
    // separate "50% slower" subtraction — see EnemyBasePD.Slow()/EffectiveMoveSpeed's own doc comments.
    void Slow(float multiplier, float duration);
    // Cancels this enemy's current active-attack-sequence (if any) outright and returns it to Moving/idle —
    // see EnemyBasePD.Interrupt()'s own doc comment. Call this BEFORE a paired Stun()/AddKnockbackVelocity
    // on the same hit (e.g. AreaEffectCastHelper's own interrupt param), never after — see Interrupt()'s
    // own doc comment for why the ordering matters.
    //
    // Returns whether this call actually cancelled something (got past the isDead/CanBeInterrupted guard) —
    // e.g. DashThroughInterruptAugment combines this with IsStunnable to tell a real interrupt (enemy was
    // genuinely mid dash/windup/attack AND the call actually took effect) from a no-op (dead, or a phase
    // explicitly excluded from CanBeInterrupted, like KingSpikePD's SpikeBurstWindup — that phase is both
    // non-stunnable AND non-interruptible, so IsStunnable alone isn't enough to tell the two apart).
    bool Interrupt();
    // Hard on/off switch for this enemy's own normal approach/backpedal movement (HandleMovement) —
    // deliberately generic/public so ANOTHER entity (at least another enemy; nothing restricts it to that)
    // can freeze/unfreeze a DIFFERENT enemy's movement from the outside, e.g. one enemy anchoring/rooting
    // another as part of its own attack. Does NOT touch isStunnable, the rigidbody's kinematic state, attack
    // state machines, or anything else — an enemy with movement disabled still attacks/rotates/telegraphs
    // completely normally, it just never walks. Purely a movement gate; pair it with Stun()/Interrupt()
    // separately if a caller also wants those effects. See EnemyBasePD.HandleMovement's own doc comment for
    // exactly what this gates.
    void SetMovementDisabled(bool disabled);
    void TriggerHitFeedback(float damage, bool isCrit, bool isHighroll, CharacterStatsPD attacker, DamageSource source, bool isPriority = false, bool overrideSfx = false, AudioClip sfxOverride = null);

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

    // Whether this enemy is currently OUT of a CC-protected commitment window (dash/windup/attack) — the same
    // intentional per-tick flag every enemy's own HandleActiveState already maintains to decide whether a Stun
    // can freeze it right now (see each subclass's own isStunnable assignment). NOT sufficient on its own to
    // mean "Interrupt() will actually do something" — some states (e.g. KingSpikePD.SpikeBurstWindup) are both
    // non-stunnable AND explicitly excluded from CanBeInterrupted. See Interrupt()'s own doc comment for why
    // callers combine this with Interrupt()'s own return value. Read-only passthrough of the existing
    // isStunnable field.
    bool IsStunnable { get; }

    // Whether another entity currently controls this enemy's position (pulled/orbiting) — see OrbitTarget's
    // own doc comment. Read-only; callers don't set this directly, it's derived from orbit state.
    bool IsBeingHeld { get; }

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
    // phase: this enemy's OWN current world position (captured here, at the instant of the call) is straight-
    // line-lerped toward `owner`'s live position + the `targetAngleDeg`/`radius` orbit slot, over
    // `pullDuration` seconds — a Cartesian lerp, not a radius-only lerp, so it visibly moves toward wherever
    // it's actually going to end up rather than snapping sideways onto the target angle first. Orbit phase
    // (begins automatically once the pull lerp completes): radius fixed at `radius`, angle advances from
    // `targetAngleDeg` at `angularSpeedDegPerSec`. Does NOT itself set movement-disabled/kinematic/
    // invulnerable — the caller composes those separately (same "small composable primitives" convention as
    // Stun/Interrupt).
    void OrbitTarget(PredictedObjectID owner, Vector3 ownerPosition, float radius, float pullDuration, float angularSpeedDegPerSec, float targetAngleDeg);
    // Ends pulling/orbiting immediately — does not itself touch kinematic/movement-disabled/invulnerable
    // (the caller releasing this enemy is expected to also call SetKinematic(false)/SetMovementDisabled(false)/
    // SetInvulnerable(false) as separate, explicit steps).
    void StopOrbiting();

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
    void ConfigureSpawn(EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier, float moveSpeedMultiplier);

    // Generic death-hold API — see EnemyBasePD.RequestDeathHold's own doc comment. Only safe to call from a
    // verified-state reaction (e.g. CharacterStatsPD.OnEnemyDeath), never from raw Simulate()-context code.
    void RequestDeathHold();
    void ReleaseDeathHold();
    // Starts the on-kill launch effect (OnKillLaunchEnemiesAugment) — see EnemyBasePD.StartKillLaunch's own
    // doc comment. Die() already reserved this effect's death-hold via the killer's killEventCount; the
    // caller doesn't need to request or track one itself, only trust HandleKillLaunch to release it.
    void StartKillLaunch(Vector3 direction, float speed, float distance, float damage, bool isCrit = false, bool isHighroll = false);

    // Called by EnemySpawnerPD (the enemy-targeting hub) whenever ANY player's taunt/invisible state
    // changes — generic regardless of concrete TState, same reason this interface exists at all. See
    // EnemyBasePD<TState>'s own implementation for the actual reaction logic.
    void OnPlayerTauntChanged(CharacterStatsPD player, bool isTaunting);
    void OnPlayerInvisibleChanged(CharacterStatsPD player, bool isInvisible);

    // Difficulty-pacing wipe (e.g. EnemySpawnerPD clearing the field right before a boss spawns) —
    // deliberately NOT a real death: no XP orbs, no RaiseOnEnemyDeath (so no on-kill augment/effect ever
    // reacts to this), no death-hold/kill-launch interaction, no red death-flash. See EnemyBasePD's own
    // implementation for the full reasoning.
    void SilentDespawn();
}

// Per-target contact damage cooldown — contact damage repeats over time (unlike a one-shot attack),
// so each player touching the enemy needs their own independent cooldown, not a single shared one.
public struct ContactCooldown
{
    public PlayerID player;
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
    public bool atPreferredRange;
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

    // See IEnemyPD.OrbitTarget's own doc comment for the full pull->orbit mechanism this drives.
    public int   orbitPhase; // EnemyOrbitPhase
    public PredictedObjectID orbitOwnerId;
    // Changes every tick while Orbiting — CompressedFloat for the same reason SpikeShieldPD.facingAngle/
    // CharacterStatsPD.weaponOrbitAngle already use it (a plain float here would pay PurrNet's full
    // "changed" cost every single tick instead of a quantized/compressed one).
    public CompressedFloat orbitAngleDeg;
    public float orbitRadiusTarget;
    // This enemy's own world position, captured the instant OrbitTarget() is called — the Pulling phase
    // straight-line-lerps FROM this TO the live target slot (owner position + orbitAngleDeg/orbitRadiusTarget);
    // irrelevant once Orbiting.
    public Vector3 orbitPullStartPosition;
    public float orbitPullDuration;
    public float orbitPullElapsed;
    public float orbitAngularSpeedDegPerSec;

    public DisposableList<ContactCooldown> contactCooldowns;
    public bool isDashing;
    public Vector3 dashDirection;
    public Vector3 dashDestination;
    public float    dashSpeed;
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
    public bool initialized;
    // Difficulty-pacing wipe — deliberately separate from isDead, never set alongside it. See
    // IEnemyPD.SilentDespawn's own doc comment for the full reasoning (no XP, no death effects/holds, no
    // red flash — just gone). Reset explicitly on every pooled reuse (Simulate()'s own !b.initialized
    // block), same as isDead, since GetInitialState() alone only covers a genuinely fresh instance.
    public bool wiped;
    // Live, ticking-down countdown — kept separate from EnemyBasePD.enrageTimer (the configured starting
    // duration, a plain never-mutated field) for the same reason WeaponRangedPD's fireTimer isn't just a
    // plain field: anything that changes every tick during Simulate() has to live in predicted State to
    // resimulate correctly, a plain field mutated in-place wouldn't roll back and replay consistently.
    public float enrageCountdown;
    public bool  enraged;

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

    [Header("Identity")]
    public string enemyName = "Enemy";

    [Header("Stats")]
    public float moveSpeed = 6f;
    [NonSerialized] public float baseMoveSpeed;
    // Does nothing on its own here — EnemyBasePD has no generic attack-cooldown system (every subclass owns
    // its own attack timing/state machine entirely independently, see OnEnrage's own doc comment), so a base
    // enemy never reads this at all. A subclass that wants attack speed to matter reads EffectiveAttackSpeed
    // itself wherever it currently uses a flat attack-cooldown/windup duration (e.g. divide by it, same shape
    // as any other rate stat), and how exactly attackSpeed shapes that cadence is left entirely up to it.
    // Hidden from the Inspector (still public + serialized, so every prefab's currently-tuned value is
    // preserved) — same "doesn't read as a real per-prefab setting" reasoning isStunnable's own [HideInInspector]
    // already uses just below: it's slow-interaction infrastructure (see EffectiveAttackSpeed's own doc
    // comment), not something meant to be hand-tuned per enemy in the Inspector.
    [HideInInspector] public float attackSpeed = 1f;
    public float backpedalSpeedDivisor = 3f;
    public float maxHealth = 30f;
    [NonSerialized] public float baseHealth;
    public float preferredRange = 0f;
    public float rangeToleranceFar = 1f;
    public float rangeToleranceClose = 0.3f;
    public float damage = 10f;
    [NonSerialized] public float baseDamage;
    public float xpValue = 1f;
    [NonSerialized] public float baseXpValue;
    // Generic per-enemy size scalar — not tied to any one visual/physics system, just a shared number
    // other generic code (e.g. HandleKillLaunch's sphere-cast radius) can read without needing its own
    // separate per-enemy tunable. Default 1, hand-tuned per enemy prefab.
    public float size = 1f;

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
    private const int   SeparationCheckInterval = 8;
    private const float SeparationRadiusPerSize = 1f;
    private const float SeparationForce = 8f;
    // Cached between checks (in EnemyBaseState.separationPush, not a plain field — see that field's own doc
    // comment) and re-applied as a small continuous force every tick in between — smoother-looking than an
    // abrupt kick only on the tick the check itself runs, and self-decays to zero once neighbors are far
    // enough apart to stop overlapping on the next check.
    private readonly Collider[] _separationOverlapResults = new Collider[16];

    [Header("Contact Damage")]
    public bool contactColliderEnabled = true;
    public float contactCooldown = 0.5f;
    public bool stunResistant = false;
    // Reduces knockback velocity (AddKnockbackVelocity) AND reduces the duration of any other stun source
    // (Stun()) — except the stun knockback itself produces, which is deliberately exempt since that duration
    // is already derived from the already-reduced knockback velocity; applying this resist a second time on
    // top of that would double-dip the same resistance against the same hit. Slow() is the one exception to
    // the "reduces duration" rule — a slow's duration always lands in full; this instead reduces the slow's
    // POTENCY (how much speed it actually takes away), see Slow()'s own doc comment for the exact math.
    [Range(0f, 1f)] public float crowdControlResist = 0f;
    public bool showDamageNumbers = true;
    // Nudges damage number spawn points along world-space +Y/+Z (not this enemy's own orientation) by
    // this many units, so they spawn off the model instead of centered inside it.
    [SerializeField] private float sizeYOffset = 0f;
    [SerializeField] private float sizeZOffset = 0f;

    [Header("Enrage")]
    // 0 = no enrage at all (the default, opt-in only) — anything <= 0 is treated as "not configured" and
    // never ticks or fires OnEnrage(), so this is a no-op for every enemy that doesn't set it.
    public float enrageTimer = 0f;

    // Fires exactly once, the tick enrageCountdown first reaches 0 — stub by default, a subclass decides
    // what enraging actually means for it (King Spike: reassigns its own attackCooldown and changes when
    // its next-attack cooldown gets armed).
    protected virtual void OnEnrage() { }

    protected Transform _target;
    // Mirrors _target — cached alongside it purely so Simulate() can cheaply check "is my current target
    // still alive" every tick (a null/bool check) without re-resolving a CharacterStatsPD from the
    // Transform. Always set together with _target in FindClosestTarget(); never assigned anywhere else.
    // Protected (not private) — subclasses with multi-stage attacks (Spear/Leaper/Dagger/Shooter/KingSpike/
    // Blitz) read this directly at their between-stage transitions to check IsInvisible before deciding
    // whether to reprioritize early.
    protected CharacterStatsPD _targetStats;

    // Public for cross-script reads (AddKnockbackVelocity, Stun()), not because it's meant to be hand-
    // tuned — it's fully derived from stunResistant at Awake() and, for state-driven enemies, reassigned
    // every tick from current attack state. Hidden so it doesn't read as a real Inspector setting
    // alongside stunResistant, which actually is one.
    [HideInInspector] public bool isStunnable = true;
    // IEnemyPD passthrough — see the interface member's own doc comment.
    public bool IsStunnable => isStunnable;
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
    // Shared "should a multi-attack chain skip continuing into another attack" signal — a Slow() now gets
    // the exact same mid-attack treatment a true Stun() does (always lands immediately, IsStunned/IsSlowed
    // read true right away even mid-attack-sequence, never interrupts whatever's already committed, but
    // stops the chain from continuing into another attack) — see IsTrueStunned's own doc comment for the
    // full reasoning, which applies identically to a Slow now. Each subclass's own "continue chaining or
    // fall through to the terminal/post-attack state" checkpoint reads this instead of IsTrueStunned alone.
    public bool IsCrowdControlled => IsTrueStunned || IsSlowed;
    // moveSpeed scaled down by whatever slow is currently active (1f = no active slow) — the only thing
    // HandleMovement's own AddForce calls should read instead of the raw moveSpeed field, so a slow actually
    // affects ordinary chase/backpedal movement. Dash-style burst movement (BlitzGloomPD/DaggerGloomPD/
    // SpearGloomPD/ShooterGloomPD's own dashSpeedMultiplier, KingSpikePD's charge) deliberately reads
    // baseMoveSpeed directly instead, same as it already bypasses even KingSpikePD's own enrage moveSpeed
    // override — a slow affects normal movement, not those separately-tuned burst speeds.
    // virtual so a subclass can layer its own situational multiplier on top (e.g. OrbiterGloomPD's own move-speed
    // boost while holding orbited enemies) via base.EffectiveMoveSpeed * ..., without duplicating
    // HandleMovement's own force/rotation code or mutating the plain moveSpeed field directly every phase
    // transition. No existing enemy overrides this — purely additive.
    public virtual float EffectiveMoveSpeed => moveSpeed * (B.slowTimer > 0f ? B.slowMultiplier : 1f);
    // Same slow-multiply shape as EffectiveMoveSpeed — every slow now doubles as an attack-speed slow (per
    // the user's own call: one shared multiplier drives both), so a subclass just swaps its own flat
    // attackSpeed read for this one wherever it paces its attacks and gets the slow interaction for free.
    // No class-wide "immune" flag exists here on purpose — a phase that should ignore attack speed/slow
    // (e.g. KingSpikePD's SpikeBurstWindup) achieves that simply by never reading this property for that
    // phase's own timer, same as every other CC exception in this codebase (isStunnable toggling,
    // CanBeInterrupted overrides) is a concrete, situated decision rather than a cross-cutting bool.
    public float EffectiveAttackSpeed => attackSpeed * (B.slowTimer > 0f ? B.slowMultiplier : 1f);
    public bool _isDead => B.isDead;
    public bool WasKilledByCrit => B.killedByCrit;

    public static int ActiveCount = 0;
    public static int ActiveNormalCount = 0;
    public static int ActiveEliteCount = 0;
    public static int ActivePlebCount = 0;
    public static int ActiveBossCount = 0;
    // Which spawn-cap bucket this instance counts against — set by whichever spawner created it via
    // ConfigureSpawn(), not authored per-prefab (a single prefab can be spawned as different categories
    // by different spawn calls, e.g. the same enemy used as both a tier's normal and elite variant).
    [NonSerialized] public EnemySpawnCategory spawnCategory = EnemySpawnCategory.Normal;

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

    // NOT virtual — this is the single, uniform gate every subclass's own damage computation passes through
    // before ever reaching a caller. While held (any non-None orbitPhase — see IEnemyPD.OrbitTarget's own
    // doc comment), reads the OWNER's live GetDamage() instead of this enemy's own damage — "using a grabbed
    // enemy like a weapon" (Orbiter's own pull attack) means it should hit as hard as its wielder currently
    // does, not whatever IT was tuned to. Read live every call, never cached/snapshotted — damage isn't
    // State-backed (a plain component field, like moveSpeed/maxHealth/xpValue), so there's no separate
    // "restore the original on release" bookkeeping needed at all: this enemy's own `damage` field (or
    // GetOwnDamage()'s own computed result) is never actually touched, only what GetDamage() ultimately
    // RETURNS changes, and that automatically reverts the instant orbitPhase goes back to None (StopOrbiting,
    // on release or the owner's own death) with nothing left to reset or leak across pooled reuse. Falls
    // back to GetOwnDamage() if the owner no longer resolves (deleted/dead) — defensive only, the owner's
    // own release path is expected to call StopOrbiting on every enemy it's holding before that could ever
    // actually happen.
    //
    // Subclasses override GetOwnDamage() below for their own situational logic (e.g. KingSpikePD's own
    // Charging multiplier), NOT this method — that's what guarantees ANY current or future subclass's own
    // damage override automatically composes correctly with being held, without that subclass needing to
    // remember to re-add its own held-check (which is exactly the gap a plain `virtual GetDamage()` would
    // have left open to a future override silently bypassing this entirely, same as KingSpikePD's own
    // pre-existing override already did before this split).
    public float GetDamage()
    {
        if (B.orbitPhase != (int)EnemyOrbitPhase.None)
        {
            var ownerGo = hierarchy.GetGameObject(B.orbitOwnerId);
            if (ownerGo != null && ownerGo.TryGetComponent(out IEnemyPD owner) && !owner._isDead)
                return owner.GetDamage();
        }
        return GetOwnDamage();
    }

    // Override point for subclass-specific damage computation (e.g. KingSpikePD's own Charging multiplier)
    // — see GetDamage()'s own doc comment for why this exists as a separate method instead of just
    // overriding GetDamage() directly.
    protected virtual float GetOwnDamage() => damage;
    public virtual bool CanContactDamage => true;

    // Cached at the very top of Simulate() every tick (before the paused early-return, since
    // OnContactTrigger can fire from the physics pass regardless of pause state) — OnContactTrigger itself
    // doesn't receive delta as a parameter (it's a PredictedPhysicsCallbacks event, not Simulate() itself),
    // but needs it to convert contactCooldown from seconds to ticks. Safe to read there: delta is a
    // constant tick duration now that nothing manipulates Time.timeScale, so it's identical throughout
    // Simulate() -> DoPhysicsPass() -> LateSimulate() for a given tick regardless of exactly when read.
    private float _currentDelta;

    // Captured EXACTLY once, in Awake() — see that method's own comment for why this exists and the bug it
    // fixes (ConfigureSpawn's multipliers compounding across pooled reuse). baseHealth/baseDamage/
    // baseMoveSpeed/baseXpValue are NOT a safe substitute for this despite the similar name: those get
    // multiplied by ConfigureSpawn too, so they're just as contaminated by a previous pooled life as
    // maxHealth/damage/moveSpeed/xpValue themselves are.
    private float _authoredMaxHealth;
    private float _authoredDamage;
    private float _authoredMoveSpeed;
    private float _authoredXpValue;

    [SerializeField] private Renderer[] _renderers;
    // Optional per-prefab visual shown while IsInvulnerable — same shape/precedent as CharacterStatsPD's own
    // _invincibilityShell. Left unassigned on prefabs that don't need one (nothing breaks — see UpdateView's
    // own null-guarded toggle).
    [SerializeField] private GameObject invulnerableShell;
    private Color[] _originalColors;
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
    // 0 for any enemy that doesn't configure enrageTimer at all (most enemies) — explicit "empty," not
    // "full," so it doesn't visually imply an enrage that will never happen.
    public float EnrageProgress => enrageTimer <= 0f ? 0f : Mathf.Clamp01(_verifiedEnrageCountdown / enrageTimer);

    public float MaxHealth => maxHealth;

    public string EnemyName => enemyName;

    public PredictedObjectID ObjectId => id.objectId;

    // ── State ──────────────────────────────────────────────────────────────
    protected override TState GetInitialState()
    {
        var s = default(TState);
        ref var b = ref GetBase(ref s);
        b.health = maxHealth;
        b.contactCooldowns = DisposableList<ContactCooldown>.Create(4);
        b.killLaunchHitIds = DisposableList<PredictedObjectID>.Create(4);
        b.enrageCountdown = enrageTimer;
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
        // ever returned to a spawner, so nothing has called ConfigureSpawn yet. See these 4 fields' own doc
        // comment for why LateAwake() (which DOES re-fire on every pooled reuse) resets maxHealth/damage/
        // moveSpeed/xpValue back to these every life, instead of just trusting whatever they currently hold.
        _authoredMaxHealth = maxHealth;
        _authoredDamage    = damage;
        _authoredMoveSpeed = moveSpeed;
        _authoredXpValue   = xpValue;
        isStunnable = !stunResistant;

        CacheRenderers();
        _animator = GetComponentInChildren<Animator>();
    }

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
        // Reset to the true prefab-authored baseline BEFORE anything below reads maxHealth/damage/
        // moveSpeed/xpValue, and BEFORE the spawner's own ConfigureSpawn() call (which always runs after
        // LateAwake(), once hierarchy.Create() returns) gets a chance to multiply them again — LateAwake()
        // re-fires on every pooled reuse (unlike Awake(), which only captured _authoredMaxHealth/etc. once),
        // so without this, a pooled slot's stats compounded further every time it got reused (e.g. a Tier 2
        // enemy reading 750, then 3750, health across successive lives instead of a stable 150 — see
        // _authoredMaxHealth's own doc comment for the full bug). baseHealth/baseDamage/baseMoveSpeed/
        // baseXpValue reset here too, for the same reason — they're just as multiplied/contaminated by a
        // previous life's ConfigureSpawn call as the main 4 fields are.
        maxHealth     = _authoredMaxHealth;
        damage        = _authoredDamage;
        moveSpeed     = _authoredMoveSpeed;
        xpValue       = _authoredXpValue;
        baseHealth    = _authoredMaxHealth;
        baseDamage    = _authoredDamage;
        baseMoveSpeed = _authoredMoveSpeed;
        baseXpValue   = _authoredXpValue;

        ActiveCount++;
        AdjustCategoryCount(spawnCategory, +1);
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

        // Declumping seed — re-rolled on every real spawn AND every pooled reuse (LateAwake() re-fires on
        // both, unlike OnRespawnReset() which only some subclasses' own overrides chain back to base for) —
        // see EnemyBaseState.separationCheckTimer/personalPushAngle's own doc comments.
        ref var declumpState = ref GetBase(ref currentState);
        declumpState.separationCheckTimer = GameRandom.instance.Next(0, SeparationCheckInterval);
        declumpState.personalPushAngle    = GameRandom.instance.NextFloat(0f, 360f);

        EnemySpawnerPD.instance?.RegisterEnemy(this);
    }

    private PredictedEvent<HitFeedbackData> _onHitFeedback;

    private void OnHitFeedback(HitFeedbackData data)
    {
        if (data.target.TryGetComponent<IEnemyPD>(predictionManager, out var enemy))
            enemy.TriggerHitFeedback(data.damage, data.isCrit, data.isHighroll, data.attacker, data.source, data.isPriority, data.overrideSfx, data.sfx);
    }

    private void OnContactTrigger(GameObject other)
    {
        if (!CanContactDamage) return;
        if (IsStunned) return;
        ref var b = ref B;
        if (b.isDead) return;

        var stats = other.GetComponent<CharacterStatsPD>();
        if (stats == null || !stats.owner.HasValue) return;

        var player = stats.owner.Value;
        var cooldowns = b.contactCooldowns;
        int ticks = Mathf.Max(1, Mathf.RoundToInt(contactCooldown / _currentDelta));

        for (int i = 0; i < cooldowns.Count; i++)
        {
            if (!cooldowns[i].player.Equals(player)) continue;
            if (cooldowns[i].remaining > 0) return;
            cooldowns[i] = new ContactCooldown { player = player, remaining = ticks };
            stats.TakeDamage(GetDamage());
            return;
        }

        cooldowns.Add(new ContactCooldown { player = player, remaining = ticks });
        stats.TakeDamage(GetDamage());
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
            b.orbitPhase               = (int)EnemyOrbitPhase.None;

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

        // Unconditional, ahead of the stun-freeze return below — declumping is a passive spatial correction,
        // not an "action" a stun/attack-lock should block, and it should keep working even for an enemy
        // that's currently stunned or mid-attack. See HandleSeparation's own doc comment.
        HandleSeparation(ref b, delta);

        // Independent of stunTimer below — a slow still counts down while stunned (see the field's own doc
        // comment), unlike trueStunTimer which only ticks inside the stun branch itself.
        if (b.slowTimer > 0f) b.slowTimer -= delta;

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

        // enrageTimer <= 0 means "not configured" for this enemy — never ticks, OnEnrage() never fires,
        // a no-op for every enemy that doesn't set it. Same "don't tick while stunned" behavior as old
        // BossBase. !b.enraged guards it to firing exactly once.
        if (!b.enraged && enrageTimer > 0f)
        {
            b.enrageCountdown -= delta;
            if (b.enrageCountdown <= 0f)
            {
                b.enraged = true;
                OnEnrage();
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

        if (!b.isDashing)
            HandleActiveState(ref state, delta, wasDashing);

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
        if (b.isDead) return;
        // None: not held at all. Claimed: held, but SetKinematic(true) is what's actually freezing this
        // enemy in place — there's no position math to run yet, OrbitTarget() hasn't been called (see
        // IEnemyPD.ClaimForOrbit's own doc comment for why these are deliberately separate calls).
        if (b.orbitPhase == (int)EnemyOrbitPhase.None || b.orbitPhase == (int)EnemyOrbitPhase.Claimed) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        var ownerGo = hierarchy.GetGameObject(b.orbitOwnerId);
        if (ownerGo == null || !ownerGo.TryGetComponent(out IEnemyPD owner) || owner._isDead)
        {
            // Defensive only — the owner's own Die()/OnInterrupted() (or equivalent) are expected to
            // proactively StopOrbiting() every enemy it's holding the instant it goes away. This just guards
            // against some future teardown path that skips that call, so a held enemy never gets stuck
            // orbiting a GameObject that no longer resolves.
            b.orbitPhase = (int)EnemyOrbitPhase.None;
            return;
        }

        Vector3 ownerPos = owner.transform.position;

        // Slot position is always computed the same way regardless of phase — the caller-assigned angle
        // (fixed during Pulling, advancing during Orbiting) at orbitRadiusTarget from the owner's current
        // position. Pulling only differs in that the enemy hasn't arrived there yet (see the Lerp below).
        if (b.orbitPhase == (int)EnemyOrbitPhase.Orbiting)
            b.orbitAngleDeg = WrapOrbitAngle(b.orbitAngleDeg + b.orbitAngularSpeedDegPerSec * delta);

        float rad = b.orbitAngleDeg * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
        Vector3 slotPos = ownerPos + dir * b.orbitRadiusTarget;

        Vector3 pos;
        if (b.orbitPhase == (int)EnemyOrbitPhase.Pulling)
        {
            b.orbitPullElapsed += delta;
            float t = Mathf.Clamp01(b.orbitPullElapsed / b.orbitPullDuration);
            // Straight-line (Cartesian) lerp from wherever this enemy actually started to the live slot
            // position — NOT a radius-at-fixed-angle lerp — so it visibly travels toward where it's really
            // going instead of snapping sideways onto the target angle the instant the pull begins. See
            // IEnemyPD.OrbitTarget's own doc comment.
            pos = Vector3.Lerp(b.orbitPullStartPosition, slotPos, t);
            if (t >= 1f) b.orbitPhase = (int)EnemyOrbitPhase.Orbiting;
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

        // Stops attack telegraphs immediately on death — see OnDeathStop's own doc comment for why this is
        // deliberately NOT gated on deathHoldCount the way the render-hide block below is. Deliberately NOT
        // gated on activeInHierarchy either (previously was, briefly) — a windup indicator that's been
        // unparented to world root (LeaperGloomPD's, during Leaping) is NOT a child of this GameObject, so
        // it doesn't get implicitly hidden when this enemy itself is deactivated/pooled; if this enemy gets
        // hidden and pooled before ever becoming active again this life (entirely possible — hide only
        // waits on deathHoldCount, not on this instance passing through an active frame first), gating
        // OnDeathStop() here meant it could simply never fire, leaving that detached indicator visible
        // forever. Individual overrides that call Animator.Play() guard THAT specific call's own
        // activeInHierarchy instead (seeing SpearGloomPD's own OnDeathStop for why), so the indicator-hide
        // half of OnDeathStop always runs immediately while only the animator snap can harmlessly skip.
        if (viewB.isDead && !_deathStopped)
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
        bool wantStunTint = !viewB.isDead && !_isFlashing && viewB.trueStunTimer > 0f;
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
                if (GameManagerPD.instance != null) GameManagerPD.instance.AwardUpgradePoints(xpValue);
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
        if (!CharacterStatsPD.AnyValidTargetExists) return;

        float nearestAny = float.MaxValue;
        Transform anyTarget = null;
        CharacterStatsPD anyStats = null;

        float nearestTaunting = float.MaxValue;
        Transform tauntingTarget = null;
        CharacterStatsPD tauntingStats = null;

        foreach (var p in CharacterStatsPD.All.Values)
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
    private static readonly List<CharacterStatsPD> _randomTargetScratch = new();

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

        _randomTargetScratch.Clear();
        foreach (var p in CharacterStatsPD.All.Values)
            if (!p.IsDead && p.IsTaunting) _randomTargetScratch.Add(p);

        if (_randomTargetScratch.Count == 0)
        {
            // Nobody taunting to override anything — exclude whoever we were just targeting (the actual
            // "not the last target" request), still respecting invisibility since nothing here overrides it.
            foreach (var p in CharacterStatsPD.All.Values)
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

    // Called by EnemySpawnerPD (the enemy-targeting hub) the instant any player's taunt state changes —
    // immediate reaction, not polled. Taunt starting overrides everything (even that player's own
    // invisibility) — every enemy snaps straight onto them. Taunt ending only matters if that player is
    // ALSO currently invisible: they lose their taunt-granted immunity, so anyone targeting them needs to
    // retarget away right now instead of waiting to notice later.
    public void OnPlayerTauntChanged(CharacterStatsPD player, bool isTaunting)
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

    // Called by EnemySpawnerPD the instant any player's invisible state changes. Becoming visible again
    // never needs a forced reaction (nobody was wrongly avoiding them). Becoming invisible only matters to
    // enemies currently targeting them, and only if they're not ALSO taunting (taunt trumps invisible).
    public void OnPlayerInvisibleChanged(CharacterStatsPD player, bool isInvisible)
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
    private void RetargetAwayFrom(CharacterStatsPD invalidPlayer)
    {
        if (_targetStats != invalidPlayer) return;
        _target = null;
        _targetStats = null;
        FindClosestTarget();
    }

    protected virtual void HandleMovement(ref TState state, float delta)
    {
        if (_target == null) return;

        ref var b = ref GetBase(ref state);
        // See IEnemyPD.SetMovementDisabled's own doc comment — a hard gate on the normal approach/backpedal
        // movement this method drives (both the facing-rotation and the AddForce calls below), set by
        // another entity (typically a different enemy) rather than this enemy's own state machine. Attack
        // sequences already go kinematic/collider-disabled through their own separate mechanisms during a
        // windup/dash/etc., so this is specifically for "keep this enemy otherwise fully alive and attacking
        // normally, just don't let it walk" — a different, narrower effect than Stun()/Interrupt().
        if (b.movementDisabled) return;
        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0;
        Vector3 dir = toTarget.normalized;

        if (dir != Vector3.zero)
        {
            Quaternion targetRotation = Quaternion.LookRotation(dir);
            _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, targetRotation, 10f * delta));
        }

        float distToTarget = Vector3.Distance(transform.position, _target.position);

        if (preferredRange > 0)
        {
            if (b.atPreferredRange)
            {
                if (distToTarget > preferredRange + rangeToleranceFar)
                {
                    b.atPreferredRange = false;
                    _rigidbody.AddForce(dir * (EffectiveMoveSpeed * delta * 5f), ForceMode.VelocityChange);
                }
                else if (distToTarget < preferredRange - rangeToleranceClose)
                {
                    b.atPreferredRange = false;
                    _rigidbody.AddForce(-dir * (EffectiveMoveSpeed / backpedalSpeedDivisor * delta * 5f), ForceMode.VelocityChange);
                }
            }
            else
            {
                if (distToTarget > preferredRange)
                    _rigidbody.AddForce(dir * (EffectiveMoveSpeed * delta * 5f), ForceMode.VelocityChange);
                else if (distToTarget < preferredRange)
                    _rigidbody.AddForce(-dir * (EffectiveMoveSpeed / backpedalSpeedDivisor * delta * 5f), ForceMode.VelocityChange);

                if (Mathf.Abs(distToTarget - preferredRange) <= 0.1f)
                    b.atPreferredRange = true;
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
        b.separationCheckTimer--;
        if (b.separationCheckTimer <= 0)
        {
            b.separationCheckTimer = SeparationCheckInterval;
            RecomputeSeparationPush(ref b);
        }

        // isKinematic-guarded — a kinematic window (windup/charge/leap, several subclasses have one) can't
        // take a velocity-change force at all; the next check after that window ends picks the push back up.
        if (b.separationPush != Vector3.zero && _rigidbody != null && !_rigidbody.isKinematic)
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

        int n = Physics.OverlapSphereNonAlloc(transform.position, radius, _separationOverlapResults, CombatLayerMasks.EnemyHurtboxMask);
        if (n <= 0) return;

        Vector3 combined = Vector3.zero;
        bool anyNeighbor = false;

        for (int i = 0; i < n; i++)
        {
            var other = _separationOverlapResults[i].GetComponent<IEnemyPD>();
            if (other == null || other._isDead || other.transform == transform) continue;

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
        b.separationPush = combined * SeparationForce;
    }

    // ── Dash helpers (for subclasses) ──────────────────────────────────────
    // speed here is expected to already exclude attack-speed scaling (e.g. baseMoveSpeed * a subclass's own
    // dashSpeedMultiplier) — HandleDash below applies EffectiveAttackSpeed live, every tick, on top of
    // whatever gets stored here, so every dasher gets live-reactive dash speed for free without needing to
    // pre-multiply it in themselves or re-issue a new StartDash call when attack speed changes mid-dash.
    protected void StartDashToTarget(ref TState state, float speed, float stopDistance)
    {
        if (_target == null) return;
        ref var b = ref GetBase(ref state);
        Vector3 dir = (_target.position - transform.position);
        dir.y = 0f;
        dir.Normalize();
        b.isDashing = true;
        b.dashDirection = dir;
        b.dashDestination = _target.position - dir * stopDistance;
        b.dashDestination.y = transform.position.y;
        b.dashSpeed = speed;
        if (_physicsCollider != null) _physicsCollider.enabled = false;
    }

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

        // Live, not baked in at dash-start — see StartDashToTarget/StartDashToPoint's own doc comment.
        _rigidbody.velocity = b.dashDirection * b.dashSpeed * EffectiveAttackSpeed;
        _rigidbody.MoveRotation(Quaternion.LookRotation(b.dashDirection));
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
    public virtual void TakeDamage(float damage, bool isCrit = false, PlayerID attackerOwner = default)
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
        // elite enemy — both are already the same 0.2 multiplier, so summing the two REDUCED values below
        // is equivalent to reducing the (unreduced) sum, no separate boss-combined constant needed.
        // critExecuteThreshold is ADDITIVE on top of executeThreshold on a crit, not an independent
        // trip-wire — e.g. 25% execute + 15% crit-execute means non-crits execute at 25%, crits execute at
        // the combined 40%. A non-crit hit only ever sees the plain executeThreshold.
        if (b.health > 0f && CharacterStatsPD.All.TryGetValue(attackerOwner, out var attackerStats) && attackerStats.Execute)
        {
            bool isBoss = spawnCategory == EnemySpawnCategory.Boss;

            float executeThresh = isBoss ? attackerStats.BossExecuteThreshold : attackerStats.executeThreshold;
            if (isCrit)
                executeThresh += isBoss ? attackerStats.BossCritExecuteThreshold : attackerStats.critExecuteThreshold;

            if (executeThresh > 0f && b.health <= maxHealth * executeThresh)
                b.health = 0f;
        }

        if (b.health <= 0)
        {
            b.health = 0;
            // Captured here, not inferred later — Die()/OnEnemyDeath need to know whether the specific hit
            // that actually killed this enemy was a crit (e.g. OnKillLaunchEnemiesAugment inheriting crit
            // status rather than rolling its own), and isCrit is only in scope as this call's own parameter.
            b.killedByCrit = isCrit;
            Die();
        }
    }

    // isPriority: true from actual weapon hits, false (default) from onhit-effect/Phantom Blade sources —
    // jumps this hit's damage number to the front of DamageNumberManager's queue for this enemy+attacker
    // ahead of whatever's already waiting, so real weapon damage always reads first regardless of how many
    // onhit effects also fired off the same hit.
    public void TriggerHitFeedback(float damage, bool isCrit, bool isHighroll, CharacterStatsPD attacker, DamageSource source, bool isPriority = false, bool overrideSfx = false, AudioClip sfxOverride = null)
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
        if (showDamageNumbers && !B.invulnerable) ShowDamageNumber(damage, attacker.owner ?? default, source, isCrit, isHighroll, isPriority);
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
            XPOrbManagerPD.instance.SpawnOrbs(hierarchy, transform.position, xpValue);
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
                enemy.TakeDamage(b.killLaunchDamage, b.killLaunchIsCrit, b.killerOwner);
                // TakeDamage alone never shows a damage number (see TriggerHitFeedback's own doc comment on
                // IEnemyPD) — fired through _onHitFeedback rather than a queue (see HitFeedbackData's own
                // doc comment for why a plain queue here was resimulation-unsafe).
                if (CharacterStatsPD.All.TryGetValue(b.killerOwner, out var killLaunchAttacker))
                    _onHitFeedback.Invoke(new HitFeedbackData { target = enemy.ObjectId, damage = b.killLaunchDamage, isCrit = b.killLaunchIsCrit, isHighroll = b.killLaunchIsHighroll, attacker = killLaunchAttacker, isPriority = false, source = DamageSource.KillLaunch });
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
    // Interrupt()'s own doc comment for why the ordering matters. KnightAreaStunHealAbilityPD/
    // ShieldBreakBurstAugment do this today (via AreaEffectCastHelper's own interrupt param); not every
    // future true stun needs to.
    public void Stun(float duration) => ApplyStun(duration * (1f - crowdControlResist), isTrueStun: true);

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
    // ArcherArrowRainAbilityPD's own AreaEffectPower/AbilityPower slow scaling uses, so resist stacks with
    // it the same intuitive way (a resist can only ever pull a slow BACK toward 1x, never past it).
    //
    // Also now doubles as an attack-speed slow — see EffectiveAttackSpeed's own doc comment — since this is
    // the one shared multiplier both EffectiveMoveSpeed and EffectiveAttackSpeed read.
    //
    // Same "always lands, even mid-attack-sequence" treatment as a true Stun() (per the user's own call — a
    // Slow no longer gets blocked by isStunnable/attack-state at all). It still never interrupts whatever
    // attack is already committed (nothing here touches HandleActiveState/HandleDash), it just means
    // IsSlowed reads true immediately regardless of attack state, and — via IsCrowdControlled — stops a
    // multi-attack chain from continuing into another attack the same way a true stun does.
    public void Slow(float multiplier, float duration)
    {
        float slowAmount = 1f - multiplier;
        float resistedAmount = slowAmount * (1f - crowdControlResist);
        ApplySlow(1f - resistedAmount, duration);
    }

    private void ApplySlow(float multiplier, float duration)
    {
        if (_isDead || stunResistant) return;

        // 90% is the max possible slow (10% speed floor) — clamped here, not at each call site, so any
        // caller can safely send an arbitrarily extreme value (e.g. a stacked 999% slow from AreaEffectPower/
        // AbilityPower scaling) without needing its own clamp, and so the strength-comparison logic right
        // below always compares already-clamped values.
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
    public void OrbitTarget(PredictedObjectID owner, Vector3 ownerPosition, float radius, float pullDuration, float angularSpeedDegPerSec, float targetAngleDeg)
    {
        ref var b = ref B;

        b.orbitPullStartPosition    = transform.position;
        b.orbitAngleDeg             = targetAngleDeg;
        b.orbitOwnerId              = owner;
        b.orbitRadiusTarget         = radius;
        b.orbitPullDuration         = Mathf.Max(0.0001f, pullDuration);
        b.orbitPullElapsed          = 0f;
        b.orbitAngularSpeedDegPerSec = angularSpeedDegPerSec;
        b.orbitPhase                = (int)EnemyOrbitPhase.Pulling;
    }

    public void StopOrbiting()
    {
        B.orbitPhase   = (int)EnemyOrbitPhase.None;
        B.orbitOwnerId = default;
    }

    public EnemySpawnCategory SpawnCategory => spawnCategory;

    // Override to reset this enemy's own attack-sequence state machine back to Moving/idle (typically its
    // own EnterMoving(ref currentState), same terminal transition its own attack-chain already uses when it
    // naturally runs out of attacks) — see Interrupt()'s own doc comment. No-op by default.
    protected virtual void OnInterrupted() { }

    // False while in a phase Interrupt() should never be able to cancel — true (interruptible) by default
    // for every other attack phase. Only two phases in the whole roster are exempt (per design): LeaperGloomPD
    // mid-Leaping (its own override) and KingSpikePD's SpikeBurstWindup/burst itself (its own override) —
    // every other windup/dash/stab/spin/charge is fair game.
    protected virtual bool CanBeInterrupted => true;

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
    public void ConfigureSpawn(EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier, float moveSpeedMultiplier)
    {
        if (category != spawnCategory)
        {
            AdjustCategoryCount(spawnCategory, -1);
            spawnCategory = category;
            AdjustCategoryCount(spawnCategory, +1);
        }

        xpValue *= xpMultiplier;
        baseXpValue *= xpMultiplier;

        if (healthMultiplier != 1f)
        {
            maxHealth *= healthMultiplier;
            baseHealth *= healthMultiplier;
            B.health = maxHealth;
        }

        if (damageMultiplier != 1f)
        {
            damage *= damageMultiplier;
            baseDamage *= damageMultiplier;
        }

        if (moveSpeedMultiplier != 1f)
        {
            moveSpeed *= moveSpeedMultiplier;
            baseMoveSpeed *= moveSpeedMultiplier;
        }
    }

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

    private void ShowDamageNumber(float amount, PlayerID attackerOwner, DamageSource source, bool isCrit = false, bool isHighroll = false, bool isPriority = false)
    {
        // Passes this (IEnemyPD), not a bare Transform — DamageNumberManager/DamageNumber read .Visuals off
        // it for position (smoothly interpolated PredictedTransform graphics child, not the raw tick-
        // stepped physics root) and .SpawnGeneration to detect pooled reuse: a raw Transform reference
        // alone can't tell a still-alive enemy apart from this same pooled GameObject having since been
        // reused for a completely different spawn, which was causing already-tracking numbers to snap
        // away/disappear when their original enemy died and got recycled before their animation finished.
        // The passed stackKey (enemy + attacking player) is the STACK this hit's damage number belongs to —
        // DamageNumberManager itself combines it with `source` to get the actual accumulation group; see its
        // own class doc comment for the full rule.
        if (DamageNumberManager.instance != null)
            DamageNumberManager.instance.Spawn(amount, this, sizeYOffset, sizeZOffset, HashCode.Combine(GetInstanceID(), attackerOwner.GetHashCode()), source, isCrit, isHighroll, attackerOwner, isPriority);
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
