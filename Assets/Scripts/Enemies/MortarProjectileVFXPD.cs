using PurrNet.Prediction;
using UnityEngine;

// Purely cosmetic — no collision, no damage, no gameplay effect whatsoever. The real hit is MortarHazardPD,
// spawned separately by MortarGloomPD and resolved on its own independent timer; this only exists so every
// client sees the same lobbed shell arc land at the same time. Still driven through predicted
// Simulate/LateSimulate (not raw Update()) so its position stays in lockstep across machines, same as every
// other spawned VFX in this codebase (see EnemyProjectilePD's own reasoning for writing position directly in
// LateSimulate() rather than interpolating in UpdateView()).
public class MortarProjectileVFXPD : PredictedIdentity<MortarProjectileVFXPD.State>
{
    public struct State : IPredictedData<State>
    {
        public Vector3 start;
        public Vector3 end;
        public float   elapsed;
        // Deferred hierarchy.Delete() to UpdateView() off verified state, same pattern as
        // EnemyProjectilePD/RemoteSpikeBurstPD's own one-shot deletion.
        public bool    expired;
        public void Dispose() { }
    }

    [SerializeField] private Renderer[] _renderers;
    // Scales the cosmetic visuals child only, same reasoning as EnemyProjectilePD._visuals — set once in
    // Launch() (a plain float, identical on every machine since it's passed in by the firer's own
    // deterministic config).
    [SerializeField] private Transform _visuals;

    private float _duration;
    private float _arcHeight;
    // Plain view-layer bookkeeping, not predicted state — same instant-predicted-hide pattern as
    // EnemyProjectilePD/WeaponRangedProjectilePD.
    private bool  _hiddenOnExpire;

    // Called immediately after hierarchy.Create() returns — same "mutate currentState right after Create()"
    // pattern as EnemyProjectilePD.Launch().
    public void Launch(Vector3 start, Vector3 end, float duration, float arcHeight, float scale = 1f, Material material = null)
    {
        _duration  = Mathf.Max(0.01f, duration);
        _arcHeight = arcHeight;

        currentState.start   = start;
        currentState.end     = end;
        currentState.elapsed = 0f;
        currentState.expired = false;

        transform.position = start;
        if (_visuals != null) _visuals.localScale = Vector3.one * scale;
        if (material != null) SetRenderersMaterial(material);

        // A pooled instance's previous life ends hidden (see UpdateView()'s predicted-hide below) — a fresh
        // launch needs to be visible again.
        _hiddenOnExpire = false;
        SetRenderersEnabled(true);
    }

    protected override State GetInitialState() => default;

    // Intentionally empty — same reasoning as EnemyProjectilePD/RemoteSpikeBurstPD: this is purely a
    // post-physics-pass position update with no interaction to resolve, so everything happens in
    // LateSimulate().
    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        state.elapsed += delta;
        float t = Mathf.Clamp01(state.elapsed / _duration);
        Vector3 pos = Vector3.Lerp(state.start, state.end, t);
        pos.y += _arcHeight * 4f * t * (1f - t); // parabola, peak at t=0.5
        transform.position = pos;

        if (t >= 1f) state.expired = true;
    }

    private void SetRenderersEnabled(bool value)
    {
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].enabled = value;
    }

    // sharedMaterial, not .material — this is a pooled object launched repeatedly, and .material would
    // silently instantiate a new material copy on every single Launch() call.
    private void SetRenderersMaterial(Material material)
    {
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].sharedMaterial = material;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Instant, predicted hide — same reasoning as EnemyProjectilePD's own UpdateView.
        if (viewState.expired && !_hiddenOnExpire)
        {
            _hiddenOnExpire = true;
            SetRenderersEnabled(false);
        }

        // State-critical — only act on server-confirmed expiry, same reasoning as every other spawned
        // one-shot effect's deletion.
        if (verified.HasValue && verified.Value.expired)
            hierarchy.Delete(this);
    }
}
