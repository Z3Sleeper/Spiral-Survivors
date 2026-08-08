using System;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

// Stacking rework (see feedback thread this was designed from): the moment a hit lands, its damage number
// prints IMMEDIATELY and locks to a fixed position relative to the enemy — no arc animation, no lifetime
// countdown yet. Any further hit from the SAME "source" (see DamageSource) landing within _stackWindow of
// the last one ADDS onto that same number (sliding window: each addition also resets the countdown) instead
// of printing its own. Once the window elapses with no further same-source hit, the number is "released"
// into its normal arc/fade/despawn animation from wherever it's currently sitting.
//
// Grouping is two-tiered:
// - "Stack" = (enemy, attacking player) — the vertical arrangement multiple different concurrent sources
//   land in on the same enemy from the same attacker.
// - "Group" = (stack, DamageSource) — the actual accumulator every same-source hit within the window feeds
//   into. Two different sources (e.g. ShieldBreakBurst and OnHitDamageHealthAugment) landing on the same
//   enemy at the same time never merge into one number, even though they share a stack.
// Every additional group's number stacks ON TOP of the previous ones within its stack, at _stackHeightStep
// world-Y per slot (5th slot = +5 * _stackHeightStep). Weapon-source groups always claim the bottom slot
// (height 0): if a fresh Weapon group is created while other groups are still locked on the same stack,
// every one of those other groups is bumped up by one slot to make room. A released group's own slot is
// simply freed (not renumbered/backfilled) — see DamageNumber.Release's own doc comment for why only the
// still-locked, pre-animation stack ever reorders.
public class DamageNumberManager : MonoBehaviour
{
    public static DamageNumberManager instance;

    [SerializeField] private DamageNumber _prefab;
    [SerializeField] private int _poolSize = 30;
    [SerializeField] private float _spawnYOffset = 1.5f;
    // Not applied at spawn — handed to DamageNumber.InitLocked()/Release() as the X distance to arc toward
    // once released (see DamageNumber.Animate()). Magnitude is randomized per NEW group (Random.Range(min,
    // max)); the SIGN alternates per stack (see _goRight) so consecutive groups on the same stack visibly
    // separate instead of risking landing on the same side twice in a row.
    [SerializeField] private float _minSpawnXJitter = 1f;
    [SerializeField] private float _maxSpawnXJitter = 1.5f;
    // The tight same-source stacking window — see class doc comment. Sliding: every additional same-source
    // hit resets this back to the full value, so a same-source burst only starts animating once hits
    // actually stop arriving.
    [SerializeField] private float _stackWindow = 0.05f;
    // World-Y spacing between simultaneously-locked groups on the same stack — see class doc comment.
    [SerializeField] private float _stackHeightStep = 1f;

    private readonly Queue<DamageNumber> _pool = new();

    private class LockedGroup
    {
        public DamageNumber number;
        public IEnemyPD enemy;
        public int enemyGeneration;
        public float sizeYOffset;
        public float sizeZOffset;
        public float amount;
        public bool isCrit;
        public bool isHighroll;
        public float windowRemaining;
        public int stackKey;
        public int height;
    }

    // Keyed by CombineGroupKey(stackKey, source) — the accumulator every same-source hit within _stackWindow
    // feeds into. See class doc comment.
    private readonly Dictionary<int, LockedGroup> _lockedGroups = new();
    // Keyed by stackKey (enemy + attacking player) — the ordered set of groupKeys currently locked on that
    // stack, bottom to top, used purely to assign each new group's height. A released group is removed from
    // here but the remaining entries are NOT renumbered/compacted — only inserting a fresh Weapon group
    // forces a reflow (see class doc comment).
    private readonly Dictionary<int, List<int>> _stackOrder = new();
    // One alternating left/right toggle per stack, flipped every time a NEW group is created on it.
    private readonly Dictionary<int, bool> _goRight = new();
    // Reused every Update() to snapshot which groupKeys expired this frame — can't remove from _lockedGroups
    // while enumerating it directly.
    private readonly List<int> _expiredScratch = new();

    private void Awake()
    {
        instance = this;
        for (int i = 0; i < _poolSize; i++)
        {
            var num = Instantiate(_prefab, transform);
            num.gameObject.SetActive(false);
            _pool.Enqueue(num);
        }
    }

    private void Update()
    {
        if (_lockedGroups.Count == 0) return;
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        _expiredScratch.Clear();
        foreach (var kvp in _lockedGroups)
        {
            var group = kvp.Value;
            // Target gone (destroyed or pooled/reused for a different enemy) — force-release immediately
            // rather than waiting out the rest of the window on an enemy that no longer exists.
            if (!IsStillTheSameEnemy(group.enemy, group.enemyGeneration))
            {
                _expiredScratch.Add(kvp.Key);
                continue;
            }
            group.windowRemaining -= Time.deltaTime;
            if (group.windowRemaining <= 0f) _expiredScratch.Add(kvp.Key);
        }
        for (int i = 0; i < _expiredScratch.Count; i++)
            ReleaseGroup(_expiredScratch[i]);
    }

