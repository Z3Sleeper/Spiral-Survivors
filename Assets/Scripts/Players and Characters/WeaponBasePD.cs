using System.Collections.Generic;
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
    // Lets WeaponManagerPD subscribe generically to any weapon's crit event, regardless of concrete type,
    // so it can relay every registered weapon's crits up to the owning player's own CharacterStatsPD.
    void AddCritStrikeListener(System.Action<PredictedObjectID> listener);
    void RemoveCritStrikeListener(System.Action<PredictedObjectID> listener);
}

// Shared damage-roll formula, non-generic so it's usable by anything that needs it without inheriting
// from WeaponBasePD<TState> — e.g. WeaponRangedProjectilePD, which is deliberately NOT a weapon (no
// orbit, no range-scaling visuals), matching the old system's WeaponBase.RollDamage static helper.
public static class WeaponDamageMath
{
    // Rolls against CharacterStatsPD.LuckFloor/LuckCeiling/Highroll instead of hardcoded constants — any
    // future system that wants a "luck-influenced random roll" should roll against these same stats rather
    // than adding its own variance constant, so a single augment moving the floor, ceiling, or highroll
    // threshold affects all of them at once. luckFloor is already clamped to >= 0 at the source (LuckFloor
    // property), so this never needs to guard against a negative result itself.
    //
    // highroll: landing in the top (1 - highroll) fraction of this roll's own [min, max] window snaps the
    // result up to max and flags it — a true max roll is otherwise a measure-zero event off continuous
    // random sampling, practically unreachable, which is the whole reason this exists.
    public static float RollDamage(float damage, float luckChance, float luckFloor, float luckCeiling, float highroll, out bool isHighroll)
    {
        float max = damage * luckCeiling;
        float rawMin = damage * luckFloor;

        // Threshold is fixed against the pre-luck [rawMin, max] window, not the luck-raised one below —
        // luckChance only raises the roll's actual floor toward this already-placed line, it doesn't move
        // the line itself. That's what makes Luck and Highroll interact: as luckChance climbs, min closes
        // the gap on threshold, so the odds of clearing it rise too, and once luckChance >= highroll, min
        // has caught up to (or passed) threshold and every roll highrolls.
        float highrollThreshold = Mathf.Lerp(rawMin, max, highroll);

        float min = Mathf.Lerp(rawMin, max, luckChance);
        float roll = GameRandom.instance.NextFloat(min, max);

        isHighroll = roll >= highrollThreshold;
        return isHighroll ? max : roll;
    }
}

// LayerMask.GetMask() does a string lookup every call — cheap once, wasteful when called per-instance
// (WeaponMeleePD/WeaponRangedPD registration) or worse, per-shot (WeaponRangedProjectilePD.Launch()). The
// layer→index mapping is fixed at editor time and never changes at runtime, so a static field computed
// once (C#'s guaranteed single-init-on-first-access for static fields) covers every caller for the whole
// process lifetime.
public static class CombatLayerMasks
{
    public static readonly int EnemyHurtboxMask = LayerMask.GetMask("Enemy");
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

    // isHighroll out so callers (which already have the attacker/target in scope for TakeDamage/etc.) can
    // invoke OnHighroll themselves — keeps the event-raising at the actual hit call site, same separation
    // the old system's RollDamage/RaiseOnCritStrike had.
    protected float RollDamage(float damage, out bool isHighroll) =>
        WeaponDamageMath.RollDamage(damage, _stats.LuckChance, _stats.LuckFloor, _stats.LuckCeiling, _stats.Highroll, out isHighroll);

    // PredictedEvent, not a plain C# event — per PurrNet's docs (client-side-prediction/multi-identity-
    // interactions), this is the framework's own mechanism for rollback-safe one-shot notifications: it
    // only actually invokes subscribers once, off a verified/non-replaying pass, so a client-side
    // misprediction that the server later disagrees with never fires it. A plain event fired directly from
    // Simulate-context code (what this used to be) would fire again on every resimulation replay.
    // protected, not private — subclasses (WeaponMeleePD.Hit(), WeaponAxePD.Hit()) invoke it directly at
    // their own hit call sites. External subscribers use the Add/RemoveHighrollListener wrappers below,
    // matching the private-field + wrapper-method convention CharacterStatsPD._onHeal and
    // PlayerControllerPD._onDashStarted already established.
    protected PredictedEvent<float> _onHighroll;

