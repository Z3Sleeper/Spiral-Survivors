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

    [System.NonSerialized] public SyncVar<float>   _rangeSync     = new(0f, ownerAuth: true);
    [System.NonSerialized] public SyncVar<Vector3> _rootScaleSync = new(Vector3.one, ownerAuth: true);

    private CharacterStats _colorStats;

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

    private const float DamageVariance = 0.20f;
    public static float RollDamage(float damage, float luckChance)
    {
        float max = damage * (1f + DamageVariance);
        float min = Mathf.Lerp(damage * (1f - DamageVariance), max, luckChance);
        return Random.Range(min, max);
    }
    protected float RollDamage(float damage) => RollDamage(damage, _stats.LuckChance);

    public static event System.Action<CharacterStats, EnemyBase, float, bool> OnStrike;
    protected void InvokeOnStrike(EnemyBase enemy, bool isCrit = false)
    {
        OnStrike?.Invoke(_stats, enemy, 1f, isCrit);
        RaiseOnHit(_stats, enemy, 1f, isCrit);
        Vector3 dir = enemy.transform.position - _stats.transform.position;
        dir.y = 0f;
        if (dir != Vector3.zero)
            enemy.ApplyKnockback(dir.normalized, _stats.Knockback);
    }
    public static void RaiseOnStrike(CharacterStats stats, EnemyBase enemy, float effectiveness = 1f, bool isCrit = false)
    {
        OnStrike?.Invoke(stats, enemy, effectiveness, isCrit);
        RaiseOnHit(stats, enemy, effectiveness, isCrit);
        Vector3 dir = enemy.transform.position - stats.transform.position;
        dir.y = 0f;
        if (dir != Vector3.zero)
            enemy.ApplyKnockback(dir.normalized, stats.Knockback);
    }

    public static event System.Action<CharacterStats, EnemyBase, float, bool> OnHit;
    public static void RaiseOnHit(CharacterStats stats, EnemyBase enemy, float effectiveness = 1f, bool isCrit = false)
    {
        OnHit?.Invoke(stats, enemy, effectiveness, isCrit);
    }

    public static event System.Action<CharacterStats, EnemyBase, float> OnCritStrike;
    public static void RaiseOnCritStrike(CharacterStats stats, EnemyBase enemy, float effectiveness = 1f) => OnCritStrike?.Invoke(stats, enemy, effectiveness);

    public static event System.Action<CharacterStats, CharacterStats, float> OnAllyStrike;
    public static void RaiseOnAllyStrike(CharacterStats stats, CharacterStats ally, float effectiveness = 1f) => OnAllyStrike?.Invoke(stats, ally, effectiveness);

    protected override void OnSpawned()
    {
        base.OnSpawned();
        if (_stats != null) return; // Already initialized by WeaponManager on the owner
        if (!owner.HasValue) return;
        if (!CharacterStats.All.TryGetValue(owner.Value, out var stats))
        {
            Debug.LogWarning($"[WeaponBase.OnSpawned] No CharacterStats found for owner {owner.Value} — scale/color won't apply on this client");
            return;
        }

        _colorStats = stats;
        ApplyColor(stats._playerColorSync.value);
        stats._playerColorSync.onChanged += ApplyColor;

        if (IsMelee)
        {
            InitModelVisuals();
            _rangeSync.onChanged     += _ => ApplyScaleVisual(_rootScaleSync.value, _rangeSync.value);
            _rootScaleSync.onChanged += _ => ApplyScaleVisual(_rootScaleSync.value, _rangeSync.value);
            if (_rangeSync.value > 0f)
                ApplyScaleVisual(_rootScaleSync.value, _rangeSync.value);
        }
    }

    private void ApplyScaleVisual(Vector3 rootScale, float range)
    {
        if (range <= 0f) return;
        transform.localScale = rootScale;
        ScaleModel(range);
    }

    private void ApplyColor(Color c)
    {
        foreach (var a in GetComponentsInChildren<PlayerColorApplicator>(true))
            a.Apply(c);
    }

    public virtual void Init(CharacterStats stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerController>();
        _colorStats = stats;
        ApplyColor(stats._playerColorSync.value);
        stats._playerColorSync.onChanged += ApplyColor;
    }

    protected override void OnDespawned(bool asServer)
    {
        if (_colorStats != null) _colorStats._playerColorSync.onChanged -= ApplyColor;
        base.OnDespawned(asServer);
    }
}
