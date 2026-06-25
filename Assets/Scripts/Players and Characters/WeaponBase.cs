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
    private float _hiltEdgeLocal;
    [System.NonSerialized] public float designOrbit;

    public void InitModelVisuals()
    {
        if (rangeScaleTarget == null) return;
        float basePos   = rangeScaleTarget.localPosition.z;
        float baseScale = rangeScaleTarget.localScale.z;
        _hiltEdgeLocal = basePos - baseScale * 0.5f;
        designOrbit    = basePos + baseScale * 0.5f; // blade front at design range (≈0.5 for unit weapons)
    }

    public void ScaleModel(float range)
    {
        if (rangeScaleTarget == null && rangeFollowers == null) return;
        float rootZ     = transform.localScale.z;
        float rangePart = range / rootZ - designOrbit; // blade front local at current range

        float visualFront = rangePart - bladeTipOffset;

        if (rangeScaleTarget != null)
        {
            var s = rangeScaleTarget.localScale;
            rangeScaleTarget.localScale    = new Vector3(s.x, s.y, visualFront - _hiltEdgeLocal);
            var p = rangeScaleTarget.localPosition;
            rangeScaleTarget.localPosition = new Vector3(p.x, p.y, (_hiltEdgeLocal + visualFront) / 2f);
        }

        if (rangeFollowers != null)
            foreach (var f in rangeFollowers)
                if (f != null) { var p = f.localPosition; f.localPosition = new Vector3(p.x, p.y, visualFront); }
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

    public virtual void Init(CharacterStats stats)
    {
        _stats = stats;
        _controller = stats.GetComponent<PlayerController>();
    }
}