    // stackKey identifies the (enemy, attacking player) vertical arrangement this hit belongs to — computed
    // by the caller (EnemyBasePD.ShowDamageNumber, same HashCode.Combine(GetInstanceID(), attackerOwner)
    // formula as before) since it's the one that already has direct access to GetInstanceID(). damageSource
    // is the actual accumulation bucket within that stack — see class doc comment for the full rule.
    // sizeYOffset/sizeZOffset nudge the spawn point along world-space +Y/+Z (not source's own facing), so
    // the number spawns off the model along fixed world axes instead of centered inside it; sizeYOffset
    // stacks on top of _spawnYOffset (the manager-wide default lift), both BELOW the per-group stack height.
    public void Spawn(float amount, IEnemyPD source, float sizeYOffset, float sizeZOffset, int stackKey, DamageSource damageSource, bool isCrit = false, bool isHighroll = false, PlayerID attackerOwner = default, bool isPriority = false)
    {
        if (amount <= 0f || IsDestroyed(source)) return;

        if (!attackerOwner.Equals(default(PlayerID)))
        {
            bool isOwn = CharacterStatsPD.All.TryGetValue(attackerOwner, out var stats) && stats.isOwner;
            if (!isOwn && !Settings.SeeOtherPlayerDamage) return;
        }

        int groupKey = CombineGroupKey(stackKey, damageSource);

        // Same-source hit within the window — fold into the existing locked number instead of printing a
        // new one, and slide the window back out to the full value.
        if (_lockedGroups.TryGetValue(groupKey, out var existing))
        {
            existing.amount += amount;
            existing.isCrit |= isCrit;
            existing.isHighroll |= isHighroll;
            existing.windowRemaining = _stackWindow;
            existing.number.SetAmount(existing.amount, existing.isCrit, existing.isHighroll);
            return;
        }

        if (!_stackOrder.TryGetValue(stackKey, out var order))
        {
            order = new List<int>();
            _stackOrder[stackKey] = order;
        }

        int height;
        if (damageSource == DamageSource.Weapon)
        {
            // Weapon always claims the bottom slot — every other currently-locked group on this stack bumps
            // up by one to make room (see class doc comment).
            for (int i = 0; i < order.Count; i++)
                if (_lockedGroups.TryGetValue(order[i], out var other))
                    other.number.SetHeight(++other.height * _stackHeightStep);
            height = 0;
            order.Insert(0, groupKey);
        }
        else
        {
            height = order.Count;
            order.Add(groupKey);
        }

        bool goRight = !_goRight.TryGetValue(stackKey, out bool last) || !last;
        _goRight[stackKey] = goRight;
        float magnitude = UnityEngine.Random.Range(_minSpawnXJitter, _maxSpawnXJitter);
        float xOffset = goRight ? magnitude : -magnitude;

        var num = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(_prefab, transform);
        Vector3 pos = ResolvePosition(source, sizeYOffset, sizeZOffset) + new Vector3(0f, _spawnYOffset, 0f);
        num.InitLocked(amount, pos, height * _stackHeightStep, xOffset, source, this, isCrit, isHighroll, isPriority);

        _lockedGroups[groupKey] = new LockedGroup
        {
            number = num,
            enemy = source,
            enemyGeneration = source.SpawnGeneration,
            sizeYOffset = sizeYOffset,
            sizeZOffset = sizeZOffset,
            amount = amount,
            isCrit = isCrit,
            isHighroll = isHighroll,
            windowRemaining = _stackWindow,
            stackKey = stackKey,
            height = height,
        };
    }

    private void ReleaseGroup(int groupKey)
    {
        if (!_lockedGroups.TryGetValue(groupKey, out var group)) return;
        _lockedGroups.Remove(groupKey);

        if (_stackOrder.TryGetValue(group.stackKey, out var order))
        {
            order.Remove(groupKey);
            // Prune fully-emptied stacks so a very long session with many distinct enemies doesn't leave
            // behind an ever-growing pile of empty entries.
            if (order.Count == 0)
            {
                _stackOrder.Remove(group.stackKey);
                _goRight.Remove(group.stackKey);
            }
        }

        group.number.Release();
    }

    private static int CombineGroupKey(int stackKey, DamageSource source) => HashCode.Combine(stackKey, (int)source);

    // Plain `source == null` on an IEnemyPD-typed reference does NOT detect a truly-destroyed Unity object
    // — Unity's overloaded "fake null" equality operator only applies when the compile-time type is
    // UnityEngine.Object (or a subclass); for an interface-typed variable, C# falls back to plain
    // reference equality, which stays "non-null" even after the underlying enemy is destroyed. Casting to
    // Object first routes the check through Unity's real destroyed-check instead. Public/static so
    // DamageNumber can use the exact same check for its own _target.
    public static bool IsDestroyed(IEnemyPD enemy) => enemy == null || (UnityEngine.Object)enemy == null;

    // source stays a valid, non-null reference even after its GameObject gets pooled/recycled for a
    // completely different enemy spawn — SpawnGeneration is the only reliable signal that it's still the
    // SAME logical enemy this entry was originally locked against.
    private static bool IsStillTheSameEnemy(IEnemyPD source, int capturedGeneration) =>
        !IsDestroyed(source) && source.SpawnGeneration == capturedGeneration;

    // World-space +Y/-Z, not source's own up/forward — a fixed nudge along world axes regardless of which
    // way the enemy happens to be facing or oriented. sizeZOffset is negated since -Z is "forward" here.
    private Vector3 ResolvePosition(IEnemyPD source, float sizeYOffset, float sizeZOffset) =>
        source.Visuals.position + new Vector3(0f, sizeYOffset, -sizeZOffset);

    public void Return(DamageNumber num) => _pool.Enqueue(num);
}
