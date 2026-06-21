using UnityEngine;

public class WindupFill : MonoBehaviour
{
    public enum FillType { Linear, Radial }
    public enum Axis { X, Y, Z }
    public enum FillMode { Center, EdgeStart, EdgeEnd }

    [SerializeField] private FillType fillType = FillType.Linear;
    [SerializeField] private Axis fillAxis = Axis.Z;
    [SerializeField] private FillMode fillMode = FillMode.EdgeStart;

    private Vector3 _baseLocalScale;
    private float _baseScale;
    private float _basePos;
    private float _halfExtent;
    private float _duration;
    private float _elapsed;
    private bool _filling;
    private bool _awoken;

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

    private void Update()
    {
        if (!_filling) return;
        _elapsed += Time.deltaTime;
        Apply(Mathf.Clamp01(_elapsed / _duration));
        if (_elapsed >= _duration) _filling = false;
    }

    private void Apply(float t)
    {
        if (!_awoken) return;
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
