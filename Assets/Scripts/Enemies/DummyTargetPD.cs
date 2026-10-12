using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Generic abstract base for "neutral, punchable" dummy variants (2026-09-02 restructure) — the exact same
// relationship EnemyBasePD<TState> has to real enemy types: each concrete variant (DummyTargetPD below,
// KnockbackDummyTargetPD, any future one) independently supplies its OWN TState, so a variant that needs
// extra predicted-state fields (e.g. KnockbackDummyTargetPD's own knockbackTimer) just adds them to its own
// State struct — nothing here needs a field for every variant's own mutable data, same reason EnemyBasePD
// itself doesn't. (First attempt at this got it wrong: made the plain DummyTargetPD concrete/non-generic and
// tried to have KnockbackDummyTargetPD inherit from THAT directly, which would have forced a knockbackTimer
// field onto every plain dummy whether it used it or not — caught and corrected before it shipped.)
//
// Completely stationary, no attack chain of its own kind, immune to stun/knockback/slow outright (crowd
// control on something that never self-navigates is meaningless, and a permanently-kinematic body must never
// actually receive a physics push regardless — see OnAwake below). Reuses EnemyBasePD's entire generic
// damage/health/death/damage-number/hurtbox/size pipeline as-is — a weapon's hit-detection is hardcoded to
// IEnemyPD everywhere in this codebase (every weapon script, every on-hit augment), so implementing a
// brand-new parallel "neutral/strikeable" interface would mean touching every one of those sites for no real
// benefit. The "neutral, not a real hostile enemy" feel comes mostly from what this class DOESN'T do (no AI/
// self-movement, immune to CC) plus, by design, at the CALL SITE, not in this script: never spawned through
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
// class) — Simulate's own override below defensively re-locks it the same tick, so a dummy stays kinematic
// for its entire lingering-corpse window too, not just while alive.
public abstract class DummyTargetPD<TState> : EnemyBasePD<TState>
    where TState : struct, IPredictedData<TState>
{
    // Spawn-in rise (2026-10-10) — a Spire looks like it's lifting up out of the ground on spawn. Driven
    // through the YAxisMovers wrapper every enemy prefab already has (everything meant to visually rise
    // lives under it), never EnemyBasePD.Visuals directly (that's continuously re-synced toward the
    // tick-accurate physics root every render frame by PredictedTransform, so a cosmetic offset written
    // there would just get fought/overwritten) and never the physics root itself (that drives the real
    // hurtbox — lifting it would move the actual hit-detection zone, not just the visual). Same wrapper/
    // reasoning as LeaperGloomPD's own _yAxisMovers arc-bob.
    [Header("Visuals")]
    [SerializeField] private Transform _yAxisMovers;

    private Vector3 _yAxisMoversRestPosition;
    private bool    _yAxisMoversRestPositionCaptured;
    private float   _spawnLiftTimer;

    // Fixed, un-augmentable visual flourish — plain consts, not Inspector fields.
    private const float SpawnLiftStartOffsetY = -2f;
    private const float SpawnLiftDuration = 2f;

    protected override void LateAwake()
    {
        base.LateAwake();

        // Guarded to a single real capture per instance, same reasoning as LeaperGloomPD's own identical
        // _yAxisMoversRestPosition capture — an unconditional recapture here could bake in a mid-lift
        // offset as the new "rest" position if this instance ever gets pooled/reused while still rising.
        if (!_yAxisMoversRestPositionCaptured && _yAxisMovers != null)
        {
            _yAxisMoversRestPosition = _yAxisMovers.localPosition;
            _yAxisMoversRestPositionCaptured = true;
        }

        // LateAwake() re-fires on every real spawn/pooled reuse, not just the first — this reliably restarts
        // the rise animation each time a Spire is (re)spawned, snapping straight to the "buried" offset so
        // there's no one-frame pop at the old resting height before UpdateView's own lerp takes over below.
        _spawnLiftTimer = 0f;
        if (_yAxisMovers != null)
            _yAxisMovers.localPosition = _yAxisMoversRestPosition + new Vector3(0f, SpawnLiftStartOffsetY, 0f);
    }

    protected override void UpdateView(TState viewState, TState? verified)
    {
        base.UpdateView(viewState, verified);

        // Purely cosmetic — plain per-client real-time timer, not PredictedIdentity State. This never
        // touches the physics root/hurtbox and has zero gameplay effect, so there's no reason to spend
        // State bandwidth/determinism machinery on it, unlike LeaperGloomPD's own bob (which rides on a
        // real attack-timing State field that already exists for gameplay reasons regardless).
        if (_yAxisMovers != null && _spawnLiftTimer < SpawnLiftDuration)
        {
            bool paused = PausePD.instance != null && PausePD.instance.IsPaused;
            if (!paused) _spawnLiftTimer += Time.deltaTime;

            float t = Mathf.Clamp01(_spawnLiftTimer / SpawnLiftDuration);
            _yAxisMovers.localPosition = _yAxisMoversRestPosition + new Vector3(0f, Mathf.Lerp(SpawnLiftStartOffsetY, 0f, t), 0f);
        }
    }

    protected override void OnAwake()
    {
        // Forced here, not left as a per-prefab Inspector toggle — "completely stationary, can't be pushed"
        // is this whole family's own defining trait, not something a future dummy prefab could accidentally
        // leave unset. stunResistant alone would already be enough (Awake() assigns isStunnable =
        // !stunResistant immediately before calling OnAwake()), but isStunnable is set explicitly too since
        // that's what AddKnockbackVelocity/ApplyStun/ApplySlow actually gate on directly, not stunResistant
        // itself.
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

    protected override void Simulate(ref TState state, float delta)
    {
        base.Simulate(ref state, delta);

        // Defensive per-tick re-assertion, same convention EnemyBasePD's own base.Simulate() already uses
        // for externalKinematic just above. The one path that still un-kinematics any dummy despite OnAwake's
        // own permanent lock: EnemyBasePD.HandleKillLaunch un-kinematics the corpse the instant its own
        // travel finishes (a generic step, not specific to this family — see StartKillLaunch/HandleKillLaunch's
        // own doc comments), which would otherwise leave a dummy corpse non-kinematic for however long it
        // lingers before deathHoldCount actually reaches 0 and it gets deleted. Runs AFTER base.Simulate() so
        // it re-locks on the SAME tick HandleKillLaunch un-locked it — cheap (a single bool check) every
        // other tick, when it's already kinematic and this is a no-op.
        if (_rigidbody != null && !_rigidbody.isKinematic) _rigidbody.isKinematic = true;
    }
}

// The plain, mechanic-free dummy — see DummyTargetPD<TState>'s own doc comment for the full shared shell
// this inherits. xpValue (an EnemyBasePD field, Inspector-authored) defaults to 1 — set to 0 for a target
// that grants no XP (ProgressionManager's XPOrbManagerPD.SpawnOrbs already no-ops on xpValue <= 0, confirmed,
// no extra guard needed here) or a positive value for one that does.
public class DummyTargetPD : DummyTargetPD<DummyTargetPD.State>
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
}
