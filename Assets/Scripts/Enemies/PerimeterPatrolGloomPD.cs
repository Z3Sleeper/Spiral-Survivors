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
// Permanently kinematic (OnAwake) so it's immune to knockback DISPLACEMENT — same one-time-permanent-lock
// precedent DummyTargetPD.OnAwake() already establishes — but deliberately does NOT set
// stunResistant/isStunnable the way DummyTargetPD does, since this enemy should still be affected by
// Stun/Slow and the micro-stun a knockback hit derives. Confirmed via EnemyBasePD.AddKnockbackVelocity's
// own implementation: the velocity write is gated behind `!_rigidbody.isKinematic`, but the derived
// ApplyStun() call right after it runs unconditionally regardless of kinematic state — so "immune to the
// push, not the stun" is already exactly how a permanently-kinematic, still-stunnable enemy behaves, no
// changes needed to AddKnockbackVelocity/Stun/Slow themselves.
//
// A permanently-kinematic rigidbody can't be moved via AddForce/velocity at all (Unity no-ops it), so
// HandleActiveState below moves it with _rigidbody.MovePosition() instead — the correct API for a
// kinematic body, same one EnemyBasePD.HandleKillLaunch already uses to move a kinematic corpse.
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
    // Separate tuning knob from the normal EffectiveMoveSpeed-driven AddForce system every other enemy
    // uses — this one moves via MovePosition instead (see class doc comment), which needs its own scale
    // to feel right rather than assuming the same numbers translate directly.
    [SerializeField] private float moveSpeedScalar = 1f;
    // Distance at which this enemy advances to the next waypoint in the loop.
    [SerializeField] private float waypointArriveTolerance = 0.5f;

    // Cached once in Launch() below — re-fetching PlayableAreaPD.GetInsetPerimeter every tick would be
    // wasteful even though that method is itself cached internally.
    private Vector3[] _perimeter;

    protected override void OnAwake()
    {
        base.OnAwake();
        // Permanent — see class doc comment. Deliberately does NOT touch stunResistant/isStunnable.
        if (_rigidbody != null) _rigidbody.isKinematic = true;
    }

    // Called by the spawning Event right after hierarchy.Create()/ConfigureSpawn — same "mutate right
    // after Create()" convention every other spawned one-shot in this codebase uses. startingWaypointIndex
    // is the waypoint this enemy should walk toward FIRST — the Event doesn't need to spawn this enemy
    // exactly ON that point (see PlayableAreaPD.GetPointAtArcLength, which generally spawns it mid-edge
    // instead, for true arc-length-even spacing across multiple spawns); HandleActiveState below already
    // just walks toward whichever waypoint is current from wherever this enemy actually is.
    //
    // Reads EventManagerPD's own hand-authored patrolGraph (2026-08-27 — was
    // PlayableAreaPD.GetInsetPerimeter(wallOffset), which is only correct for a convex outerBoundary; see
    // that method's own doc comment for why the whole procedural-placement approach was abandoned
    // project-wide). No wallOffset parameter anymore — the hand-placed graph already accounts for wall
    // clearance, there's no margin math left to compute here.
    public void Launch(int startingWaypointIndex)
    {
        _perimeter = EventManagerPD.instance != null ? EventManagerPD.instance.GetPatrolGraph() : null;
        currentState.waypointIndex = startingWaypointIndex;
    }

    // Completely replaces the normal target-chase body — no _target, no HandleMovement call at all.
    protected override void HandleActiveState(ref State state, float delta, bool dashJustEnded = false)
    {
        // HandleMovement's own callers get stun/movement-disabled gating for free elsewhere in the base
        // class — bypassing it entirely here means this override needs its own explicit checks.
        if (state.b.movementDisabled) return;
        if (state.b.stunTimer > 0f) return;
        if (_perimeter == null || _perimeter.Length == 0) return;
        if (_rigidbody == null) return;

        Vector3 target = _perimeter[state.waypointIndex];
        Vector3 toTarget = target - transform.position;
        toTarget.y = 0f;

        if (toTarget.magnitude <= waypointArriveTolerance)
        {
            // GetInsetPerimeter already guarantees increasing index = counterclockwise — this method never
            // needs its own direction logic.
            state.waypointIndex = (state.waypointIndex + 1) % _perimeter.Length;
            target = _perimeter[state.waypointIndex];
            toTarget = target - transform.position;
            toTarget.y = 0f;
        }

        Vector3 dir = toTarget.normalized;
        if (dir == Vector3.zero) return;

        Quaternion targetRotation = Quaternion.LookRotation(dir);
        _rigidbody.MoveRotation(Quaternion.Slerp(transform.rotation, targetRotation, 10f * delta));
        _rigidbody.MovePosition(transform.position + dir * (EffectiveMoveSpeed * moveSpeedScalar * delta));
    }
}
