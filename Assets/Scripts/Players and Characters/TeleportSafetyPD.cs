using UnityEngine;

// Finds a safe (non-obstructed) landing position for a teleport-style repositioning — general-purpose, not
// tied to any one ability, so any caller (BWBlinkAbilityPD, dashes ending inside something, any future
// teleport) can reuse this directly.
//
// IMPORTANT caveat for any caller/layer mask (confirmed live 2026-09-19): every check here is
// Physics.CheckSphere, an OVERLAP query — Unity/PhysX cannot detect a non-convex MeshCollider with an
// overlap query under any circumstance, regardless of layer (sweep queries like SphereCast/Raycast, and real
// solid Rigidbody collision, don't have this restriction — only overlap queries do). Any layer this gets
// pointed at needs its colliders to be primitives, convex MeshColliders, or otherwise overlap-detectable —
// see ObstacleColliderPD's own doc comment for how obstacles solved this (extra convex, trigger-only
// MeshCollider siblings that exist purely so overlap queries can see them, alongside the real non-convex
// one used for actual physical collision).
//
// Full algorithm (redesigned 2026-09-19, see the earlier caller-position-based version's own history if this
// ever needs revisiting):
// 1. If the raw target position itself is already clear, use it verbatim — a teleport doesn't travel THROUGH
//    the space between start and end the way a dash's own physical movement does, so whatever's sitting on
//    that straight line is irrelevant on its own; only the final landing spot's own validity matters.
// 2. If the target is blocked, step outward from the target in small increments, in BOTH directions along
//    the given travel direction at once (backward — the side the caller came from — and forward, continuing
//    the same original direction) — checking backward before forward at each matching step distance, so an
//    equal-distance tie resolves toward the side the caller came from. The first clear step found, in either
//    direction, wins immediately.
//
// Takes a direction + a flat maxDistance rather than a caller position to derive both from, per explicit
// design correction: the original version capped the "backward" search at the caller's own position,
// reasoning that position was always guaranteed safe (true for BWBlinkAbilityPD, whose caller position is
// wherever the player stood BEFORE teleporting). That assumption breaks for a dash-ending-inside-something
// check, which only runs the instant right before the dash ends — by then "the caller's own current
// position" IS the dash's own (possibly-obstructed) end point, not a known-safe anchor. So neither direction
// gets a special "guaranteed to succeed eventually" treatment anymore — both search the exact same flat
// maxDistance (a placeholder default of 40 — realistically there's no hard maximum, this is just a sane
// search cap).
//
// Deliberately a stepped CheckSphere march, not a single SphereCast sweep — a single sweep only ever reports
// the FIRST obstruction encountered and has no way to see whether clear space exists just past it (confirmed
// via discussion/drawings: this breaks the moment there's more than one obstacle in a row, or an obstacle
// whose exact far edge doesn't line up with a single fixed probe distance). A march has one real trade-off in
// exchange for robustness: a genuinely valid but very narrow gap sitting BETWEEN two consecutive sample
// points could be missed. The failure mode only ever goes the safe direction though — it can wrongly report
// "no room here" when there technically is some, but every position it DOES accept is independently verified
// by its own CheckSphere, so it can never wrongly accept an actually-invalid spot.
//
// Returns null on total failure (nothing found within maxDistance in either direction) — deliberately NOT a
// baked-in fallback position, since what "couldn't find anywhere safe" should actually mean differs per
// caller (BWBlinkAbilityPD falls back to not moving at all, using its own known-safe pre-teleport position;
// a dash-ending-inside-something check may want something else entirely) — that decision belongs at the call
// site, not hidden inside this shared helper.
//
// isValidPosition (2026-09-20, optional): a candidate must ALSO satisfy this predicate, when given, on top of
// passing the CheckSphere test — added after a live bug where BWBlink could teleport a player past the
// playable area's own boundary wall entirely. The wall itself is only a thin panel hugging the actual
// walkable outline (PlayableAreaBoundsPD) — genuinely open, uncollided space starts again immediately past
// its outer face, same as it would past any other thin wall. CheckSphere alone can't tell "past the map" apart
// from "a legitimately clear spot": both read as equally clear. This only matters for a target that can land
// ARBITRARILY far away with no relation to solid geometry (BWBlink's blink target is the live position of its
// own orbiting blades, which has no wall-awareness of its own) — a dash can never trigger this since its own
// physical movement is already stopped by the real Bounds collider before ever reaching a point beyond it, so
// callers whose caller position and target are both already guaranteed on-map can safely omit this (pass null,
// the default) with no behavior change. A plain predicate (not a Bounds) so a caller can enforce the ACTUAL
// playable-area shape (PlayableAreaPD.instance.IsInsidePlayableArea — a real point-in-polygon test, correct
// for a concave outline too), not just its bounding box.
public static class TeleportSafetyPD
{
    public static Vector3? FindSafeTeleportPosition(Vector3 targetPosition, Vector2 direction, float stepSize, float radius, int layerMask, float maxDistance = 40f, System.Func<Vector3, bool> isValidPosition = null)
    {
        float checkRadius = Mathf.Max(0.01f, radius);

        bool IsClear(Vector3 p) =>
            !Physics.CheckSphere(p, checkRadius, layerMask) && (isValidPosition == null || isValidPosition(p));

        if (IsClear(targetPosition))
            return targetPosition;

        Vector3 forwardDir = new Vector3(direction.x, 0f, direction.y);
        float dirMagnitude = forwardDir.magnitude;
        if (dirMagnitude < 0.0001f) return null; // degenerate direction — nothing sensible to search along
        forwardDir /= dirMagnitude;
        Vector3 backwardDir = -forwardDir;

        int steps = Mathf.FloorToInt(maxDistance / stepSize);
        for (int i = 1; i <= steps; i++)
        {
            // Backward (the side the caller came from) checked first — tie-break preference.
            Vector3 backwardCandidate = targetPosition + backwardDir * (stepSize * i);
            if (IsClear(backwardCandidate))
                return backwardCandidate;

            Vector3 forwardCandidate = targetPosition + forwardDir * (stepSize * i);
            if (IsClear(forwardCandidate))
                return forwardCandidate;
        }

        return null; // total failure
    }

