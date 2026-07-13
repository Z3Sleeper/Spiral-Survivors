using PurrNet;
using PurrNet.Prediction;
using UnityEngine;

[RequireComponent(typeof(PredictedRigidbody))]
[RequireComponent(typeof(PredictedPhysicsCallbacks))]
public class XPOrbPD : PredictedIdentity<XPOrbPD.State>
{
    public struct State : IPredictedData<State>
    {
        public bool initialized;
        public float value;
        public bool settled;
        public bool collected;
        public float settleTimer;
        public PlayerID? attractingPlayer;
        public void Dispose() { }
    }

    [SerializeField] private PredictedRigidbody _rigidbody;
    [SerializeField] private float scatterForce = 3f;
    [SerializeField] private float attractSpeed = 20f;
    [SerializeField] private float collectRadius = 0.5f;
    [SerializeField] private AudioClip collectSound;

    private const float SettleDelay = 0.5f;

    private Collider[] _colliders;
    private Renderer[] _renderers;
    private bool _wasCollectedLastFrame;

    protected override State GetInitialState() => new() { settleTimer = SettleDelay };

    private void Awake()
    {
        _colliders = GetComponentsInChildren<Collider>();
        _renderers = GetComponentsInChildren<Renderer>();
    }

    public void SetValue(float value) => currentState.value = value;

    protected override void LateAwake()
    {
        if (TryGetComponent(out PredictedPhysicsCallbacks callbacks))
            callbacks.onTriggerStay += OnBoundsOrObstacleTrigger;
    }

    private void OnBoundsOrObstacleTrigger(GameObject other)
    {
        if (!currentState.settled || currentState.collected) return;

        float delta = predictionManager.tickDelta;

        if (other.layer == LayerMask.NameToLayer("Bounds"))
        {
            transform.position = Vector3.MoveTowards(
                transform.position,
                new Vector3(0f, transform.position.y, 0f),
                attractSpeed * delta);
        }
        else if (other.layer == LayerMask.NameToLayer("Obstacle"))
        {
            Vector3 obstacleCenter = other.TryGetComponent(out Collider col) ? col.bounds.center : other.transform.position;
            Vector3 dir = new Vector3(transform.position.x - obstacleCenter.x, 0f, transform.position.z - obstacleCenter.z);
            if (dir == Vector3.zero) dir = Vector3.right;
            transform.position += dir.normalized * attractSpeed * delta;
        }
    }

    // ── Simulation ─────────────────────────────────────────────────────────
    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        if (state.collected) return;

        // Pooled reuse never calls SimulationStart()/OnRemovedFromPool() (PredictedHierarchy's pool
        // path re-registers with reset:false), so first-tick setup has to be driven off State instead,
        // since GetInitialState() is the one thing reliably re-run on every reuse.
        if (!state.initialized)
        {
            state.initialized = true;
            _wasCollectedLastFrame = false;
            foreach (var col in _colliders) if (col != null) col.enabled = true;
            foreach (var rend in _renderers) if (rend != null) rend.enabled = true;

            _rigidbody.isKinematic = false;
            _rigidbody.velocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;

            Vector3 dir = new Vector3(
                GameRandom.instance.NextFloat(-1f, 1f),
                0f,
                GameRandom.instance.NextFloat(-1f, 1f)
            ).normalized;
            _rigidbody.AddForce(dir * scatterForce, ForceMode.Impulse);
        }

        if (!state.settled)
        {
            state.settleTimer -= delta;
            if (state.settleTimer <= 0f)
            {
                state.settled = true;
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.isKinematic = true;
            }
            return;
        }

        if (!state.attractingPlayer.HasValue) return;

        if (!CharacterStatsPD.All.TryGetValue(state.attractingPlayer.Value, out var stats) || stats.IsDead)
        {
            state.attractingPlayer = null;
            return;
        }

        Vector3 targetPos = stats.transform.position;
        transform.position = Vector3.MoveTowards(transform.position, targetPos, attractSpeed * delta);

        if (Vector3.Distance(transform.position, targetPos) < collectRadius)
            state.collected = true;
    }

    public void TryStartAttracting(PlayerID player)
    {
        if (currentState.collected || currentState.attractingPlayer.HasValue) return;
        currentState.attractingPlayer = player;
    }

    // ── View ───────────────────────────────────────────────────────────────
    protected override void UpdateView(State viewState, State? verified)
    {
        if (!verified.HasValue) return;
        var v = verified.Value;

        if (v.collected && !_wasCollectedLastFrame)
        {
            foreach (var col in _colliders) col.enabled = false;
            foreach (var rend in _renderers) rend.enabled = false;
            if (SFXManager.instance != null) SFXManager.instance.Play(collectSound, transform.position);
            if (GameManagerPD.instance != null) GameManagerPD.instance.AwardXP(v.value);
            hierarchy.Delete(this);
        }

        _wasCollectedLastFrame = v.collected;
    }
}
