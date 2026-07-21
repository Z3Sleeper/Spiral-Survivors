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
    // True for a weapon spawned onto OuterTrackAugment's second ring — set once by WeaponManagerPD at
    // registration time (see its own RegisterWeapon/_spawningOuter), never changes after. Read by
    // OrbitalPosition to orbit from just past the main track's own reach instead of from the player
    // directly, using a separate slot-count space, and by each weapon's own LateSimulate to spin the
    // opposite direction (see WeaponMeleePD.LateSimulate's own angleSign comment).
    bool IsOuterTrack { get; set; }
    bool IsMelee { get; }
    Transform transform { get; }
    Transform hitboxTarget { get; }
    Transform rangeScaleTarget { get; }
    float RangeValue { get; set; }
    Vector3 RootScale { get; set; }
    void Init(CharacterStatsPD stats);
    void InitModelVisuals();
    void ScaleModel(float range);
    // Lets WeaponManagerPD subscribe generically to any weapon's crit/hit events, regardless of concrete
    // type, so it can relay every registered weapon's crits/hits up to the owning player's own
    // CharacterStatsPD.
    void AddCritStrikeListener(System.Action<WeaponHitData> listener);
    void RemoveCritStrikeListener(System.Action<WeaponHitData> listener);
    void AddHitListener(System.Action<WeaponHitData> listener);
    void RemoveHitListener(System.Action<WeaponHitData> listener);
    // Fires once per REAL weapon connection with an enemy — deliberately separate from AddHitListener/OnHit,
    // which augments like CritTriggerOnHitAugment can (and do) invoke an extra time per real hit for their
    // own onhit-chain purposes. OnStrike is never touched by anything that re-triggers OnHit, so it's safe
    // for effects that must fire exactly once per genuine strike (e.g. OnStrikeEnergyRegenAugment).
    void AddStrikeListener(System.Action<WeaponHitData> listener);
    void RemoveStrikeListener(System.Action<WeaponHitData> listener);
    // Real-world distance from origin to this weapon's own current outer hit-detection edge — see
    // WeaponBasePD's shared implementation for why one formula covers every weapon type.
    float GetOuterReach(Vector3 origin);
}

// Shared payload for both the OnHit (every weapon hit) and OnCrit (the subset of hits that also crit)
// event chains — a crit is still a hit, so it carries the same hit data rather than just a bare target id.
// weaponDamage is the pre-luck-roll, pre-crit-multiplier damage baseline for THIS specific hit, not a
// flat re-read of CharacterStatsPD.WeaponDamage — WeaponRangedProjectilePD passes its own already
// ricochet-decayed State.currentDamage, and WeaponAxePD passes WeaponDamage * innerDamageMultiplier for
// handle (inner) hits, so downstream on-hit effects see the damage that hit actually represented rather
// than re-deriving it themselves per weapon type.
public struct WeaponHitData
{
    public PredictedObjectID target;
    public float weaponDamage;
    public float onHitPower;
    // Whether the hit that fired this event was itself a crit — onhit-triggered damage effects
    // (OnHitDamageAugment, OnHitDamageHealthRegenAugment) read this directly and crit in lockstep with the
    // original hit instead of rolling their own independent chance, same "if it crits, crit on all hits"
    // reasoning PhantomBladePD now uses.
    public bool isCrit;
}

