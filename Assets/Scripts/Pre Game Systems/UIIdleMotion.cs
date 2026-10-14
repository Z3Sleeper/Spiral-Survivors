using UnityEngine;

// Generic, reusable ambient idle motion for lobby menu polish (2026-10-12) — gentle sine-wave drift/rotation/
// scale breathing on whatever it's attached to, purely cosmetic, no gameplay coupling. Drop onto individual
// menu elements (panels, icons, decorative art) that should feel subtly alive instead of perfectly static;
// each amplitude defaults to 0 (no motion on that axis) so a fresh instance is a safe no-op until tuned.
public class UIIdleMotion : MonoBehaviour
{
    // All *Speed fields below are radians/second fed straight into Mathf.Sin(t * speed), not Hz/"cycles per
    // second" — a speed of 1 does NOT mean one full oscillation per second, it means one full oscillation
    // every (2*PI / speed) seconds (~6.28s at speed 1). Doubling a speed value halves that axis's period,
    // but the absolute number itself isn't directly "cycles per second" — tune by feel/trial, not by
    // computing an intended period from the raw value.
    [Header("Position (local units — UI space, so these are pixels)")]
    [SerializeField] private Vector2 positionAmplitude = Vector2.zero;
    [SerializeField] private float positionSpeedX = 0.5f;
    [SerializeField] private float positionSpeedY = 0.4f;

    [Header("Rotation (degrees, Z axis)")]
    [SerializeField] private float rotationAmplitudeDegrees = 0f;
    [SerializeField] private float rotationSpeed = 0.3f;

    [Header("Scale (fraction of base scale, e.g. 0.05 = breathes +/-5%)")]
    [SerializeField] private float scaleAmplitude = 0f;
    [SerializeField] private float scaleSpeed = 0.4f;

    // Randomized per-instance so multiple elements using this same script don't all drift in perfect
    // lockstep — without this, several of these on screen at once would read as one single mechanical pulse
    // rather than organic, independent motion. Seeded once at Awake(), stays consistent for this instance's
    // whole lifetime.
    private float _phaseOffset;

    private RectTransform _rectTransform;
    private Vector2 _basePosition;
    private Quaternion _baseRotation;
    private Vector3 _baseScale;

    private void Awake()
    {
        _rectTransform = GetComponent<RectTransform>();
        _basePosition = _rectTransform != null ? _rectTransform.anchoredPosition : (Vector2)transform.localPosition;
        _baseRotation = transform.localRotation;
        _baseScale = transform.localScale;
        _phaseOffset = Random.Range(0f, Mathf.PI * 2f);
    }

    private void Update()
    {
        // Unscaled — this is pure menu polish, should keep animating even through a Time.timeScale == 0
        // moment, same convention LoadingScreen's own dot/hint cycling and MusicPlayer's FadeOut already use.
        float t = Time.unscaledTime + _phaseOffset;

        if (positionAmplitude != Vector2.zero)
        {
            // Y offset carries its own +1.3 phase offset (independent of positionSpeedX/Y now being directly
            // tunable) so the path still traces a loose organic drift rather than a perfectly straight
            // diagonal line even if X/Y speeds happen to end up equal.
            Vector2 offset = new Vector2(
                Mathf.Sin(t * positionSpeedX) * positionAmplitude.x,
                Mathf.Sin(t * positionSpeedY + 1.3f) * positionAmplitude.y);
            if (_rectTransform != null) _rectTransform.anchoredPosition = _basePosition + offset;
            else transform.localPosition = (Vector3)_basePosition + (Vector3)offset;
        }

        if (rotationAmplitudeDegrees != 0f)
            transform.localRotation = _baseRotation * Quaternion.Euler(0f, 0f, Mathf.Sin(t * rotationSpeed) * rotationAmplitudeDegrees);

        if (scaleAmplitude != 0f)
            transform.localScale = _baseScale * (1f + Mathf.Sin(t * scaleSpeed) * scaleAmplitude);
    }
}