    public void AddHighrollListener(System.Action<float> listener) => _onHighroll.AddListener(listener);
    public void RemoveHighrollListener(System.Action<float> listener) => _onHighroll.RemoveListener(listener);

    // Same PredictedEvent reasoning as _onHighroll above. Part of a per-owner event chain (weapon fires ->
    // WeaponManagerPD relays -> owner's CharacterStatsPD fires its own -> subscribers like
    // PhantomBladeOnCritAugment) deliberately built to have zero shared/global channel anywhere, unlike the
    // old system's static WeaponBase.OnCritStrike bus. NotifyCritStrike is the external-invoke entry point
    // for WeaponRangedProjectilePD, which rolls its own crit but isn't a WeaponBasePD<TState> itself, so it
    // relays through the WeaponRangedPD that launched it instead.
    protected PredictedEvent<PredictedObjectID> _onCritStrike;

    public void AddCritStrikeListener(System.Action<PredictedObjectID> listener) => _onCritStrike.AddListener(listener);
    public void RemoveCritStrikeListener(System.Action<PredictedObjectID> listener) => _onCritStrike.RemoveListener(listener);
    public void NotifyCritStrike(PredictedObjectID target) => _onCritStrike.Invoke(target);

    protected override void LateAwake()
    {
        base.LateAwake();
        _onHighroll = new PredictedEvent<float>(predictionManager, this);
        _onCritStrike = new PredictedEvent<PredictedObjectID>(predictionManager, this);
    }

    // Shared "must separate then re-contact to re-hit" gate — both WeaponMeleePD and WeaponAxePD sweep one
    // or more shapes per tick and need this identically, so it lives here rather than being duplicated.
    // A cooldown alone isn't enough: without this, an enemy standing continuously inside a hitbox gets
    // rehit the instant its cooldown clears, even though the weapon never actually left and struck again.
    // Subclasses build their own local "touched this tick" set as they run their physics queries (they
    // need to iterate it anyway for their own hit pass — WeaponAxePD additionally needs it for its
    // outer-zone tie-break), then hand it to CommitOverlapFrame() once at the end of the tick.
    private readonly HashSet<IEnemyPD> _currentOverlaps = new();

    // True only if this enemy wasn't already touching last tick AND isn't on cooldown — a hit this tick
    // should only register once per "contact session," not every tick contact continues.
    protected bool IsFreshHit(IEnemyPD enemy, Dictionary<IEnemyPD, float> hitCooldowns)
        => !_currentOverlaps.Contains(enemy) && !hitCooldowns.ContainsKey(enemy);

    // Call once at the end of every tick's hit-detection pass, after all zones have been checked, passing
    // every enemy touched this tick (regardless of whether it resulted in a hit).
    protected void CommitOverlapFrame(IEnumerable<IEnemyPD> touchedThisTick)
    {
        _currentOverlaps.Clear();
        _currentOverlaps.UnionWith(touchedThisTick);
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

    // How many sub-arcs a swept-hitbox weapon (WeaponMeleePD, WeaponAxePD) should sample this tick instead
    // of sweeping straight from its start position to its end position. A straight-line sweep between two
    // points on a circle is a chord, not the arc itself — at high enough rotation speed (WeaponSpeed *
    // delta covering many degrees in one tick), that chord cuts across the circle's interior and can miss
    // an enemy sitting on the arc between the two sampled points entirely. Below SubArcTriggerDegrees a
    // single straight sweep is already close enough to the true arc that subdividing isn't worth it — by
    // far the common case at any realistic WeaponSpeed, and costs nothing extra (falls straight through to
    // the existing single-sweep behavior). Above that, subdivide down to MaxDegreesPerSubArc-sized
    // segments, capped at MaxSubArcs total no matter how much faster the rotation gets beyond that — more
    // than one full lap doesn't need finer coverage, it's the same circle of points either way.
    private const float SubArcTriggerDegrees = 45f;
    private const float MaxDegreesPerSubArc  = 30f;
    private const int   MaxSubArcs           = 12;

    protected static int ComputeSubArcCount(float angleDeltaDegrees)
    {
        float effective = Mathf.Min(Mathf.Abs(angleDeltaDegrees), 360f);
        if (effective <= SubArcTriggerDegrees) return 1;
        return Mathf.Clamp(Mathf.CeilToInt(effective / MaxDegreesPerSubArc), 2, MaxSubArcs);
    }
}
