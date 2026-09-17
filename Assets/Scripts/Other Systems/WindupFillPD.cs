using UnityEngine;

// Lives on WindupIndicator itself (2026-09-14 refactor). WindupIndicator's own transform is NEVER written to
// by this script, or by any caller, anymore — its position/rotation come purely from whatever it's parented
// under (typically the enemy), inherited automatically, untouched. Every caller instead hands this script raw
// shape data (a world-space center, plus a radius or length/width) via ShowCircle/ShowRectangle, called every
// frame the indicator is up, and this script alone is responsible for turning that into both visual children's
// actual transforms:
// - "Line Renderer" (outlineRenderer) — carries its own fixed local rotation (90 on X) so LineRenderer's
//   Transform-Z alignment lies the ribbon flat on the ground instead of standing it up vertically. Always
//   draws in outlineRenderer's own LOCAL space (useWorldSpace = false, set BY SCRIPT in Awake(), not left to
//   the Inspector) — see RecomputeOutline's own doc comment for how both shapes' points get converted into
//   that tilted local frame, so everything composes with WindupIndicator's CURRENT transform by Unity itself
//   at render time, immune to PredictedTransform-smoother read-timing jitter.
// - "Windup Fill" (fillMesh) — the actual filling plane/cylinder. Positioned/scaled directly by this script
//   every call too (see ApplyFill), growing from empty to the full shape as progress advances.
//
// Replaces the old scheme where callers directly mutated WindupIndicator via
// IndicatorMath.ApplyRadialTransform/ApplyLineTransform and this script tried to infer everything back out by
// reading transforms after the fact (fighting the fill mesh's own animation, alignment-fix rotations polluting
// facing reads, etc.) — this version has exactly one source of truth (whatever the caller passed this frame),
// no inference needed anywhere.
//
// Neither shape ever takes a world-space position (2026-09-14, circle's own center parameter removed to match
// — see ShowCircle/ShowRectangle's own doc comments for the full reasoning) — every caller is expected to
// already have WindupIndicator itself sitting at (or facing toward, for the offset case) the geometrically
// correct spot, either passively (inheriting the enemy's own facing) or explicitly (SetPositionAndRotation).
// Any caller that tracks an independent target is expected to also visually FACE that tracking direction on
// WindupIndicator itself (explicit design) — a caller that computes a tracked bearing/distance but never
// applies it to any transform's rotation has a bug to fix on its own end, not a case this script accommodates.
//
// World-units convention: _length/_width/_radius are always true world-space units. Converting those into an
// actual localScale for fillMesh needs a *0.1f factor for length/width (NOT radius) — see ApplyFill's own
// comment for why (mirrors the old IndicatorMath.ApplyLineTransform's identical distance*0.1f convention, and
// Spear's own hand-rolled stabWidth*0.1f precedent for width specifically).
public class WindupFillPD : MonoBehaviour
{
    public enum FillType { Linear, Radial }
    // GrowOutward (2026-09-14) — Linear only, same as Center (no position shift, always centered) except
    // width ALSO grows with progress instead of being fully present from t=0 — see ApplyFill's own comment.
    public enum FillMode { Center, EdgeStart, EdgeEnd, GrowOutward }

    [SerializeField] private FillType fillType = FillType.Linear;
    [SerializeField] private FillMode fillMode = FillMode.EdgeStart;

    [Header("Visuals")]
    [SerializeField] private Transform fillMesh;
    [SerializeField] private LineRenderer outlineRenderer;

    // Fixed, not per-instance-tunable — 48 is plenty smooth for any circle this project draws, and a
    // rectangle is definitionally always exactly 4 corners.
    private const int CircleOutlineSegments = 48;
    private const int RectangleOutlineCorners = 4;

    // fillMesh's own authored local X scale, converted to true world units ONCE at Awake (before ApplyFill
    // ever overwrites fillMesh's own scale) — the DEFAULT rectangle width whenever a caller doesn't pass one
    // explicitly (ShowRectangle's width <= 0, the common case). Mirrors the old
    // IndicatorMath.ApplyLineTransform convention exactly: X left untouched, whatever's authored.
    private float _authoredWidthWorld;

