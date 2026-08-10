using PurrNet.Prediction;
using UnityEngine;

// A standalone ground hazard spawned by MortarGloomPD at Windup entry — not IEnemyPD, not a weapon, same
// category as RemoteSpikeBurstPD ("deliberately not X"). Lives independently of the Mortar once spawned: it
// keeps ticking and will still detonate even if the Mortar that spawned it has since died, EXCEPT while still
// mid-Windup, where MortarGloomPD.OnInterrupted() can actively Cancel() it (see that class's own doc comment
// for why cancellation is phase-gated rather than blanket-never).
public class MortarHazardPD : PredictedIdentity<MortarHazardPD.State>
{
    public struct State : IPredictedData<State>
    {
        public float chargeTimer;
        public bool  expired;
        // Set only via Cancel() (MortarGloomPD.OnInterrupted, Windup-phase only) — distinguishes "shut down
        // early, no damage" from a normal chargeTimer-reaches-0 expiry, both of which route through the same
        // `expired` flag for UpdateView()'s existing delete-on-expiry path.
        public bool  cancelled;
        public void Dispose() { }
    }

    [SerializeField] private WindupFillPD _windupFill;

    private static readonly Collider[] _hitResults = new Collider[8]; // hard cap of 4 players, generous margin

    private float _halfWidth;
    private float _halfDepth;
    private float _damage;
    private bool  _visualStarted;

    // Called immediately after hierarchy.Create() returns — same "mutate currentState right after Create()"
    // pattern as RemoteSpikeBurstPD.Launch(). Takes full width/depth (not half-extents) — friendlier to tune
    // from the Inspector-facing caller; halved here into the half-extents Physics.OverlapBoxNonAlloc actually
    // wants.
    public void Launch(float chargeTime, float width, float depth, float damage)
    {
        _halfWidth = width * 0.5f;
        _halfDepth = depth * 0.5f;
        _damage    = damage;
        currentState.chargeTimer = chargeTime;

        // Pooled-reuse safety — same reasoning as RemoteSpikeBurstPD.Launch()'s own _visualStarted reset and
        // explicit ResetFill() call.
        _visualStarted = false;
        if (_windupFill != null) _windupFill.ResetFill();

        // Scales this hazard's own root — DangerIndicator and WindupFill both live as children underneath it
        // and inherit the non-uniform width/depth for free via ordinary transform-hierarchy propagation, same
        // convention RemoteSpikeBurstPD.UpdateView uses (IndicatorMath.ApplyRadialTransform(transform, ...)),
        // just non-uniform (width != depth) instead of a single radius. No direct DangerIndicator reference
        // needed — matches every other windup-hazard prefab in this codebase, which only ever slots in
        // WindupFill (and occasionally a WindupIndicator wrapper), never DangerIndicator itself.
        // *0.1f — same compensation IndicatorMath.ApplyLineTransform's own "Z scale is 10% of distance" uses:
        // Unity's built-in Plane primitive is 10x10 world units at scale 1 (unlike a Quad, which is 1x1), so
        // a DangerIndicator/WindupFill authored as a Plane needs this ratio to make localScale units actually
        // correspond to world units.
        var rootScale = transform.localScale;
        transform.localScale = new Vector3(width * 0.1f, rootScale.y, depth * 0.1f);
    }

    protected override State GetInitialState() => default;

    protected override void Simulate(ref State state, float delta) { }

    // Called from MortarGloomPD.OnInterrupted() (Windup-phase only) — cross-identity predicted-state
    // mutation from within the caller's own Simulate() context, same established-safe pattern
    // SetMovementDisabled/Stun/Slow/Interrupt already use elsewhere in this codebase.
    public void Cancel() => currentState.cancelled = true;

    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (state.cancelled) { state.expired = true; return; } // no damage, no timer countdown — just expire
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        state.chargeTimer -= delta;
        if (state.chargeTimer > 0f) return;

        Vector3 half = new Vector3(_halfWidth, 2f, _halfDepth); // tall enough to catch any player hurtbox
        int n = Physics.OverlapBoxNonAlloc(transform.position, half, _hitResults, transform.rotation, CombatLayerMasks.PlayerHurtboxMask);
        for (int i = 0; i < n; i++)
        {
            var stats = _hitResults[i].GetComponent<CharacterStatsPD>();
            if (stats != null && !stats.IsDead && !stats.IsInvincible) stats.TakeDamage(_damage);
        }
        state.expired = true;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Same verified-snapshot reasoning as RemoteSpikeBurstPD.UpdateView — avoids starting the fill off a
        // blended/pooled-reuse chargeTimer value.
        if (!_visualStarted && _windupFill != null && verified.HasValue && verified.Value.chargeTimer > 0f)
        {
            _windupFill.StartFill(verified.Value.chargeTimer);
            _visualStarted = true;
        }

        // State-critical — only act on server-confirmed expiry, same reasoning as every other spawned
        // one-shot effect's deletion. Covers both a real timeout and an early Cancel() the same way.
        if (verified.HasValue && verified.Value.expired)
            hierarchy.Delete(this);
    }
}
