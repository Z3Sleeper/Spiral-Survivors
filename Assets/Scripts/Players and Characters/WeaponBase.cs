using PurrNet;
using UnityEngine;

public class WeaponBase : NetworkIdentity
{
    protected CharacterStats _stats;
    protected PlayerController _controller;
    public virtual bool IsMelee => true;

    public Transform hitboxTarget;

    // Visual model scaling
    public Transform   rangeScaleTarget;
    public Transform[] rangeFollowers;
    [SerializeField] private float bladeTipOffset = 0f;
    [SerializeField] public  float xRangeScaleRatio = 0f;
    [SerializeField] public  Transform[] yScaleFollowers;
    [SerializeField] public  Transform[] xzScaleFollowers;
    [SerializeField] public  Transform[] xScaleFollowers;
    private float _hiltEdgeLocal;
    private float _baseRangeScaleX;
    private float[] _baseYScales;
    private Vector2[] _baseXZScales;
    private float[] _baseXScales;
    [System.NonSerialized] public float designOrbit;

    public void InitModelVisuals()
    {
        if (rangeScaleTarget != null)
        {
            float basePos    = rangeScaleTarget.localPosition.z;
            float baseScale  = rangeScaleTarget.localScale.z;
            _hiltEdgeLocal   = basePos - baseScale * 0.5f;
            designOrbit      = basePos + baseScale * 0.5f;
            _baseRangeScaleX = rangeScaleTarget.localScale.x;
        }

        if (yScaleFollowers != null)
        {
            _baseYScales = new float[yScaleFollowers.Length];
            for (int i = 0; i < yScaleFollowers.Length; i++)
                if (yScaleFollowers[i] != null)
                    _baseYScales[i] = yScaleFollowers[i].localScale.y;
        }

        if (xzScaleFollowers != null)
        {
            _baseXZScales = new Vector2[xzScaleFollowers.Length];
            for (int i = 0; i < xzScaleFollowers.Length; i++)
                if (xzScaleFollowers[i] != null)
                    _baseXZScales[i] = new Vector2(xzScaleFollowers[i].localScale.x, xzScaleFollowers[i].localScale.z);
        }

        if (xScaleFollowers != null)
        {
            _baseXScales = new float[xScaleFollowers.Length];
            for (int i = 0; i < xScaleFollowers.Length; i++)
                if (xScaleFollowers[i] != null)
                    _baseXScales[i] = xScaleFollowers[i].localScale.x;
        }
    }

    public void ScaleModel(float range)
    {
        if (rangeScaleTarget == null && rangeFollowers == null && (yScaleFollowers == null || yScaleFollowers.Length == 0)) return;
        float rootZ     = transform.localScale.z;
        float rangePart = range / rootZ - designOrbit;
        float visualFront = rangePart - bladeTipOffset;

        float xRatio = xRangeScaleRatio > 0f
            ? Mathf.Lerp(1f, range / Mathf.Max(0.001f, designOrbit * rootZ), xRangeScaleRatio)
            : 1f;

        if (rangeScaleTarget != null)
        {
            float newX = xRangeScaleRatio > 0f ? _baseRangeScaleX * xRatio : rangeScaleTarget.localScale.x;
            rangeScaleTarget.localScale    = new Vector3(newX, rangeScaleTarget.localScale.y, visualFront - _hiltEdgeLocal);
            var p = rangeScaleTarget.localPosition;
            rangeScaleTarget.localPosition = new Vector3(p.x, p.y, (_hiltEdgeLocal + visualFront) / 2f);
        }

        if (rangeFollowers != null)
            foreach (var f in rangeFollowers)
                if (f != null) { var p = f.localPosition; f.localPosition = new Vector3(p.x, p.y, visualFront); }

        if (_baseYScales != null && xRangeScaleRatio > 0f)
            for (int i = 0; i < yScaleFollowers.Length; i++)
            {
                if (yScaleFollowers[i] == null) continue;
                var s = yScaleFollowers[i].localScale;
                yScaleFollowers[i].localScale = new Vector3(s.x, _baseYScales[i] * xRatio, s.z);
            }

        if (_baseXZScales != null && xRangeScaleRatio > 0f)
            for (int i = 0; i < xzScaleFollowers.Length; i++)
            {
                if (xzScaleFollowers[i] == null) continue;
                var s = xzScaleFollowers[i].localScale;
                xzScaleFollowers[i].localScale = new Vector3(_baseXZScales[i].x * xRatio, s.y, _baseXZScales[i].y * xRatio);
            }

        if (_baseXScales != null && xRangeScaleRatio > 0f)
            for (int i = 0; i < xScaleFollowers.Length; i++)
            {
                if (xScaleFollowers[i] == null) continue;
                var s = xScaleFollowers[i].localScale;
                xScaleFollowers[i].localScale = new Vector3(_baseXScales[i] * xRatio, s.y, s.z);
            }
    }

    public static event System.Action<CharacterStats, EnemyBase, float> OnStrike;
    protected void InvokeOnStrike(EnemyBase enemy)
    {
        OnStrike?.Invoke(_stats, enemy, 1f);
        RaiseOnHit(_stats, enemy);
    }
    public static void RaiseOnStrike(CharacterStats stats, EnemyBase enemy, float effectiveness = 1f)
    {
        OnStrike?.Invoke(stats, enemy, effectiveness);
        RaiseOnHit(stats, enemy, effectiveness);
    }

    public static event System.Action<CharacterStats, EnemyBase, float> OnHit;
    public static void RaiseOnHit(CharacterStats stats, EnemyBase enemy, float effectiveness = 1f) => OnHit?.Invoke(stats, enemy, effectiveness);

    public static event System.Action<CharacterStats, EnemyBase, float> OnCritStrike;
    public static void RaiseOnCritStrike(CharacterStats stats, EnemyBase enemy, float effectiveness = 1f) => OnCritStrike?.Invoke(stats, enemy, effectiveness);

    public static event System.Action<CharacterStats, CharacterStats, float> OnAllyHit;
    public static void RaiseOnAllyHit(CharacterStats stats, CharacterStats ally, float effectiveness = 1f) => OnAllyHit?.Invoke(stats, ally, effectiveness);

    public virtual void Init(CharacterStats stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerController>();
    }
}
