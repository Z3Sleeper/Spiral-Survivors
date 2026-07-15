using UnityEngine;

// Lightweight replacement for PredictedTransform on objects whose position is already fully
// deterministic and derived from other already-synced state (e.g. a WeaponBasePD<TState> subclass's
// orbit, computed from the player's position + the weapon's own angle state) — there's nothing independently
// mispredicted here to roll back, so the full PredictedIdentity/network-sync machinery is pure
// redundant traffic. Unity's own Update/LateUpdate runs after PurrDiction finishes all tick/replay
// processing for the frame, so by the time this runs, transform.position is already the final,
// fully-reconciled value — every machine computes it identically without needing it transmitted.
public class LocalGraphicsSmoother : MonoBehaviour
{
    [SerializeField] private Transform _graphics;
    [SerializeField] private float _positionSmoothTime = 0.05f;
    [SerializeField] private float _rotationSmoothSpeed = 20f;
    // Mirrors BurstMovementInterpolation's snap threshold — a sharp jump (e.g. the owning player
    // dashing) should snap instantly rather than visibly lag while smoothing catches up.
    [SerializeField] private float _snapDistance = 1.5f;

    private Vector3 _positionVelocity;

    private void OnEnable()
    {
        _positionVelocity = Vector3.zero;
        if (_graphics != null)
            _graphics.SetPositionAndRotation(transform.position, transform.rotation);
    }

    private void LateUpdate()
    {
        if (_graphics == null) return;

        // Unscaled on purpose. A weapon's own LateSimulate() (WeaponMeleePD, WeaponRangedPD) stops
        // updating transform.position on its own once PausePD's predicted IsPaused flips true — that's
        // the real freeze point. This smoother
        // just needs to keep making real progress toward wherever the root currently is regardless of
        // Time.timeScale, so it fully converges within a few frames right as pause visually kicks in,
        // instead of either racing to finish (snap-on-pause) or getting stuck mid-lag if gated on pause
        // (stuck-then-snap-on-unpause) — both tried before this.
        float dt = Time.unscaledDeltaTime;

        if (Vector3.Distance(_graphics.position, transform.position) > _snapDistance)
        {
            _graphics.SetPositionAndRotation(transform.position, transform.rotation);
            _positionVelocity = Vector3.zero;
            return;
        }

        _graphics.position = Vector3.SmoothDamp(_graphics.position, transform.position, ref _positionVelocity, _positionSmoothTime, Mathf.Infinity, dt);
        _graphics.rotation = Quaternion.Slerp(_graphics.rotation, transform.rotation, _rotationSmoothSpeed * dt);
    }
}
