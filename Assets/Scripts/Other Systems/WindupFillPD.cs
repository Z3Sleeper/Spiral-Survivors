using UnityEngine;

public class WindupFillPD : MonoBehaviour
{
    public enum FillType { Linear, Radial }
    public enum Axis { X, Y, Z }
    public enum FillMode { Center, EdgeStart, EdgeEnd }

    [SerializeField] private FillType fillType = FillType.Linear;
    [SerializeField] private Axis fillAxis = Axis.Z;
    [SerializeField] private FillMode fillMode = FillMode.EdgeStart;

    // 0-255 byte scale (matches Unity's own color-picker display), not 0-1 — divided by 255 when applied to
    // Color.a. "Opaque" here means basically-opaque alpha-wise (255/255), NOT a material render-mode switch —
    // this only ever lerps the alpha channel of the material's own authored color, never touches render
    // queue/mode/anything else, so the material stays exactly as authored (Transparent) throughout.
    [Header("Alpha (0-255)")]
    [SerializeField] private float transparentAlpha = 50f;
    [SerializeField] private float opaqueAlpha       = 255f;

    private Vector3 _baseLocalScale;
    private float _baseScale;
    private float _basePos;
    private float _halfExtent;
    private float _duration;
    private float _elapsed;
    private bool _filling;
    private bool _awoken;

    private Renderer _renderer;
    // The material's own authored RGB, captured once — only .a ever gets overridden per-frame below, so
    // whatever color this fill was authored as (red/yellow/whatever) is always preserved.
    private Color _baseColor;

    private void Awake()
    {
        _awoken = true;
        _baseLocalScale = transform.localScale;

        switch (fillAxis)
        {
            case Axis.X: _baseScale = transform.localScale.x; _basePos = transform.localPosition.x; break;
            case Axis.Y: _baseScale = transform.localScale.y; _basePos = transform.localPosition.y; break;
            case Axis.Z: _baseScale = transform.localScale.z; _basePos = transform.localPosition.z; break;
        }

        var mf = GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            Vector3 ext = mf.sharedMesh.bounds.extents;
            float meshK = fillAxis == Axis.X ? ext.x : fillAxis == Axis.Y ? ext.y : ext.z;
            _halfExtent = meshK * _baseScale;
        }
        else
        {
            _halfExtent = _baseScale * 0.5f;
        }

        // .material (not sharedMaterial) auto-instances a per-renderer copy the FIRST time it's touched —
        // same established convention as EnemyBasePD's own hit-flash color system (_renderers[i].material.
        // color = ...) — so animating alpha here never mutates the shared material asset every other
        // WindupFillPD instance also uses, without needing a MaterialPropertyBlock.
        _renderer = GetComponent<Renderer>();
        if (_renderer != null && _renderer.material != null) _baseColor = _renderer.material.color;

        Apply(0f);
    }

    public void StartFill(float duration)
    {
        _duration = duration;
        _elapsed = 0f;
        _filling = true;
    }

    public void ResetFill()
    {
        _filling = false;
        Apply(0f);
    }

    // Drives the fill directly off a caller-computed 0..1 progress value instead of this component's own
    // self-timed StartFill/Update loop above — for callers whose underlying duration isn't fixed (e.g. an
    // enemy's attack-speed-scaled windup, where a slow landing/expiring mid-windup changes how fast the
    // real remaining time drains). Bypasses _filling entirely so the two driving modes never fight over the
    // same frame — a caller should pick one mode per fill and stick to it.
    public void SetProgress(float t01) => Apply(Mathf.Clamp01(t01));

    private void Update()
    {
        if (!_filling) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;
        _elapsed += Time.deltaTime;
        Apply(Mathf.Clamp01(_elapsed / _duration));
        if (_elapsed >= _duration) _filling = false;
    }

    private void Apply(float t)
    {
        if (!_awoken) return;

        // Alpha lerp applies regardless of fillType (Linear or Radial) — same progress value driving the
        // scale/position fill below also drives how transparent-to-basically-opaque this looks.
        if (_renderer != null && _renderer.material != null)
        {
            Color c = _baseColor;
            c.a = Mathf.Lerp(transparentAlpha, opaqueAlpha, t) / 255f;
            _renderer.material.color = c;
        }

        if (fillType == FillType.Radial)
        {
            transform.localScale = _baseLocalScale * t;
            return;
        }

        Vector3 scale = transform.localScale;
        Vector3 pos = transform.localPosition;

        float posOffset;
        if (fillMode == FillMode.EdgeStart)
            posOffset = -_halfExtent * (1f - t);
        else if (fillMode == FillMode.EdgeEnd)
            posOffset = _halfExtent * (1f - t);
        else
            posOffset = 0f;

        switch (fillAxis)
        {
            case Axis.X: scale.x = _baseScale * t; pos.x = _basePos + posOffset; break;
            case Axis.Y: scale.y = _baseScale * t; pos.y = _basePos + posOffset; break;
            case Axis.Z: scale.z = _baseScale * t; pos.z = _basePos + posOffset; break;
        }
        transform.localScale = scale;
        transform.localPosition = pos;
    }
}
