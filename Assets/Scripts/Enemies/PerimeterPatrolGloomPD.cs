using PurrNet.Packing;
using PurrNet.Prediction;
using UnityEngine;

// Genuine EnemyBasePD subclass (health/hurtbox/CC pipeline all reused as-is), NOT a stationary dummy —
// spawned by a perimeter-patrol Event to walk laps around the playable area's outer wall at a fixed
// inward offset, counterclockwise (player weapons orbit clockwise, so this is intentionally the easier
// direction for players to intercept). No target, no attack chain — HandleActiveState below completely
// replaces the normal target-chase body with waypoint-following instead. No CanContactDamage override
// either (2026-08-26) — EnemyBasePD's own contactColliderEnabled field (default true) already controls
// contact damage generically via the "ContactDamageCollider" child's own Collider component, and a prefab
// with no such child never gets a trigger callback to begin with regardless — same reasoning DummyTargetPD
// now follows too (see its own doc comment). This prefab simply not wiring up that child is what actually
// keeps it damage-free; flip contactColliderEnabled / wire up the child later if that's ever wanted.
//
// A completely ordinary (non-kinematic) EnemyBasePD physically, same as any target-chasing enemy — no
// special CC immunity, no permanent kinematic lock (2026-09-13, explicit design reversal: this used to be
// permanently kinematic specifically to be immune to knockback DISPLACEMENT, but that made it feel like a
// different kind of object from every other enemy for no real benefit). Fully affected by knockback now,
// same as anything else — a hit shoving it off its patrol line is fine and expected, since HandleActiveState
// below just keeps aiming fresh at the SAME current waypoint every tick regardless of where a knockback push
// left it, so it self-corrects back toward the route on its own with no extra recovery logic needed.
//
// Moves via the same AddForce(dir * EffectiveMoveSpeed * delta * 5f, ForceMode.VelocityChange) shape
// EnemyBasePD.HandleMovement's own plain (no preferredRange) approach branch uses for a normal chasing
// enemy — HandleMovement itself isn't reused directly (it's built around _target, which this enemy never
// sets), but the movement math is identical, just aimed at the current patrol waypoint instead of a target's
// position.
public class PerimeterPatrolGloomPD : EnemyBasePD<PerimeterPatrolGloomPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public EnemyBaseState b;
        // Index into the (cached, PlayableAreaPD-owned) inset perimeter this enemy is currently walking
        // toward. Advances (wrapping) once within waypointArriveTolerance of it — see HandleActiveState.
        public int waypointIndex;
        public void Dispose() { b.contactCooldowns.Dispose(); b.killLaunchHitIds.Dispose(); }

        public State Duplicate()
        {
            var copy = this;
            copy.b = b.Duplicate();
            return copy;
        }
    }

    protected override ref EnemyBaseState GetBase(ref State state) => ref state.b;

    [Header("Patrol Settings")]
    // Distance at which this enemy advances to the next waypoint in the loop.
    [SerializeField] private float waypointArriveTolerance = 0.5f;

    // Called by the spawning Event right after hierarchy.Create()/ConfigureSpawn — same "mutate right
    // after Create()" convention every other spawned one-shot in this codebase uses. startingWaypointIndex
    // is the waypoint this enemy should walk toward FIRST — the Event doesn't need to spawn this enemy
    // exactly ON that point (see PlayableAreaPD.GetPointAtArcLength, which generally spawns it mid-edge
    // instead, for true arc-length-even spacing across multiple spawns); HandleActiveState below already
    // just walks toward whichever waypoint is current from wherever this enemy actually is.
    public void Launch(int startingWaypointIndex)
    {
        currentState.waypointIndex = startingWaypointIndex;
    }

    // Completely replaces the normal target-chase body — no _target, no HandleMovement call at all.
    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        // HandleMovement's own callers get stun/movement-disabled gating for free elsewhere in the base
        // class — bypassing it entirely here means this override needs its own explicit checks.
        if (state.b.movementDisabled) return;
        if (state.b.stunTimer > 0f) return;
        if (_rigidbody == null) return;

        // Read live every tick, not cached once at Launch() (2026-09-22 fix) — Launch() is only ever called
        // by the spawning Event, same "only runs on whichever machine actually made the spawn decision"
        // caveat as ConfigureSpawn (see EnemyBaseState.scaledMaxHealth's own doc comment) — a machine where
        // this patroller was instead created by PredictedHierarchy reconciling a misprediction would never
        // call Launch() at all, permanently null-guarding HandleActiveState into doing nothing, freezing the
        // patroller in place on that one client forever. GetPatrolGraph() is already cached internally by
        // EventManagerPD and returns the same fixed, hand-authored array on every machine, so reading it
        // live here costs nothing extra and closes the gap completely instead of just narrowing it.
        var perimeter = EventManagerPD.instance != null ? EventManagerPD.instance.GetPatrolGraph() : null;
        if (perimeter == null || perimeter.Length == 0) return;

        Vector3 target = perimeter[state.waypointIndex];
        Vector3 toTarget = target - transform.position;
        toTarget.y = 0f;

        if (toTarget.magnitude <= waypointArriveTolerance)
        {
            // GetInsetPerimeter already guarantees increasing index = counterclockwise — this method never
            // needs its own direction logic.
            state.waypointIndex = (state.waypointIndex + 1) % perimeter.Length;
            target = perimeter[state.waypointIndex];
            toTarget = target - transform.position;
            toTarget.y = 0f;
        }

        Vector3 dir = toTarget.normalized;
        if (dir == Vector3.zero) return;

        Quaternion targetRotation = Quaternion.LookRotation(dir);
        _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, targetRotation, 10f * delta));
        // Same AddForce shape EnemyBasePD.HandleMovement's own plain-approach branch uses for a normal
        // chasing enemy (see class doc comment) — moveSpeed/EffectiveMoveSpeed is this prefab's own tuning
        // knob now, no separate scalar needed.
        _rigidbody.AddForce(dir * (EffectiveMoveSpeed * delta * 5f), ForceMode.VelocityChange);
    }
}
