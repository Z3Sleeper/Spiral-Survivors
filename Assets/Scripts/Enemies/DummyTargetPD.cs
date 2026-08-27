using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Completely stationary, no attack chain, immune to stun/knockback/slow outright (crowd control on something
// that never self-navigates is meaningless, and a permanently-kinematic body must never actually receive a
// physics push regardless — see OnAwake's own comment). Reuses EnemyBasePD's entire generic damage/health/
// death/damage-number/hurtbox/size pipeline as-is — a weapon's hit-detection is hardcoded to IEnemyPD
// everywhere in this codebase (every weapon script, every on-hit augment), so implementing a brand-new
// parallel "neutral/strikeable" interface would mean touching every one of those sites for no real benefit.
// The "neutral, not a real hostile enemy" feel comes mostly from what this class DOESN'T do (no AI/self-
// movement, immune to CC) plus, by design, at the CALL SITE, not in this script: never spawned through
// EnemySpawnerPD, so it never joins ActiveEnemies/spawn-cap tracking or gets targeted by other enemies' own
// logic. Contact damage: no CanContactDamage override here at all (2026-08-26, was a dedicated
// canDealContactDamage Inspector bool) — EnemyBasePD's own contactColliderEnabled field already controls
// this generically (enables/disables the "ContactDamageCollider" child's own Collider component directly,
// see EnemyBasePD.Awake's own transform.Find("ContactDamageCollider") lookup), and a prefab with no
// ContactDamageCollider child at all never gets an OnTriggerEnter/Stay callback to begin with regardless of
// that bool's value — so a second, class-level gate on top was redundant. A dummy prefab that wants contact
// damage just wires up the child and leaves contactColliderEnabled at its own default (true); one that
// doesn't want it just never wires up the child. Spawn it directly via hierarchy.Create() from whatever
// event/system wants one, same as CaptureThePointPD spawns its own zone.
//
// Compatible with OnKillLaunchEnemiesAugment (verified 2026-08-20) — HandleKillLaunch moves a corpse via
// _rigidbody.MovePosition(), the CORRECT Unity API for a kinematic body, not a workaround; StartKillLaunch
// already guards its own velocity-reset behind !_rigidbody.isKinematic, so it gracefully no-ops there too.
// HandleKillLaunch DOES un-kinematic the body once the launch finishes (a generic step, not specific to this
// class) — Simulate's own override below defensively re-locks it the same tick, so this dummy stays
// kinematic for its entire lingering-corpse window too, not just while alive.
//
// Reusable base for any future "neutral, punchable" entity — damage numbers and size-scaled spawn offsets
// (DamageNumberManager) already work identically to any other enemy with zero extra code here, since both
// key off IEnemyPD/EnemyBasePD's own generic Size/TakeDamage plumbing. xpValue (an EnemyBasePD field,
// Inspector-authored) defaults to 1 — set to 0 for a target that grants no XP (ProgressionManager's
// XPOrbManagerPD.SpawnOrbs already no-ops on xpValue <= 0, confirmed, no extra guard needed here) or a
// positive value for one that does.
public class DummyTargetPD : EnemyBasePD<DummyTargetPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    // Empty, not calling base — EnemyBasePD.HandleActiveState's own default body still acquires and chases
    // the nearest player via HandleMovement (see Simulate()'s own FindClosestTarget call, unconditional
    // unless this is overridden). This is what actually makes the dummy stationary; HandleMovement itself is
    // never reached.
    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false) { }

    protected override void OnAwake()
    {
        // Forced here, not left as a per-prefab Inspector toggle — "completely stationary, can't be pushed"
        // is this class's own defining trait, not something a future dummy prefab could accidentally leave
        // unset. stunResistant alone would already be enough (Awake() assigns isStunnable = !stunResistant
        // immediately before calling OnAwake()), but isStunnable is set explicitly too since that's what
        // AddKnockbackVelocity/ApplyStun/ApplySlow actually gate on directly, not stunResistant itself.
        stunResistant = true;
        isStunnable   = false;

        // Permanently kinematic — a one-time physical setup, not routed through predicted state/SetKinematic
        // (that machinery exists for TEMPORARY, resimulation-safe toggle windows like a Windup sub-state;
        // this never changes for this object's entire life, same category as the plain Awake-time _hurtbox/
        // _physicsCollider resolution already happening in EnemyBasePD.Awake() just before this runs).
        // Belt-and-suspenders alongside stunResistant/isStunnable above — those already block
        // AddKnockbackVelocity's velocity write (and its own derived micro-stun) outright; this just
        // guarantees nothing else could ever physically displace it either. See Simulate's own override
        // below for the one path that still un-kinematics it regardless (HandleKillLaunch).
        if (_rigidbody != null) _rigidbody.isKinematic = true;
    }

    protected override void Simulate(ref State state, float delta)
    {
        base.Simulate(ref state, delta);

        // Defensive per-tick re-assertion, same convention EnemyBasePD's own base.Simulate() already uses
        // for externalKinematic just above. The one path that still un-kinematics this dummy despite
        // OnAwake's own permanent lock: EnemyBasePD.HandleKillLaunch un-kinematics the corpse the instant
        // its own travel finishes (a generic step, not specific to this class — see StartKillLaunch/
        // HandleKillLaunch's own doc comments), which would otherwise leave a dummy corpse non-kinematic
        // for however long it lingers before deathHoldCount actually reaches 0 and it gets deleted. Runs
        // AFTER base.Simulate() so it re-locks on the SAME tick HandleKillLaunch un-locked it — cheap (a
        // single bool check) every other tick, when it's already kinematic and this is a no-op.
        if (_rigidbody != null && !_rigidbody.isKinematic) _rigidbody.isKinematic = true;
    }
}