    private bool _awoken;
    private float _duration;
    private float _elapsed;
    private bool _filling;

    // Current shape, set by ShowCircle/ShowRectangle — ApplyFill/the outline both read these every call to
    // know the FULL target size to interpolate/draw from, since nothing is inherited from a parent transform
    // anymore; this script is the sole source of the real dimensions, refreshed every frame it's called.
    private float _radius;
    private float _length;
    private float _width;
    // How far forward (WindupIndicator's own local Z) the shape's center sits from WindupIndicator's own
    // current origin — shared by both ShowCircle and ShowRectangle (see either's own doc comment for the
    // full reasoning); safe to share since a given instance only ever runs one branch (fillType is fixed).
    // 0 default is always a safe no-op (self-positioned at local origin), never a "snap to world origin" risk
    // the way an unset Vector3 world center used to be.
    private float _centerForwardOffset;

    private readonly Vector3[] _circleOffsets = new Vector3[CircleOutlineSegments];
    private readonly Vector3[] _outlinePositions = new Vector3[CircleOutlineSegments];
    private bool  _circleOffsetsValid;
    // The _radius the cache above was actually built for (2026-09-14 fix) — every ORIGINAL Radial caller in
    // this codebase calls Hide() between uses (a state transition hides the old show before the next one
    // starts), which invalidates the cache anyway, so a live radius CHANGE mid-show never used to happen.
    // PlayerRangeIndicatorPD broke that assumption: it's a continuously-shown ring (never Hide()'d while the
    // owning player is alive) whose radius genuinely changes over its whole life (weapon range augments,
    // Size stat changes) — without this, the cached circle would silently freeze at whatever radius the
    // FIRST-ever ShowCircle/RecomputeOutline call happened to see, and every later radius change would keep
    // recomputing the FILL mesh correctly (never cached) while the OUTLINE stayed stuck forever, a real,
    // confirmed desync between the two. RecomputeOutline now also invalidates the cache whenever _radius no
    // longer matches what it was built for, not just on Hide().
    private float _circleOffsetsRadius;

    // Rectangle outline jitter fix (2026-09-14) — outlineRenderer's own LOCAL rotation (the fixed 90-on-X
    // Transform-Z-alignment tilt, authored once on the prefab, NEVER touched at runtime) converts a point
    // expressed in WindupIndicator's plain local frame (right/up/forward = local X/Y/Z, exactly what
    // ApplyFill's own fillMesh math already uses) into outlineRenderer's own tilted local frame. Cached once
    // here specifically because it's safe to — unlike WindupIndicator's own WORLD rotation (which a
    // PredictedTransform smoother can still be mid-interpolating when this script reads it, the actual root
    // cause of the fill-mesh jitter this same pass already fixed), this LOCAL offset is a static, author-time
    // constant with nothing to smooth or go stale. See RecomputeOutline's own doc comment for how this gets
    // used — rectangles only; a circle's own points never depended on rotation at all.
    private Quaternion _outlineInverseLocalRotation = Quaternion.identity;

    private void Awake()
    {
        _awoken = true;

        if (fillMesh != null)
            _authoredWidthWorld = fillMesh.localScale.x * 10f;

        if (outlineRenderer != null)
        {
            _outlineInverseLocalRotation = Quaternion.Inverse(outlineRenderer.transform.localRotation);
            outlineRenderer.useWorldSpace = false;
        }

        ApplyFill(0f);
    }

