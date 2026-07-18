using System.Collections;
using System.Collections.Generic;
using PurrNet;
using UnityEngine;

public class DamageNumberManager : MonoBehaviour
{
    public static DamageNumberManager instance;

    [SerializeField] private DamageNumber _prefab;
    [SerializeField] private int _poolSize = 30;
    [SerializeField] private float _spawnYOffset = 1.5f;
    // Not applied at spawn — rolled here, then handed to DamageNumber.Init() as the X distance to arc
    // toward over its lifetime (see DamageNumber.Animate()), rather than an instant spawn-position offset.
    [SerializeField] private float _spawnXJitter = 0.4f;
    // Every number stays separate (never summed into one) — this just spaces out same-groupKey hits
    // landing in the same instant so they don't render as a single unreadable overlapping stack.
    [SerializeField] private float _staggerDelay = 0.05f;

    private readonly Queue<DamageNumber> _pool = new();

    private struct QueuedNumber
    {
        // IEnemyPD, not a baked Vector3 — resolved live at actual spawn time (see ResolvePosition()), not
        // at enqueue time. A deep queue backlog (hits queuing faster than _staggerDelay drains them) means
        // a late entry can pop a full second or more after it was queued; a source that's still moving (a
        // boss walking/charging) would otherwise show every one of those late numbers frozen at wherever
        // it was back when each hit was originally registered, clustering far from its current position
        // instead of tracking it.
        public IEnemyPD source;
        // Captured at enqueue time (source is always alive then) — SpawnGeneration lets IsStillTheSameEnemy
        // detect pooled reuse (source itself stays a valid non-null reference even after its GameObject is
        // recycled for a different spawn), and fallbackPos is used if source is no longer the same enemy by
        // the time this entry's turn to drain comes up, instead of silently dropping the number entirely
        // (the earlier "numbers disappear when enemies die" bug) or snapping to wherever the recycled
        // GameObject now is (the later, actually-reported version of that same bug).
        public int sourceGeneration;
        public Vector3 fallbackPos;
        public float sizeYOffset;
        public float sizeZOffset;
        public float damage;
        public bool isCrit;
        public bool isHighroll;
        public bool isPriority;
    }
    // One queue per groupKey — DrainQueue() below pops and spawns one entry at a time, `_staggerDelay`
    // apart, picking up any new entries queued mid-drain rather than needing its own restart. LinkedList,
    // not Queue<T> — priority entries (real weapon hits) need to jump to the front ahead of whatever's
    // already queued (onhit effects, Phantom Blades, etc.), which a plain FIFO queue can't do.
    private readonly Dictionary<int, LinkedList<QueuedNumber>> _queues = new();
    // Tracks which groupKeys currently have a DrainQueue() coroutine actively running — queue.Count alone
    // can't tell you this: once a coroutine dequeues its first entry (synchronously, before its first
    // yield) the queue is back to Count == 0 while the coroutine is still alive and paused between spawns,
    // so the next Spawn() call would also see Count == 1 after enqueueing and wrongly start a second,
    // concurrent coroutine for the same key — this is what caused several onhit effects on the same hit to
    // all pop up simultaneously instead of staggering.
    private readonly HashSet<int> _draining = new();

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

