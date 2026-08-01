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
        // Was missing the same pause gate Simulate() has — this callback fires off Unity's own trigger
        // detection, entirely independent of Simulate()'s early-return, so an orb sitting in a Bounds/
        // Obstacle trigger kept getting nudged every tick regardless of pause. That's the "vibrates like
        // crazy during pause and never stops" symptom: as long as the orb stayed overlapped, this fired
        // forever with nothing ever gating it off.
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        // Deliberately NOT gated on settled — an orb spawned/scattered out of bounds needs to be pushed back
        // in regardless of whether it's already gone kinematic (MovePosition works fine either way), or it
        // just settles in place still outside the map and sits there forever. Still skipped once attracting
        // (moving toward a player under its own steam) or collected.
        if (currentState.collected || currentState.attractingPlayer.HasValue) return;
        // Held off until the scatter's own settleTimer window has elapsed — purely cosmetic (letting the
        // little scatter-impulse tumble play out before the pull-back starts, instead of it instantly
        // snapping/fighting the scatter from frame one), NOT a correctness gate: the callback still fires
        // and is simply ignored below, so a still-out-of-bounds orb gets corrected the instant the timer
        // expires, same tick it otherwise would have gone kinematic.
        if (currentState.settleTimer > 0f) return;

        float delta = predictionManager.tickDelta;

        // MovePosition, not a raw transform.position write — every other moving predicted object in this
        // project (dashes, knockback, kill-launch) goes through the Rigidbody so PredictedTransform's own
        // interpolation/tracking picks it up correctly. A bare transform.position assignment bypasses that
        // pipeline entirely, which is very likely why this specifically jittered on non-host clients (their
        // smoothed/interpolated view fighting a position change PredictedTransform never knew happened)
        // while host, rendering its own live transform directly, looked fine.
        if (other.layer == LayerMask.NameToLayer("Bounds"))
        {
            Vector3 target = Vector3.MoveTowards(
                transform.position,
                new Vector3(0f, transform.position.y, 0f),
                attractSpeed * delta);
            _rigidbody.MovePosition(target);
        }
        else if (other.layer == LayerMask.NameToLayer("Obstacle"))
        {
            Vector3 obstacleCenter = other.TryGetComponent(out Collider col) ? col.bounds.center : other.transform.position;
            Vector3 dir = new Vector3(transform.position.x - obstacleCenter.x, 0f, transform.position.z - obstacleCenter.z);
            if (dir == Vector3.zero) dir = Vector3.right;
            _rigidbody.MovePosition(transform.position + dir.normalized * attractSpeed * delta);
        }
    }

    // ── Simulation ─────────────────────────────────────────────────────────
    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused)
        {
            // Same reasoning as EnemyBasePD's own pause-handling: the physics engine keeps integrating
            // whatever velocity a non-kinematic Rigidbody already has every tick regardless of this early
            // return — an orb still mid-scatter (not yet settled, so not yet kinematic) when pause hits
            // would otherwise keep drifting on residual velocity for the whole pause, which is exactly the
            // milder "wiggle" left over after the trigger-callback jitter was fixed. Guarded by !isKinematic
            // since a settled orb's Rigidbody is already kinematic and PredictedRigidbody's velocity setter
            // throws if attempted on one.
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.velocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            return;
        }
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