    // Same core CheckSphere march as FindSafeTeleportPosition above, but steps in ONLY the given direction —
    // added 2026-09-21 for a caller that needs to tell the two directions apart rather than taking whichever
    // FindSafeTeleportPosition's own "race, closer one wins" behavior happens to return. Concretely:
    // PlayerControllerPD.ResolveSafeTravelVelocity needs to distinguish "the near-side stop-short candidate"
    // from "the far-side clear-the-obstacle candidate," so it can explicitly prefer the far side when it's
    // within the speed-up grace cap and only fall back to the near side otherwise — using the bidirectional
    // method there was choosing the far side almost every time the obstacle sat well inside the dash's own
    // full range (the natural endpoint lands past the whole obstacle, so "keep going" only needs a tiny step
    // while "back up" has to cross the obstacle's entire width first — the far side reliably won that race),
    // which then got rejected by the speed cap far more often than intended, discarding a perfectly good
    // near-side stop that was never actually considered.
    //
    // Deliberately a SEPARATE small method rather than refactoring FindSafeTeleportPosition to share this loop
    // internally — that method is already proven-correct and load-bearing for several callers; duplicating
    // ~10 lines here is a safer trade than risking a subtle regression in it.
    public static Vector3? FindSafeTeleportPositionOneDirection(Vector3 targetPosition, Vector2 direction, float stepSize, float radius, int layerMask, float maxDistance, System.Func<Vector3, bool> isValidPosition = null)
    {
        float checkRadius = Mathf.Max(0.01f, radius);

        bool IsClear(Vector3 p) =>
            !Physics.CheckSphere(p, checkRadius, layerMask) && (isValidPosition == null || isValidPosition(p));

        if (IsClear(targetPosition))
            return targetPosition;

        Vector3 dir = new Vector3(direction.x, 0f, direction.y);
        float dirMagnitude = dir.magnitude;
        if (dirMagnitude < 0.0001f) return null;
        dir /= dirMagnitude;

        int steps = Mathf.FloorToInt(maxDistance / stepSize);
        for (int i = 1; i <= steps; i++)
        {
            Vector3 candidate = targetPosition + dir * (stepSize * i);
            if (IsClear(candidate))
                return candidate;
        }

        return null;
    }