    // source is the damage source (e.g. an enemy) — its position is read live off source.Visuals wherever
    // actually needed (immediately here, or later inside DrainQueue()), never baked in ahead of time.
    // sizeYOffset/sizeZOffset nudge the spawn point along world-space +Y/+Z by that many units (not
    // source's own facing direction), so the number spawns off the model along fixed world axes instead
    // of centered inside it. sizeYOffset stacks on top of _spawnYOffset (the manager-wide default lift).
    // isPriority jumps this entry to the front of the queue (ahead of anything already waiting) instead of
    // the back — real weapon hits use this so they always show up before onhit-effect/Phantom Blade
    // numbers from the same burst, regardless of invocation order.
    public void Spawn(float amount, IEnemyPD source, float sizeYOffset, float sizeZOffset, int groupKey = 0, bool isCrit = false, bool isHighroll = false, PlayerID attackerOwner = default, bool isPriority = false)
    {
        if (amount <= 0f || IsDestroyed(source)) return;

        if (!attackerOwner.Equals(default(PlayerID)))
        {
            bool isOwn = CharacterStatsPD.All.TryGetValue(attackerOwner, out var stats) && stats.isOwner;
            if (!isOwn && !Settings.SeeOtherPlayerDamage) return;
        }

        if (groupKey == 0)
        {
            SpawnImmediate(amount, source, ResolvePosition(source, sizeYOffset, sizeZOffset), isCrit, isHighroll, isPriority);
            return;
        }

        if (!_queues.TryGetValue(groupKey, out var queue))
        {
            queue = new LinkedList<QueuedNumber>();
            _queues[groupKey] = queue;
        }
        var entry = new QueuedNumber
        {
            source = source,
            sourceGeneration = source.SpawnGeneration,
            fallbackPos = ResolvePosition(source, sizeYOffset, sizeZOffset),
            sizeYOffset = sizeYOffset,
            sizeZOffset = sizeZOffset,
            damage = amount,
            isCrit = isCrit,
            isHighroll = isHighroll,
            isPriority = isPriority
        };
        if (isPriority) queue.AddFirst(entry);
        else queue.AddLast(entry);

        // HashSet.Add returns true only if groupKey wasn't already present — the one atomic "claim this
        // key" check, unlike inferring it from queue.Count (see _draining's doc comment).
        if (_draining.Add(groupKey))
            StartCoroutine(DrainQueue(groupKey));
    }

    // Plain `source == null` on an IEnemyPD-typed reference does NOT detect a truly-destroyed Unity object
    // — Unity's overloaded "fake null" equality operator only applies when the compile-time type is
    // UnityEngine.Object (or a subclass); for an interface-typed variable, C# falls back to plain
    // reference equality, which stays "non-null" even after the underlying enemy is destroyed. Casting to
    // Object first routes the check through Unity's real destroyed-check instead. Public/static so
    // DamageNumber can use the exact same check for its own _target.
    public static bool IsDestroyed(IEnemyPD enemy) => enemy == null || (Object)enemy == null;

    // source stays a valid, non-null reference even after its GameObject gets pooled/recycled for a
    // completely different enemy spawn — SpawnGeneration is the only reliable signal that it's still the
    // SAME logical enemy this entry was originally queued against.
    private static bool IsStillTheSameEnemy(IEnemyPD source, int capturedGeneration) =>
        !IsDestroyed(source) && source.SpawnGeneration == capturedGeneration;

    private IEnumerator DrainQueue(int groupKey)
    {
        var wait = new WaitForSeconds(_staggerDelay);
        // Re-checks queue.Count every loop, so hits that arrive mid-drain (still within the same burst)
        // get picked up and staggered too, instead of only draining what existed at coroutine start.
        while (_queues.TryGetValue(groupKey, out var queue) && queue.Count > 0)
        {
            var entry = queue.First.Value;
            queue.RemoveFirst();
            bool alive = IsStillTheSameEnemy(entry.source, entry.sourceGeneration);
            Vector3 pos = alive ? ResolvePosition(entry.source, entry.sizeYOffset, entry.sizeZOffset) : entry.fallbackPos;
            SpawnImmediate(entry.damage, alive ? entry.source : null, pos, entry.isCrit, entry.isHighroll, entry.isPriority);
            yield return wait;
        }
        _queues.Remove(groupKey);
        _draining.Remove(groupKey);
    }

    // World-space +Y/-Z, not source's own up/forward — a fixed nudge along world axes regardless of which
    // way the enemy happens to be facing or oriented. sizeZOffset is negated since -Z is "forward" here.
    private Vector3 ResolvePosition(IEnemyPD source, float sizeYOffset, float sizeZOffset) =>
        source.Visuals.position + new Vector3(0f, sizeYOffset, -sizeZOffset);

    // pos is the already-resolved spawn position (live off source, or the queued fallback if source is no
    // longer the same enemy by drain time) — source is still passed through separately so DamageNumber can
    // keep tracking it live for the rest of its own animation if it's still the same enemy.
    private void SpawnImmediate(float amount, IEnemyPD source, Vector3 pos, bool isCrit, bool isHighroll, bool isPriority)
    {
        var num = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(_prefab, transform);
        float xOffset = Random.Range(-_spawnXJitter, _spawnXJitter);
        num.Init(amount, pos + new Vector3(0f, _spawnYOffset, 0f), xOffset, source, this, isCrit, isHighroll, isPriority);
    }

    public void Return(DamageNumber num) => _pool.Enqueue(num);
}
