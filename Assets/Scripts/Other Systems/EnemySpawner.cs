using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PurrNet;
using UnityEngine;

public enum EnemyTier { Normal, Elite, Miniboss, Boss, Pleb }

[System.Serializable]
public class EnemyEntry
{
    public GameObject prefab;
    public float weight = 1f;
    public EnemyTier tier;
}

public class EnemySpawner : NetworkIdentity
{
    private static readonly WaitForSeconds _waitForSeconds0_5 = new(0.5f);
    public static EnemySpawner instance;

    [SerializeField] private float ScalingHealthFactor = 1.15f;
    [SerializeField] private float ScalingHealthAddend = 0.5f;
    [SerializeField] private float ScalingDamageFactor = 1f;
    [SerializeField] private float ScalingDamageAddend = 0.5f;
    [SerializeField] private float ScalingSpeedFactor = 1f;
    [SerializeField] private float ScalingSpeedAddend = 0.1f;

    [SerializeField] private List<EnemyEntry> enemyEntries;
    [SerializeField] private float spawnMinDistance = 25f;
    [SerializeField] private float spawnMaxDistance = 40f;
    [SerializeField] private float baseSpawnInterval = 1f;
    private float _spawnInterval;
    [SerializeField] private float cycleInterval = 150f; // 2.5 minutes
    [SerializeField] private float baseEliteChance = 0.1f;
    [SerializeField] private int baseEliteCap = 2;
    public float spawnTimeCycleScalar = 0.9f;
    public float coopSpawnTimeScalar = 0.5f;
    [SerializeField] private float coopBossHealthScalar = 0.5f;
    [SerializeField] private bool waitForPlayersLoaded = true;
    [SerializeField] private int maxEnemies = 300;

    private List<EnemyEntry> _normalEnemies = new();
    private List<EnemyEntry> _eliteEnemies = new();
    private List<EnemyEntry> _minibossEnemies = new();
    private List<EnemyEntry> _bossEnemies = new();
    private List<EnemyEntry> _plebEnemies = new();
    [SerializeField] private float plebSpawnInterval = 1f;

    private List<EnemyEntry> _currentSet = new();
    private List<EnemyEntry> _previousSet = new();

