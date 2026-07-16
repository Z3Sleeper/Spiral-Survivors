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
    [SerializeField] private float _spawnXJitter = 0.4f;
    [SerializeField] private float _accumulateWindow = 0.02f;

    private readonly Queue<DamageNumber> _pool = new();

    private struct PendingNumber
    {
        public float damage;
        public Vector3 pos;
        public bool hasCrit;
        public bool hasHighroll;
        public Coroutine routine;
    }
    private readonly Dictionary<int, PendingNumber> _pending = new();

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

    public void Spawn(float amount, Vector3 worldPos, int groupKey = 0, bool isCrit = false, bool isHighroll = false, PlayerID attackerOwner = default)
    {
        if (amount <= 0f) return;

        if (!attackerOwner.Equals(default(PlayerID)))
        {
            bool isOwn = CharacterStatsPD.All.TryGetValue(attackerOwner, out var stats) && stats.isOwner;
            if (!isOwn && !Settings.SeeOtherPlayerDamage) return;
        }

        if (groupKey != 0 && _pending.TryGetValue(groupKey, out var entry))
        {
            _pending[groupKey] = new PendingNumber
            {
                damage = entry.damage + amount,
                pos = entry.pos,
                hasCrit = entry.hasCrit || isCrit,
                hasHighroll = entry.hasHighroll || isHighroll,
                routine = entry.routine
            };
        }
        else if (groupKey != 0)
        {
            _pending[groupKey] = new PendingNumber
            {
                damage = amount,
                pos = worldPos,
                hasCrit = isCrit,
                hasHighroll = isHighroll,
                routine = StartCoroutine(Flush(groupKey))
            };
        }
        else
        {
            SpawnImmediate(amount, worldPos, isCrit, isHighroll);
        }
    }

    private IEnumerator Flush(int groupKey)
    {
        yield return new WaitForSeconds(_accumulateWindow);
        if (_pending.TryGetValue(groupKey, out var entry))
        {
            _pending.Remove(groupKey);
            SpawnImmediate(entry.damage, entry.pos, entry.hasCrit, entry.hasHighroll);
        }
    }

    private void SpawnImmediate(float amount, Vector3 worldPos, bool isCrit, bool isHighroll)
    {
        var num = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(_prefab, transform);
        float xOffset = Random.Range(-_spawnXJitter, _spawnXJitter);
        num.Init(amount, worldPos + new Vector3(xOffset, _spawnYOffset, 0f), this, isCrit, isHighroll);
    }

    public void Return(DamageNumber num) => _pool.Enqueue(num);
}
