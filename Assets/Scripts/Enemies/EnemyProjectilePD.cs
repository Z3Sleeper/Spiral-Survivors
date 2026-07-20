using PurrNet.Prediction;
using UnityEngine;

// A short-lived, self-contained flying projectile fired BY an enemy AT players — the enemy-owned mirror of
// WeaponRangedProjectilePD (player-owned, damages enemies), but much simpler: no ricochet, no per-hit
// damage/onHitPower dropoff, no ally detection — matches old ShooterGloomProjectile's own simplicity.
// Deliberately not named after any specific enemy (e.g. not "ShooterGloomProjectilePD") so any future enemy
// that fires a straight-line shot can reuse it. Expires on its first hit (doesn't pierce), matching old.
public class EnemyProjectilePD : PredictedIdentity<EnemyProjectilePD.State>
{
    public struct State : IPredictedData<State>
    {
        public Vector3 direction;
        public float   distanceTravelled;
        // Deferred hierarchy.Delete() to UpdateView() off verified state, same pattern as
        // WeaponRangedProjectilePD/EnemyBasePD's own death handling.
        public bool    expired;
        public void Dispose() { }
    }

    [SerializeField] private Vector3 halfExtents = new(0.1f, 0.1f, 0.25f);
    [SerializeField] private Renderer[] _renderers;
    // Scales the cosmetic Visuals child only — deliberately NOT the root transform, which
    // PredictedTransform actively drives for position/rotation. Set once in Launch() (a plain float, same
    // on every machine since it's passed in from the firer's own deterministic config), then baked into
    // the box-cast half-extents below instead of leaning on transform.lossyScale.
    [SerializeField] private Transform _visuals;

    private float _damage;
    private float _speed;
    private float _maxRange;
    private float _scale = 1f;
    // Plain view-layer bookkeeping, not predicted state — same instant-predicted-hide pattern as
    // WeaponRangedProjectilePD/PhantomBladePD.
    private bool _hiddenOnExpire;

    private readonly RaycastHit[] _sweepResults = new RaycastHit[8];

    // Called immediately after hierarchy.Create() returns — same "mutate currentState right after Create()"
    // pattern as WeaponRangedProjectilePD.Launch().
    public void Launch(float damage, Vector3 direction, float speed, float maxRange, float scale = 1f, Material material = null)
    {
        _damage   = damage;
        _speed    = speed;
        _maxRange = maxRange;
        _scale    = scale;

        currentState.direction = direction;
        currentState.distanceTravelled = 0f;
        currentState.expired = false;

        transform.rotation = Quaternion.LookRotation(direction);
        if (_visuals != null) _visuals.localScale = Vector3.one * scale;
        if (material != null) SetRenderersMaterial(material);

        // A pooled instance's previous life ends hidden (see UpdateView()'s predicted-hide below) — a
        // fresh launch needs to be visible again.
        _hiddenOnExpire = false;
        SetRenderersEnabled(true);
    }

    protected override State GetInitialState() => default;

    // Intentionally empty — movement/hit detection need the post-physics-pass position of anything they
    // interact with, so everything happens in LateSimulate(), same reasoning as WeaponRangedProjectilePD.
    protected override void Simulate(ref State state, float delta) { }

    protected override void LateSimulate(ref State state, float delta)
    {
        if (state.expired) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        Vector3 prevPos = transform.position;
        Vector3 step    = state.direction * (_speed * delta);
        transform.position = prevPos + step;
        state.distanceTravelled += step.magnitude;

        float dist = step.magnitude;
        if (dist > 0.0001f)
        {
            Vector3 half = halfExtents * _scale;
            int n = Physics.BoxCastNonAlloc(prevPos, half, step / dist, _sweepResults, transform.rotation, dist, CombatLayerMasks.PlayerHurtboxMask);
            for (int i = 0; i < n; i++)
            {
                var target = _sweepResults[i].collider.GetComponent<CharacterStatsPD>();
                if (target == null || target.IsDead || target.IsInvincible) continue;
                target.TakeDamage(_damage);
                state.expired = true;
                break;
            }
        }

        if (!state.expired && state.distanceTravelled >= _maxRange)
            state.expired = true;
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
        // Instant, predicted hide — same reasoning as WeaponRangedProjectilePD's own UpdateView.
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