    [NonSerialized] public SyncVar<float> _cycleTimer = new(0f);
    [HideInInspector] public float CycleTimer
    {
        get => _cycleTimer.value;
        set => _cycleTimer.value = value;
    }
    private int _cycleCount = 0;
    private int _elitesSpawnedThisCycle = 0;
    private float _currentEliteChance;
    private int _currentEliteCap;
    private int _playerCount;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        CycleTimer = cycleInterval;
    }

    protected override void OnSpawned()
    {
        base.OnSpawned();
        if (!isServer) return;

        // Sort entries by tier
        foreach (var entry in enemyEntries)
        {
            switch (entry.tier)
            {
                case EnemyTier.Normal: _normalEnemies.Add(entry); break;
                case EnemyTier.Elite: _eliteEnemies.Add(entry); break;
                case EnemyTier.Miniboss: _minibossEnemies.Add(entry); break;
                case EnemyTier.Boss: _bossEnemies.Add(entry); break;
                case EnemyTier.Pleb: _plebEnemies.Add(entry); break;
            }
        }

        RollNewSet();

        if (!waitForPlayersLoaded)
            StartCoroutine(DelayedStart());
    }

    private IEnumerator DelayedStart()
    {
        yield return _waitForSeconds0_5;
        StartSpawning();
    }

    public void StartSpawning()
    {
        EnemyBase.ActiveCount = 0;
        _playerCount = networkManager.playerCount;
        _spawnInterval = baseSpawnInterval / (1f + (_playerCount - 1) * coopSpawnTimeScalar);
        _currentEliteChance = baseEliteChance;
        _currentEliteCap = baseEliteCap * _playerCount;
        CycleTimer = cycleInterval;
        StartCoroutine(SpawnLoop());
        StartCoroutine(CycleLoop());
        if (_plebEnemies.Count > 0)
            StartCoroutine(PlebSpawnLoop());
    }

    private IEnumerator SpawnLoop()
    {
        while (true)
        {
            if (EnemyBase.ActiveCount < maxEnemies)
            {
                if (_eliteEnemies.Count > 0 && _elitesSpawnedThisCycle < _currentEliteCap
                    && UnityEngine.Random.value < _currentEliteChance)
                {
                    SpawnThisEnemy(GetWeightedRandom(_eliteEnemies), false);
                    _elitesSpawnedThisCycle++;
                }
                else if (_currentSet.Count > 0)
                {
                    SpawnThisEnemy(GetWeightedRandom(_currentSet), false);
                }
            }

            yield return new WaitForSeconds(_spawnInterval);
        }
    }

    private IEnumerator PlebSpawnLoop()
    {
        float interval = plebSpawnInterval / (1f + (_playerCount - 1) * coopSpawnTimeScalar);
        while (true)
        {
            for (int i = 0; i < 5; i++)
            {
                if (EnemyBase.ActiveCount < maxEnemies)
                    SpawnThisEnemy(GetWeightedRandom(_plebEnemies), false);
            }
            yield return new WaitForSeconds(interval);
        }
    }

    private IEnumerator CycleLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(baseSpawnInterval * 0.1f); // tick frequently

            CycleTimer -= baseSpawnInterval * 0.1f;
            if (CycleTimer > 0f) continue;

            CycleTimer = cycleInterval;
            _cycleCount++;
            _elitesSpawnedThisCycle = 0;

            // Increase elite chance and cap
            _currentEliteChance += baseEliteChance * 0.5f;
            _currentEliteCap = (baseEliteCap + _cycleCount) * _playerCount;
            
            // Increase spawn rate per cycle
            _spawnInterval = baseSpawnInterval / (1f + (_playerCount - 1) * coopSpawnTimeScalar) * Mathf.Pow(spawnTimeCycleScalar, _cycleCount);

            // Spawn miniboss or boss
            if (_cycleCount % 4 == 0 && _bossEnemies.Count > 0)
                SpawnThisEnemy(GetWeightedRandom(_bossEnemies), true);
            else if (_minibossEnemies.Count > 0)
                SpawnThisEnemy(GetWeightedRandom(_minibossEnemies), true);

            RollNewSet();
        }
    }

    private void RollNewSet()
    {
        if (_normalEnemies.Count == 0) return;

        List<EnemyEntry> pool = new(_normalEnemies);
        List<EnemyEntry> newSet = new();

        // Pick 3 random from pool
        int pickCount = Mathf.Min(3, pool.Count);
        for (int i = 0; i < pickCount; i++)
        {
            int idx = UnityEngine.Random.Range(0, pool.Count);
            newSet.Add(pool[idx]);
            pool.RemoveAt(idx);
        }

        // Never same set twice - with only 4 enemies, swap one if all 3 match
        if (_previousSet.Count == 3 && newSet.Count == 3)
        {
            bool allSame = !newSet.Except(_previousSet).Any();
            if (allSame)
            {
                // Find the one enemy not in the current set and swap it in
                var excluded = _normalEnemies.Except(newSet).FirstOrDefault();
                if (excluded != null)
                    newSet[UnityEngine.Random.Range(0, newSet.Count)] = excluded;
            }

            /* TODO: When more than 4 enemies are available, use this instead:
            // Shuffle entire pool excluding previous set entries
            var availablePool = _normalEnemies.Except(_previousSet).ToList();
            if (availablePool.Count >= 3)
            {
                newSet.Clear();
                for (int i = 0; i < 3; i++)
                {
                    int idx = Random.Range(0, availablePool.Count);
                    newSet.Add(availablePool[idx]);
                    availablePool.RemoveAt(idx);
                }
            }
            */
        }

        _previousSet = new List<EnemyEntry>(_currentSet);
        _currentSet = newSet;
    }

    private void SpawnThisEnemy(EnemyEntry entry, bool isBoss)
    {
        var obj = Instantiate(entry.prefab, GetSpawnPosition(), Quaternion.identity);
        var enemy = obj.GetComponent<EnemyBase>();
        if (_cycleCount > 0)
        {
            enemy.maxHealth += enemy.baseHealth * ScalingHealthAddend * _cycleCount;
            enemy.maxHealth *= ScalingHealthFactor;
            enemy.damage += enemy.baseDamage * ScalingDamageAddend * _cycleCount;
            enemy.damage *= ScalingDamageFactor;
            enemy.moveSpeed += enemy.baseMoveSpeed * ScalingSpeedAddend * _cycleCount;
            enemy.moveSpeed *= ScalingSpeedFactor;
        }
        if (isBoss)
            enemy.maxHealth *= 1f + (_playerCount - 1) * coopBossHealthScalar;
    }

    private EnemyEntry GetWeightedRandom(List<EnemyEntry> entries)
    {
        float total = entries.Sum(e => e.weight);
        float roll = UnityEngine.Random.Range(0f, total);
        float cumulative = 0f;
        foreach (var entry in entries)
        {
            cumulative += entry.weight;
            if (roll <= cumulative) return entry;
        }
        return entries[^1];
    }

    private Vector3 GetSpawnPosition()
    {
        var players = CharacterStats.All.Where(p => !p.IsDead).ToList();

        if (players.Count == 0) return Vector3.zero;

        Vector3 pos;
        int attempts = 0;

        do
        {
            var randomPlayer = players[UnityEngine.Random.Range(0, players.Count)];
            float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float distance = UnityEngine.Random.Range(spawnMinDistance, spawnMaxDistance);
            pos = randomPlayer.transform.position + new Vector3(Mathf.Cos(angle) * distance, 0, Mathf.Sin(angle) * distance);
            attempts++;
        }
        while (!IsFarEnoughFromPlayers(pos, players) && attempts < 30);

        return pos;
    }

    private bool IsFarEnoughFromPlayers(Vector3 pos, List<CharacterStats> players)
    {
        foreach (var player in players)
        {
            if (Vector3.Distance(pos, player.transform.position) < spawnMinDistance)
                return false;
        }
        return true;
    }
}