using PurrNet.Prediction;
using UnityEngine;

// Generic, reusable "line from a fixed point, sweeping to track an aim direction" windup/telegraph
// indicator — spawned via hierarchy.Create() (not Instantiate()) specifically so a sibling PredictedTransform
// component on this same prefab actually ticks and smooths this object's pose for rendering
// (PredictedTransform is itself a PredictedIdentity — it only registers/ticks for objects spawned through
// PurrNet's own hierarchy system, never for a plain Instantiate() clone). Root position is set ONCE (at
// hierarchy.Create()'s own spawn position, the line's near/back edge) and never touched again — only rotation
// changes per tick (2026-08-22, see SetAim's own doc comment for why: an indicator anchored to a stationary
// point only ever needs to sweep, and isolating rotation-only interpolation is a deliberately narrower test
// than the original position+rotation-every-tick shape). This script owns nothing but pose — no timing, no
// target-tracking, no fill/UI. The caller (any enemy) is fully responsible for calling SetAim() every tick it
// wants this shown, and Despawn() the moment it's done with it — there is no self-expiry, so a caller that
// stops calling either just leaves this visually frozen (never disappearing) or leaked (never returning to
// the pool).
public class LineIndicatorPD : PredictedIdentity<LineIndicatorPD.State>
{
    public struct State : IPredictedData<State>
    {
        public void Dispose() { }
    }

    // Cached child reference, not owned/driven by this script — a caller that wants to show windup progress
    // reads this and calls SetProgress() itself every frame it needs to, same as it already drives SetAim()
    // and Despawn(). Cached once in Awake() (fires once per underlying GameObject, not per hierarchy.Create()
    // reuse — SetActive(true)/(false) via the pool never re-fires it) — safe since the child never changes
    // across reuses, same prefab structure every time.
    public WindupFillPD Fill { get; private set; }

    // Child holding the actual rendered line mesh, offset forward from this root by half the line's own span
    // — rotating THIS root (or its PredictedTransform-smoothed Visuals copy) sweeps the offset mesh around
    // the fixed root position, same effect as re-deriving a moving center point every tick without actually
    // needing to. Sized/offset once per spawn (Launch), never touched again — span is a per-caller constant
    // (e.g. KingOrbiterPD.shootingRange), not something that varies tick to tick.
    [SerializeField] private Transform pivot;

    private void Awake()
    {
        Fill = GetComponentInChildren<WindupFillPD>(true);
    }

    protected override State GetInitialState() => default;

    // Called once immediately after hierarchy.Create() returns — same "mutate right after Create()" pattern
    // as every other spawned effect in this codebase (RemoteSpikeBurstPD.Launch(), MortarHazardPD.Launch()).
    // Root position/initial rotation already came from hierarchy.Create()'s own spawn params — this only
    // sizes the pivot's fixed local offset, which SetAim never touches again.
    public void Launch(float span)
    {
        if (pivot == null) return;
        pivot.localPosition = new Vector3(0f, 0f, span * 0.5f);
        var scale = pivot.localScale;
        scale.z = span * 0.1f;
        pivot.localScale = scale;
    }

    // Own-identity self-write, own Simulate-context call — same shape as WeaponMeleePD's orbit position/
    // MortarHazardPD's tracked bearing. ROTATION ONLY (2026-08-22) — position is set once at spawn and never
    // rewritten, since the caller's own near edge is stationary for this object's entire lifetime (e.g. a
    // King Orbiter held pleb during ShootFlight — EnterKnockbackWindup zeroes its orbit angular speed before
    // KnockbackWindup/ShootFlight even begin, so it doesn't move again until release). transform.rotation is
    // what the sibling PredictedTransform actually smooths (Quaternion.Slerp, see its own Interpolate()) —
    // this never touches State, since pose here is purely cosmetic and fully caller-derived (nothing to roll
    // back independently).
    public void SetAim(Vector3 aim)
    {
        transform.rotation = Quaternion.LookRotation(aim);
    }

    // Convenience wrapper so callers never need to know this is a hierarchy.Create()'d object under the
    // hood — same "caller just calls a method on the reference it already has" shape as every other
    // spawned-effect cleanup in this codebase.
    public void Despawn()
    {
        hierarchy.Delete(this);
    }
}