    // Same core CheckSphere march as FindSafeTeleportPosition above, but searches EIGHT directions around a
    // full circle (the given direction, its exact opposite, and the 6 others at 45-degree increments) instead
    // of just those two — added 2026-09-21 specifically for BWBlinkAbilityPD's own teleport correction, NOT
    // used by any dash-style caller (a real dash's own physical motion only ever travels along its one
    // straight line, so ResolveSafeTravelVelocity/FindSafeTeleportPositionOneDirection's own one-axis
    // near/far distinction is already exactly right for that — a dash landing "off to the side" of its own
    // travel direction isn't a thing that can physically happen). Confirmed live: for an obstacle that's much
    // longer along the travel axis than it is wide (e.g. blinking into a tall, narrow rectangle well past its
    // own midpoint), the plain two-direction search could only ever exit back the way the player came or
    // continue all the way to the far end — both a much longer trip than simply stepping SIDEWAYS through the
    // nearest wall, which a teleport is free to do (it doesn't travel through the space between start and
    // target the way a dash does) but the old search had no way to even consider.
    //
    // Priority order at each step distance (2026-09-21, explicit design — "an extremely infinitely small
    // distance difference" in practice, so this only ever matters on an exact tie): the ORIGINAL forward
    // direction wins every tie, the exact opposite (backward) loses every tie, and the 6 remaining directions
    // are checked in a symmetric spiral outward from forward (45 degrees each side, then 90, then 135) — a
    // deliberately arbitrary but stable ordering for directions where the actual choice genuinely doesn't
    // matter.
    public static Vector3? FindSafeTeleportPositionMultiDirectional(Vector3 targetPosition, Vector2 direction, float stepSize, float radius, int layerMask, float maxDistance = 40f, System.Func<Vector3, bool> isValidPosition = null)
    {
        float checkRadius = Mathf.Max(0.01f, radius);

        bool IsClear(Vector3 p) =>
            !Physics.CheckSphere(p, checkRadius, layerMask) && (isValidPosition == null || isValidPosition(p));

        if (IsClear(targetPosition))
            return targetPosition;

        Vector3 forward = new Vector3(direction.x, 0f, direction.y);
        float dirMagnitude = forward.magnitude;
        if (dirMagnitude < 0.0001f) return null;
        forward /= dirMagnitude;

        Vector3[] searchDirs =
        {
            forward,                            // 0 degrees — wins ties
            Quaternion.Euler(0f, 45f, 0f) * forward,
            Quaternion.Euler(0f, -45f, 0f) * forward,
            Quaternion.Euler(0f, 90f, 0f) * forward,
            Quaternion.Euler(0f, -90f, 0f) * forward,
            Quaternion.Euler(0f, 135f, 0f) * forward,
            Quaternion.Euler(0f, -135f, 0f) * forward,
            -forward,                           // 180 degrees — loses ties
        };

        int steps = Mathf.FloorToInt(maxDistance / stepSize);
        for (int i = 1; i <= steps; i++)
        {
            for (int d = 0; d < searchDirs.Length; d++)
            {
                Vector3 candidate = targetPosition + searchDirs[d] * (stepSize * i);
                if (IsClear(candidate))
                    return candidate;
            }
        }

        return null;
    }

    // Finds the TRUE outward surface normal of whatever's blocking blockedPosition, by raycasting from a
    // KNOWN-CLEAR anchor position toward it (2026-09-19, added after live testing showed dashing into an
    // angled wall "spitting the player out" at a diagonal angle when FindSafeTeleportPosition's own direction
    // was just the raw travel direction — backing straight along the original travel vector doesn't
    // correspond to moving away from the wall's own actual surface once it's hit at an angle). The raycast
    // itself is safe/well-defined precisely because it starts from genuinely open space, not from inside
    // whatever's blocking blockedPosition — same "can't cast from inside a collider" reasoning
    // FindSafeTeleportPosition's own class doc comment already covers for CheckSphere, just solved here by
    // construction (a caller of THIS method is expected to already have a real known-clear anchor on hand,
    // e.g. PlayerControllerPD.EndDash passing state.dashStartPosition).
    //
    // Returns the result PRE-NEGATED, ready to pass directly as FindSafeTeleportPosition's own `direction`
    // parameter with no further sign-juggling needed at the call site: hit.normal itself already points away
    // from the wall, back toward the known-clear anchor — exactly that method's own "backward"
    // (tie-break-preferred) direction — so this negates it once here, up front, rather than leaving every
    // caller to rediscover and repeat that same negation. Returns null if the two positions coincide or the
    // raycast finds nothing (e.g. blockedPosition turned out not to actually be blocked after all) — the
    // caller should fall back to some other direction (e.g. the raw travel direction) in that case.
    public static Vector2? FindOutwardDirection(Vector3 knownClearPosition, Vector3 blockedPosition, int layerMask)
    {
        Vector3 toBlocked = blockedPosition - knownClearPosition;
        float distance = toBlocked.magnitude;
        if (distance < 0.0001f) return null;

        if (Physics.Raycast(knownClearPosition, toBlocked / distance, out RaycastHit hit, distance, layerMask, QueryTriggerInteraction.Collide))
            return new Vector2(-hit.normal.x, -hit.normal.z);

        return null;
    }
}