// Shared payload for CharacterStatsPD._onAllyStrike — fired whenever any of this player's weapons touches
// (not damages) an ally, so any "hit ally, do something" augment can subscribe once rather than each
// needing its own copy of the weapon-side overlap-detection plumbing. A direct CharacterStatsPD reference
// (not a PredictedObjectID, unlike WeaponHitData's enemy target) is safe here — unlike enemies,
// CharacterStatsPD instances are never pooled/reused for a different player mid-match, so there's no
// SpawnGeneration-style staleness risk to guard against. onHitPower carries the same per-hit
// dropoff/penalty treatment as WeaponHitData (ricochet decay, axe handle penalty), so ally-strike effects
// scale down the same way onhit-damage effects already do.
public struct AllyStrikeData
{
    public CharacterStatsPD ally;
    public float onHitPower;
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

// Shared multishot fan-angle formula — used by both WeaponRangedPD.Fire() (player) and ShooterGloomPD (enemy),
// so "the same multishot behavior" holds for both without duplicating the math. One shot always goes
// straight (0°); the rest ("extra") split between left (negative) and right (positive), left getting the
// odd one out, each side's shots evenly spaced from near-center out to the full half-spread edge.
// Reconstructed from spec examples: extra=3, spread=135 -> {0, -33.75, -67.5, +67.5} (left gets 2, right
// gets 1; left's 2 shots sit at half*(1/2)=33.75 and half*(2/2)=67.5; right's 1 shot sits at half*(1/1)=67.5).
public static class MultishotMath
{
    // Fills `angles` (cleared first) with 1 + extra angle offsets in degrees. Static shared scratch list
    // pattern, not a fresh allocation per call — same "shared, immediately drained within one synchronous
    // call" convention as PhantomBladeSummonHelper._overlapResults/DashExplosionAugment._overlapResults.
    public static void ComputeAngles(int extra, float spread, List<float> angles)
    {
        angles.Clear();
        angles.Add(0f);
        if (extra <= 0) return;

        float half = spread / 2f;
        int leftCount = Mathf.CeilToInt(extra / 2f);
        int rightCount = extra - leftCount;

        for (int i = 1; i <= leftCount; i++)
            angles.Add(-half * (i / (float)leftCount));
        for (int i = 1; i <= rightCount; i++)
            angles.Add(half * (i / (float)rightCount));
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
    // Only ever queried when a weapon's owner has allyStrikeHitEnabled or allyStrikeDamageEnabled (any
    // "hit ally, do something" augment, e.g. OnStrikeHealAllyAugment, or real friendly fire) — every other
    // weapon skips the extra query entirely, so this costs nothing for players without either.
    public static readonly int PlayerHurtboxMask = LayerMask.GetMask("Player");
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

    // Decorative tip mesh (e.g. an arrowhead/blade point) that extends past the base hitbox/hb edge —
    // GetOuterReach can't see this from meleeHalfExtents/hb.lossyScale alone since it's a separate piece
    // with its own authored length, not part of the hitbox box itself. hitboxTipOffset is that length AT
    // SCALE 1 (same "base value, scaled live" convention as meleeHalfExtents); the scalesWith bools mark
    // which of hitboxTip's own localScale axes track range growth (e.g. a diamond tip in xzScaleFollowers
    // would check X and Z), so GetOuterReach knows which current scale value(s) to multiply the base
    // offset by. Optional — leave hitboxTip unassigned for weapons with no separate tip decoration.
    [SerializeField] private Transform hitboxTip;
    [SerializeField] private float     hitboxTipOffset = 0f;
    [SerializeField] private bool      hitboxTipScalesWithX = false;
    [SerializeField] private bool      hitboxTipScalesWithY = false;
    [SerializeField] private bool      hitboxTipScalesWithZ = false;

    private float     _hiltEdgeLocal;
    private float     _baseRangeScaleX;
    private float[]   _baseYScales;
    private Vector2[] _baseXZScales;
    private float[]   _baseXScales;
    // hitboxTipOffset is authored against hitboxTip's scale AT THIS captured moment (i.e. whatever the
    // prefab is currently set to) — so GetOuterReach multiplies by (current / base) per marked axis rather
    // than the raw current scale directly. That's what lets hitboxTipOffset be set to the actual,
    // Scene-view-measurable extension length at the prefab's authored scale, instead of a confusing
    // per-unit-scale constant the raw-current-scale approach would otherwise require.
    private Vector3   _hitboxTipBaseScale = Vector3.one;
    // Guards InitModelVisuals() below to a single real capture per GameObject instance — WeaponManagerPD
    // calls InitModelVisuals() from RegisterWeapon() on every logical spawn, including pooled reuse (since
    // LateAwake() re-fires then too). Without this, a reused instance would re-capture the "base" values
    // from wherever ScaleModel() left the visual transforms scaled to at the END of its PREVIOUS life,
    // instead of the prefab's true untouched default — corrupting designOrbit/_hiltEdgeLocal/
    // _baseRangeScaleX/etc. and producing a wrong (usually tiny, badly offset) scale on every reuse after
    // the first. Same "cache once in a hook that re-fires on pooled reuse" bug class as
    // WeaponRangedProjectilePD/PhantomBladePD's own _baseScale, just here fixed with a guard flag instead
    // of moving the capture into Awake() (RegisterWeapon/InitModelVisuals is called well after Awake(), so
    // there's no earlier hook available to move it to).
    private bool _modelVisualsInitialized;

    [System.NonSerialized] public float designOrbit;
    public float   RangeValue { get; set; }
    public Vector3 RootScale  { get; set; } = Vector3.one;
    public int     SlotIndex  { get; set; }
    public bool    IsOuterTrack { get; set; }

    // Every weapon (melee or ranged) registers with its owner's WeaponManagerPD the same way — see
    // WeaponMeleePD.TryInit()/WeaponRangedPD's equivalent — and OrbitalPosition() below needs WeaponCount
    // from it for the multi-weapon angular-spacing term, so this lives here rather than being duplicated
    // per weapon type.
    [System.NonSerialized] public WeaponManagerPD _manager;

    public virtual void Init(CharacterStatsPD stats) => _stats = stats;

    public void InitModelVisuals()
    {
        if (_modelVisualsInitialized) return;
        _modelVisualsInitialized = true;

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

        if (hitboxTip != null)
            _hitboxTipBaseScale = hitboxTip.localScale;
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
    protected PredictedEvent<WeaponHitData> _onCritStrike;

    public void AddCritStrikeListener(System.Action<WeaponHitData> listener) => _onCritStrike.AddListener(listener);
    public void RemoveCritStrikeListener(System.Action<WeaponHitData> listener) => _onCritStrike.RemoveListener(listener);
    public void NotifyCritStrike(WeaponHitData data) => _onCritStrike.Invoke(data);

    // Fires for every weapon hit (crit or not) — same per-owner event chain shape as _onCritStrike, and
    // likewise relayed through WeaponRangedPD by WeaponRangedProjectilePD (NotifyHit). A crit still invokes
    // this once via the normal hit path; augments that want a hit to count "extra" on crit (e.g.
    // CritTriggerOnHitAugment) do so by calling CharacterStatsPD.NotifyHit again themselves from their own
    // crit listener, not by this event double-firing on its own.
    protected PredictedEvent<WeaponHitData> _onHit;

    public void AddHitListener(System.Action<WeaponHitData> listener) => _onHit.AddListener(listener);
    public void RemoveHitListener(System.Action<WeaponHitData> listener) => _onHit.RemoveListener(listener);
    public void NotifyHit(WeaponHitData data) => _onHit.Invoke(data);

    // Fires once per REAL weapon connection with an enemy, invoked directly alongside _onHit at each
    // weapon's own actual hit-resolution call site (WeaponMeleePD.Hit/WeaponAxePD's equivalent/
    // WeaponRangedProjectilePD's ProcessHit) — never touched by anything that re-invokes _onHit/NotifyHit
    // (e.g. CritTriggerOnHitAugment), so it can't be duplicated the way _onHit can.
    protected PredictedEvent<WeaponHitData> _onStrike;

    public void AddStrikeListener(System.Action<WeaponHitData> listener) => _onStrike.AddListener(listener);
    public void RemoveStrikeListener(System.Action<WeaponHitData> listener) => _onStrike.RemoveListener(listener);
    public void NotifyStrike(WeaponHitData data) => _onStrike.Invoke(data);

    protected override void LateAwake()
    {
        base.LateAwake();
        _onHighroll = new PredictedEvent<float>(predictionManager, this);
        _onCritStrike = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onHit = new PredictedEvent<WeaponHitData>(predictionManager, this);
        _onStrike = new PredictedEvent<WeaponHitData>(predictionManager, this);
    }

    // Shared orbital-position formula — WeaponMeleePD/WeaponAxePD (swept hitbox) and WeaponRangedPD (fires
    // at whatever it has line-of-sight to) orbit their owner identically, only what happens once they're
    // positioned differs. WeaponCount comes from _manager so multiple simultaneous weapons space evenly
    // around the circle regardless of which weapon type(s) they are — an outer-track weapon (IsOuterTrack)
    // uses the manager's separate OuterWeaponCount/slot-space instead, so the two rings' spacing math never
    // interferes with each other. baseAngle is always ±_stats.WeaponOrbitAngle in practice — a single value
    // shared by every weapon this player owns, negated by outer-track weapons for the opposite spin
    // direction (see WeaponMeleePD.LateSimulate's own angleSign comment for why the sign has to be applied
    // at the call site, not in here) — so only the SlotIndex-based offset term below actually varies
    // between weapons on the SAME ring.
    protected Vector3 OrbitalPosition(float baseAngle, Vector3 origin)
    {
        int   total     = _manager != null ? Mathf.Max(1, IsOuterTrack ? _manager.OuterWeaponCount : _manager.WeaponCount) : 1;
        float rad       = (baseAngle + 360f / total * SlotIndex) * Mathf.Deg2Rad;
        float halfRange = rangeScaleTarget != null
            ? designOrbit * RootScale.z
            : (IsMelee ? RangeValue / 2f : 0.25f);
        // Outer-track weapons start their own orbit from just past the main track's full reach
        // (WeaponRadius + WeaponRange) instead of straight from the player — dynamically rescales with
        // range automatically since both are live stats, recomputed every frame this runs.
        float weaponRadiusOffset = IsOuterTrack ? _stats.WeaponRadius + _stats.WeaponRange : _stats.WeaponRadius;
        float radius = weaponRadiusOffset + _stats.transform.lossyScale.x * 0.5f + halfRange;
        return origin + new Vector3(Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);
    }

    // Used by PlayerRangeIndicatorPD to show players exactly where their weapons can actually hit, without
    // duplicating each weapon type's own hitbox geometry. WeaponMeleePD's box (half-extent along hb's local
    // Z = meleeHalfExtents.z * hb.lossyScale.z, default 0.5) and WeaponAxePD's inner-box+outer-capsule zones
    // (whose farthest reach past hb.position works out to exactly hb.lossyScale.z * 0.5 once the
    // handle/blade ratio split cancels out — see WeaponAxePD.ComputeZones) both reduce to the same "half of
    // hb.lossyScale.z past hb.position" formula, so one shared implementation covers both without needing
    // per-subclass overrides. hb.forward always points radially outward from the player (LookRotation(
    // outward) is set every tick in LateSimulate), so a scalar distance-plus-half-extent is exact, not an
    // approximation. Ranged weapons have no melee-style fixed hitbox at all — their real reach is
    // target-acquisition range (WeaponRangedPD.FindTarget), whose raycast pulls its origin back by 1 unit
    // and extends range by +1 to compensate, exactly cancelling out — so the real reach past the weapon's
    // own live position is just WeaponRange. Measuring from transform.position directly (like the melee
    // branch measures from hb.position) rather than reconstructing it from WeaponRadius/track offset keeps
    // this exact regardless of orbit clearance terms, instead of dropping them like a stats-only formula
    // would.
    public float GetOuterReach(Vector3 origin)
    {
        if (!IsMelee)
            return Vector3.Distance(origin, transform.position) + _stats.WeaponRange;

        Transform hb = hitboxTarget != null ? hitboxTarget : transform;
        // hitboxTip's own pivot tracks the same range-driven position as hb's far edge (both derive from
        // the same WeaponRange-based math, via rangeFollowers/SetupWeaponRange respectively), so its extra
        // length is added directly on top rather than measured from a second, separate origin.
        return Vector3.Distance(origin, hb.position) + hb.lossyScale.z * 0.5f + GetHitboxTipExtraReach();
    }

    // Shared by GetOuterReach above and WeaponMeleePD's own hit-detection box, so the actual hitbox and the
    // cosmetic range indicator can never drift apart — the real extra forward reach hitboxTip contributes
    // right now, scaled by (current / base) per marked axis rather than the raw current scale (see
    // _hitboxTipBaseScale's own comment for why). 0 if no tip is assigned.
    protected float GetHitboxTipExtraReach()
    {
        if (hitboxTip == null || hitboxTipOffset == 0f) return 0f;

        float ratio = 0f;
        int   axisCount = 0;
        if (hitboxTipScalesWithX && _hitboxTipBaseScale.x > 0f) { ratio += hitboxTip.localScale.x / _hitboxTipBaseScale.x; axisCount++; }
        if (hitboxTipScalesWithY && _hitboxTipBaseScale.y > 0f) { ratio += hitboxTip.localScale.y / _hitboxTipBaseScale.y; axisCount++; }
        if (hitboxTipScalesWithZ && _hitboxTipBaseScale.z > 0f) { ratio += hitboxTip.localScale.z / _hitboxTipBaseScale.z; axisCount++; }
        return hitboxTipOffset * (axisCount > 0 ? ratio / axisCount : 1f);
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
