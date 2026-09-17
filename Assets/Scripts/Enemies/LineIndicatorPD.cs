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

    // This life's own total span, captured once at Launch() — lets SetTraveled (2026-09-14) recompute the
    // remaining-length shrink without the caller needing to keep re-passing the original span every call.
    private float _span;

    private void Awake()
    {
        Fill = GetComponentInChildren<WindupFillPD>(true);
    }

    protected override State GetInitialState() => default;

    // Called once immediately after hierarchy.Create() returns — same "mutate right after Create()" pattern
    // as every other spawned effect in this codebase (RemoteSpikeBurstPD.Launch(), MortarHazardPD.Launch()).
    // Root position/initial rotation already came from hierarchy.Create()'s own spawn params — this only
    // sizes the shape, which SetAim never touches again.
    //
    // Fill.ShowRectangle, not a manually-poked pivot child (2026-09-14 fix, replaces a stale pre-refactor
    // direct pivot.localPosition/localScale write) — WindupFillPD's post-refactor architecture (see its own
    // class doc comment) now owns its fillMesh/outline shape EXCLUSIVELY via _length/_width/_centerForwardOffset,
    // only ever set through ShowCircle/ShowRectangle, and actively recomputes fillMesh's transform from those
    // every single call — including every one of the plain SetProgress() calls KingOrbiterPD's own
    // UpdateShootIndicatorFills/UpdatePersistentIndicatorFills make every render frame. Since ShowRectangle was
    // never called anywhere in this class, those internal fields sat permanently at their zero defaults, and
    // every one of those per-frame SetProgress calls silently collapsed the mesh AND the outline (Linear mode
    // recomputes both fully every call, no caching) back down to a zero-size quad at local origin — confirmed
    // live as "the indicator line never shows up at all, every outline position reads 0,0,0". centerForwardOffset:
    // span * 0.5f is the exact "WindupIndicator sits at the NEAR EDGE, reaching length forward" case — see
    // ShowRectangle's own doc comment — matching this class's own "root stays at the fixed near edge, only the
    // rendered shape reaches forward from it" design (previously done by hand via the now-removed pivot field).
    public void Launch(float span)
    {
        _span = span;
        if (Fill == null) return;
        Fill.ShowRectangle(span, centerForwardOffset: span * 0.5f);
    }

    // Shrinks the shown shape to only the REMAINING (untraveled) portion of this life's own total span,
    // "collapsing in behind" whatever's flying along it (2026-09-14) — same visual concept as BlitzGloomPD/
    // KingSpikePD's own dash/charge indicators, just achieved differently here: those unparent and physically
    // reposition the WHOLE indicator object forward, since their indicator is a passively-Visuals-inherited
    // child that otherwise has no independent position of its own; THIS object's root is already independent
    // (spawned via hierarchy.Create(), its own PredictedTransform, never Visuals-parented at all — see class
    // doc comment) and deliberately never repositioned after spawn, so shrinking is done purely by shifting
    // WHERE within the fixed root's own local frame the shown rectangle starts: centerForwardOffset moves the
    // shape's center forward by `traveled` (hiding the traveled portion behind the root, which nothing ever
    // renders past anyway) while its length shrinks to match, so the far edge (this life's own fixed maximum
    // reach) never moves. traveled is caller-derived — KingOrbiterPD's own ShootFlight/Persistent Volley
    // callers pass a live Vector3.Distance to the flying pleb's own transform.position (2026-09-14, was
    // IEnemyPD.LaunchRemainingDistance — a raw, non-interpolated predicted-state read that desynced visibly
    // from the pleb's own separately-smoothed Visuals position; see UpdateShootIndicatorFills's own doc
    // comment for the full story). That's safe here specifically because it's a DIFFERENT object's own live
    // position being read (this indicator is never parented under the pleb), same category as
    // UpdatePullLineIndicators' own plebPos read — not the self-referential live-transform-of-a-still-parented-
    // object pattern that's unsafe elsewhere in this codebase.
    public void SetTraveled(float traveled)
    {
        if (Fill == null) return;
        float remaining = Mathf.Max(0f, _span - traveled);
        Fill.ShowRectangle(remaining, centerForwardOffset: traveled + remaining * 0.5f);
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