    // Sets/updates a circular AoE's shape. Safe to call every frame with a live-tracked target — cheap, no
    // allocation, and the outline math below already assumes a fresh call each frame for exactly that reason
    // (see RecomputeOutline's own doc comment on why only the circle's RELATIVE offsets get cached, never
    // centerForwardOffset itself).
    //
    // No world-space center parameter (2026-09-14, removed — matches ShowRectangle's own identical change,
    // see its doc comment for the full jitter reasoning this avoids). centerForwardOffset (0 default) is a
    // plain scalar distance forward, in WindupIndicator's own local space, same convention as
    // ShowRectangle's own parameter of the same name — 0 means "WindupIndicator's own current position
    // already IS the center" (Dagger/KnockbackDummyTarget/King Spike's spike burst/RemoteSpikeBurstPD, all
    // stationary AoEs with no independent target). A caller tracking an independent target (Leaper) computes
    // its own tracked distance and passes that instead — see LeaperGloomPD's own doc comment for why this is
    // only valid once that caller ALSO applies its own tracked bearing to WindupIndicator's actual rotation
    // (explicit design: every tracking caller is expected to visually face its own tracking direction).
    public void ShowCircle(float radius, float centerForwardOffset = 0f)
    {
        _radius = radius;
        _centerForwardOffset = centerForwardOffset;
    }

    // Sets/updates a rectangular AoE's shape. width <= 0 (the default) means "use whatever's authored on
    // fillMesh's own prefab" (_authoredWidthWorld) — pass a real value (e.g. Spear's stabWidth * size) to
    // override it per-call instead, same opt-in convention the old hand-rolled per-enemy width writes used.
    //
    // No world-space center parameter (2026-09-14, removed — was the "off-centered" fix's own doc comment,
    // now superseded) — every caller already positions WindupIndicator itself at the geometrically-correct
    // spot before calling this (either passively, inheriting the enemy's own smoothed facing, or explicitly
    // via SetPositionAndRotation), so a SEPARATE world center was both redundant AND actively harmful:
    // computing it required reading transform.forward at script-execution time and baking that into an
    // absolute world position, which — for any indicator passively inheriting a PredictedTransform-smoothed
    // rotation (an enemy's own Visuals hierarchy) rather than being explicitly, synchronously positioned by
    // this same script — could read a hair before or after the smoother's own per-frame update, producing a
    // small but very visible jitter once multiplied by a large lever arm (half the reach distance). The
    // enemy's own body mesh never showed this because ordinary Unity parent-child inheritance always composes
    // fresh at RENDER time, immune to any such read-timing mismatch; a manually-computed world write does not
    // get that same guarantee.
    //
    // centerForwardOffset (0 default) instead: a plain SCALAR distance forward, in WindupIndicator's own
    // local space, from WindupIndicator's own current position to where the rectangle's true center sits.
    // 0 (the default) means "WindupIndicator's own current position already IS the center" — Mortar Hazard
    // and Orbiter/King Orbiter's pull-line indicators both explicitly self-position every frame to exactly
    // that point already. Pass length*0.5f instead for the more common "WindupIndicator sits at the NEAR
    // EDGE, reaching length forward" case (Blitz/Spear/SpikeShield/King Spike's charge windup) — ApplyFill
    // applies this PURELY via fillMesh.localPosition (see its own doc comment), never needing to read
    // transform.forward at all, which is what actually fixes the jitter (a plain scalar has no rotation
    // dependency to go stale). The outline (RecomputeOutline) still needs a genuine world-space point — no way
    // around that given LineRenderer's own useWorldSpace requirement — so it isn't fully immune the same way;
    // flagged as a separate, likely much smaller concern worth checking independently.
    public void ShowRectangle(float length, float width = -1f, float centerForwardOffset = 0f)
    {
        _length = length;
        _width = width > 0f ? width : _authoredWidthWorld;
        _centerForwardOffset = centerForwardOffset;
    }

    public void StartFill(float duration)
    {
        _duration = duration;
        _elapsed = 0f;
        _filling = true;
    }

    public void Hide()
    {
        _filling = false;
        ApplyFill(0f);
        if (outlineRenderer != null)
        {
            _circleOffsetsValid = false;
            outlineRenderer.positionCount = 0;
        }
    }

