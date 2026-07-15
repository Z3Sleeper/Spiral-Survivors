using PurrNet.Prediction;
using UnityEngine;

// Lets WeaponManagerPD (and anything else that needs to treat "a weapon" generically) operate on any
// WeaponBasePD<TState> subclass regardless of its own state type — same reason IEnemyPD exists for
// EnemyBasePD<TState>. Melee (WeaponMeleePD) and ranged (WeaponRangedPD) weapons each have their own
// simulated state shape, so neither can be the other's base class; this interface is what lets
// WeaponManagerPD manage both through one list instead of being hard-coded to one concrete weapon type.
public interface IWeaponPD
{
    int SlotIndex { get; set; }
    bool IsMelee { get; }
    Transform transform { get; }
    Transform hitboxTarget { get; }
    Transform rangeScaleTarget { get; }
    float RangeValue { get; set; }
    Vector3 RootScale { get; set; }
    void Init(CharacterStatsPD stats);
    void InitModelVisuals();
    void ScaleModel(float range);
}

public abstract class WeaponBasePD<TState> : PredictedIdentity<TState>, IWeaponPD
    where TState : struct, IPredictedData<TState>
{
    protected CharacterStatsPD _stats;
    public virtual bool IsMelee => true;

    public Transform   hitboxTarget;
    public Transform   rangeScaleTarget;
    public Transform[] rangeFollowers;

    // Explicit interface forwarding, not auto-properties — hitboxTarget/rangeScaleTarget above are plain
    // public fields wired up in the Inspector on existing weapon prefabs (e.g. KnightWeapon.prefab).
    // Converting them to auto-properties would satisfy IWeaponPD directly but silently drop those
    // Inspector-assigned references (auto-properties aren't serialized), breaking the prefab. Forwarding
    // keeps the fields serialized exactly as before while still satisfying the interface for
    // WeaponManagerPD's IWeaponPD-typed access.
    Transform IWeaponPD.hitboxTarget    => hitboxTarget;
    Transform IWeaponPD.rangeScaleTarget => rangeScaleTarget;
    [SerializeField] protected float     tipOffset        = 0f;
    [SerializeField] public    float     xRangeScaleRatio = 0f;
    [SerializeField] public    Transform[] yScaleFollowers;
    [SerializeField] public    Transform[] xzScaleFollowers;
    [SerializeField] public    Transform[] xScaleFollowers;

    private float     _hiltEdgeLocal;
    private float     _baseRangeScaleX;
    private float[]   _baseYScales;
    private Vector2[] _baseXZScales;
    private float[]   _baseXScales;

    [System.NonSerialized] public float designOrbit;
    public float   RangeValue { get; set; }
    public Vector3 RootScale  { get; set; } = Vector3.one;
    public int     SlotIndex  { get; set; }

    // Every weapon (melee or ranged) registers with its owner's WeaponManagerPD the same way — see
    // WeaponMeleePD.TryInit()/WeaponRangedPD's equivalent — and OrbitalPosition() below needs WeaponCount
    // from it for the multi-weapon angular-spacing term, so this lives here rather than being duplicated
    // per weapon type.
    [System.NonSerialized] public WeaponManagerPD _manager;

    public virtual void Init(CharacterStatsPD stats) => _stats = stats;

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
        float rootZ       = transform.localScale.z;
        float rangePart   = range / rootZ - designOrbit;
        float visualFront = rangePart - tipOffset;

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

    protected float RollDamage(float damage)
    {
        float max = damage * (1f + DamageVariance);
        float min = Mathf.Lerp(damage * (1f - DamageVariance), max, _stats.LuckChance);
        return GameRandom.instance.NextFloat(min, max);
    }

    // Shared orbital-position formula — both WeaponMeleePD (swept hitbox) and WeaponRangedPD (fires at
    // whatever it has line-of-sight to) orbit their owner identically, only what happens once they're
    // positioned differs. WeaponCount comes from _manager so multiple simultaneous weapons space evenly
    // around the circle regardless of which weapon type(s) they are.
    protected Vector3 OrbitalPosition(float baseAngle, Vector3 origin)
    {
        int   total     = _manager != null ? Mathf.Max(1, _manager.WeaponCount) : 1;
        float rad       = (baseAngle + 360f / total * SlotIndex) * Mathf.Deg2Rad;
        float halfRange = rangeScaleTarget != null
            ? designOrbit * RootScale.z
            : (IsMelee ? RangeValue / 2f : 0.25f);
        float radius = _stats.WeaponRadius + _stats.transform.lossyScale.x * 0.5f + halfRange;
        return origin + new Vector3(Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);
    }
}