    // Drives the fill directly off a caller-computed 0..1 progress value instead of this component's own
    // self-timed StartFill/Update loop below — for callers whose underlying duration isn't fixed (e.g. an
    // enemy's attack-speed-scaled windup, where a slow landing/expiring mid-windup changes how fast the real
    // remaining time drains). Bypasses _filling entirely so the two driving modes never fight over the same
    // frame — a caller should pick one mode per fill and stick to it. This is the MAJORITY path for real
    // enemy windups today.
    public void SetProgress(float t01)
    {
        ApplyFill(Mathf.Clamp01(t01));
        RecomputeOutline();
    }

    private void Update()
    {
        if (!_filling) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        _elapsed += Time.deltaTime;
        ApplyFill(Mathf.Clamp01(_elapsed / _duration));
        RecomputeOutline();
        if (_elapsed >= _duration) _filling = false;
    }

    // Positions/scales fillMesh directly — nothing here is inherited from WindupIndicator's own transform
    // (which is never touched), so every number is computed fresh from _centerForwardOffset/_radius/_length/
    // _width each call. The *0.1f on length/width (NOT radius) mirrors the old IndicatorMath.ApplyLineTransform's
    // own "Z scale is 10% of distance" convention (the shared plane mesh every line indicator in this codebase
    // uses is authored at 10 world units per 1 scale unit) — Spear's own old hand-rolled
    // `scale.x = stabWidth * 0.1f * size` confirms this applies to width too, not just length.
    private void ApplyFill(float t)
    {
        if (!_awoken) return;

        // No per-frame alpha lerp anymore (2026-09-14, removed) — was fading in from a more-transparent to a
        // more-opaque alpha as progress advanced, needed back when a windup fill could still visually overlay
        // a separate, differently-transparent indicator underneath it. Now that's no longer the case, so the
        // material's own authored alpha (whatever's set on fillMesh's material in the Inspector) is simply
        // left untouched for this fill's whole life.
        if (fillMesh == null) return;

        if (fillType == FillType.Radial)
        {
            // Pure LOCAL write, same jitter-fix reasoning as the Linear branch below (and ShowCircle's own
            // doc comment) — no transform.forward/.position read involved at all.
            float diameter = _radius * 2f * t;
            fillMesh.localPosition = new Vector3(0f, fillMesh.localPosition.y, _centerForwardOffset);
            Vector3 scale = fillMesh.localScale;
            scale.x = diameter;
            scale.z = diameter;
            fillMesh.localScale = scale;
            return;
        }

        float halfFull = _length * 0.5f;
        float posOffset;
        if (fillMode == FillMode.EdgeStart)
            posOffset = -halfFull * (1f - t);
        else if (fillMode == FillMode.EdgeEnd)
            posOffset = halfFull * (1f - t);
        else
            posOffset = 0f; // Center and GrowOutward both stay centered — see their own doc comments.

        // Pure LOCAL write (2026-09-14 jitter fix) — NOT transform.forward * offset written as a world
        // position. fillMesh is a direct child of WindupIndicator (WindupIndicator's own scale is always 1,
        // so local and world distances match 1:1 here), so a local Z write gets composed with
        // WindupIndicator's own CURRENT transform by Unity itself at render time — the same mechanism that
        // already keeps the enemy's own body mesh smooth — rather than baking a specific transform.forward
        // READ (taken at this script's own execution moment, which can land a hair before or after a
        // PredictedTransform smoother's own per-frame update) into a fixed world number. See
        // ShowRectangle/_centerForwardOffset's own doc comment for the full reasoning.
        fillMesh.localPosition = new Vector3(0f, fillMesh.localPosition.y, _centerForwardOffset + posOffset);

        // GrowOutward scales width by t too (full width -> nothing until progress starts) — every other
        // mode (Center included) keeps width fully present from t=0, only length ever grows/shifts.
        Vector3 scaleL = fillMesh.localScale;
        scaleL.x = fillMode == FillMode.GrowOutward ? _width * t * 0.1f : _width * 0.1f;
        scaleL.z = _length * t * 0.1f;
        fillMesh.localScale = scaleL;
    }

    // Radial -> circle, Linear -> rectangle, mirroring IndicatorMath.ApplyRadialTransform/ApplyLineTransform's
    // own two shapes respectively. Both computed here in WindupIndicator's own plain local frame (right/up/
    // forward = local X/Y/Z, exactly matching ApplyFill's own fillMesh convention), then rotated by
    // _outlineInverseLocalRotation into outlineRenderer's own tilted local frame (see that field's own doc
    // comment for why that conversion is safe — a fixed author-time constant, nothing live to go stale) —
    // no world-space read anywhere in this method, immune to the same PredictedTransform-smoother
    // read-timing jitter fillMesh's own position was fixed for.
    //
    // Circle: relative offsets from center are rotation-invariant, so the Cos/Sin trig for all
    // CircleOutlineSegments points is cached in _circleOffsets and only actually recomputed when it's stale —
    // guarded by _circleOffsetsValid (invalidated in Hide()) OR _radius no longer matching _circleOffsetsRadius
    // (invalidated by a live radius change — see that field's own doc comment for why this second condition
    // is needed, not just the first). Every call still translates those (possibly just-recomputed, possibly
    // still-cached) offsets by the (possibly live) _centerForwardOffset before the single per-call rotation
    // into outlineRenderer's own frame — that part was never cacheable, it's genuinely live every call.
    //
    // Rectangle: recomputed FULLY every single call instead, deliberately NOT using a cache — a rectangle's 4
    // corners are NOT rotation-invariant the way a circle's are, so caching anything beyond the fixed
    // conversion quaternion itself risks going stale the instant _centerForwardOffset or the shape changes.
    // Cheap enough to just redo from scratch every call regardless — 4 points, no trig at all, just two
    // scalar multiplies and four additions.
    private void RecomputeOutline()
    {
        if (outlineRenderer == null) return;

        if (fillType == FillType.Radial)
        {
            if (!_circleOffsetsValid || _circleOffsetsRadius != _radius)
            {
                for (int i = 0; i < CircleOutlineSegments; i++)
                {
                    float angle = i / (float)CircleOutlineSegments * Mathf.PI * 2f;
                    _circleOffsets[i] = new Vector3(Mathf.Cos(angle) * _radius, 0f, Mathf.Sin(angle) * _radius);
                }
                outlineRenderer.positionCount = CircleOutlineSegments;
                _circleOffsetsValid = true;
                _circleOffsetsRadius = _radius;
            }

            Vector3 center = new Vector3(0f, 0f, _centerForwardOffset);
            for (int i = 0; i < CircleOutlineSegments; i++)
                _outlinePositions[i] = _outlineInverseLocalRotation * (center + _circleOffsets[i]);
            outlineRenderer.SetPositions(_outlinePositions);
        }
        else
        {
            // Pure LOCAL write (2026-09-14 jitter fix, same reasoning as ApplyFill's own fillMesh.localPosition
            // switch) — computed here in WindupIndicator's own plain local frame (right/forward = local X/Z,
            // exactly matching ApplyFill's own convention), then rotated ONCE by _outlineInverseLocalRotation
            // into outlineRenderer's own tilted local frame (see that field's own doc comment for why that
            // conversion is safe — it's a fixed author-time constant, not a live read of anything a smoother
            // could still be interpolating). useWorldSpace is set false for Linear in Awake() specifically so
            // these local points render correctly.
            float halfWidth  = _width * 0.5f;
            float halfLength = _length * 0.5f;
            Vector3 c       = new Vector3(0f, 0f, _centerForwardOffset);
            Vector3 right   = new Vector3(halfWidth, 0f, 0f);
            Vector3 forward = new Vector3(0f, 0f, halfLength);

            outlineRenderer.positionCount = RectangleOutlineCorners;
            outlineRenderer.SetPosition(0, _outlineInverseLocalRotation * (c - right - forward));
            outlineRenderer.SetPosition(1, _outlineInverseLocalRotation * (c + right - forward));
            outlineRenderer.SetPosition(2, _outlineInverseLocalRotation * (c + right + forward));
            outlineRenderer.SetPosition(3, _outlineInverseLocalRotation * (c - right + forward));
        }
    }
}
