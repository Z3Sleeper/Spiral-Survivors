using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PurrNet;
using UnityEngine;

public enum EnemyTier { Normal, Elite, Miniboss, Boss }

[System.Serializable]
public class EnemyEntry
{
    public GameObject prefab;
    public float weight = 1f;
    public EnemyTier tier;
}

public class EnemySpawner : NetworkIdentity
{
    public static EnemySpawner instance;

    [SerializeField] private List<EnemyEntry> enemyEntries;
    [SerializeField] private float spawnMinDistance = 25f;
    [SerializeField] private float spawnMaxDistance = 40f;
    [SerializeField] private float spawnInterval = 1f;
    [SerializeField] private float cycleInterval = 150f; // 2.5 minutes
    [SerializeField] private float baseEliteChance = 0.1f;
    [SerializeField] private int baseEliteCap = 2;
    [SerializeField] private float playerCountSpawnMultiplier = 0.75f;
    [SerializeField] private bool waitForPlayersLoaded = true;

    private List<EnemyEntry> _normalEnemies = new();
    private List<EnemyEntry> _eliteEnemies = new();
    private List<EnemyEntry> _minibossEnemies = new();
    private List<EnemyEntry> _bossEnemies = new();

    private List<EnemyEntry> _currentSet = new();
    private List<EnemyEntry> _previousSet = new();

    private float _cycleTimer;
    private float _spawnTimer;
    private int _cycleCount = 0;
    private int _elitesSpawnedThisCycle = 0;
    private float _currentEliteChance;
    private int _currentEliteCap;
    private int _playerCount;

    private bool _bossAlive = false;
    private EnemyBase _activeBoss;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
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
            }
        }

        RollNewSet();

        if (!waitForPlayersLoaded)
            StartCoroutine(DelayedStart());
    }

    private IEnumerator DelayedStart()
    {
        yield return new WaitForSeconds(0.5f);
        StartSpawning();
    }

    public void StartSpawning()
    {
        _playerCount = FindObjectsByType<CharacterStats>(FindObjectsSortMode.None).Count();
        _currentEliteChance = baseEliteChance;
        _currentEliteCap = baseEliteCap * _playerCount;
        _cycleTimer = cycleInterval;
        StartCoroutine(SpawnLoop());
        StartCoroutine(CycleLoop());
    }

    private IEnumerator SpawnLoop()
    {
        while (true)
        {
            if (!_bossAlive)
            {
                // Try elite spawn
                if (_eliteEnemies.Count > 0 && _elitesSpawnedThisCycle < _currentEliteCap
                    && Random.value < _currentEliteChance)
                {
                    SpawnEnemy(GetWeightedRandom(_eliteEnemies));
                    _elitesSpawnedThisCycle++;
                }
                else if (_currentSet.Count > 0)
                {
                    SpawnEnemy(GetWeightedRandom(_currentSet));
                }
            }

            float interval = spawnInterval / (1f + (_playerCount - 1) * playerCountSpawnMultiplier);
            yield return new WaitForSeconds(interval);
        }
    }

    private IEnumerator CycleLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(spawnInterval * 0.1f); // tick frequently

            if (_bossAlive) continue;

            _cycleTimer -= spawnInterval * 0.1f;
            if (_cycleTimer > 0f) continue;

            _cycleTimer = cycleInterval;
            _cycleCount++;
            _elitesSpawnedThisCycle = 0;

            // Increase elite chance and cap
            _currentEliteChance += baseEliteChance * 0.5f;
            _currentEliteCap = (baseEliteCap + _cycleCount) * _playerCount;

            // Spawn miniboss or boss
            if (_cycleCount % 4 == 0 && _bossEnemies.Count > 0)
                SpawnBossEnemy(GetWeightedRandom(_bossEnemies));
            else if (_minibossEnemies.Count > 0)
                SpawnBossEnemy(GetWeightedRandom(_minibossEnemies));

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
            int idx = Random.Range(0, pool.Count);
            newSet.Add(pool[idx]);
            pool.RemoveAt(idx);
        }
        
        Debug.Log($"New set: {string.Join(", ", newSet.Select(e => e.prefab.name))}");

        // Never same set twice - with only 4 enemies, swap one if all 3 match
        if (_previousSet.Count == 3 && newSet.Count == 3)
        {
            bool allSame = !newSet.Except(_previousSet).Any();
            if (allSame)
            {
                // Find the one enemy not in the current set and swap it in
                var excluded = _normalEnemies.Except(newSet).FirstOrDefault();
                if (excluded != null)
                    newSet[Random.Range(0, newSet.Count)] = excluded;
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

    private void SpawnBossEnemy(EnemyEntry entry)
    {
        _bossAlive = true;
        var obj = Instantiate(entry.prefab, GetSpawnPosition(), Quaternion.identity);
        _activeBoss = obj.GetComponent<EnemyBase>();
        StartCoroutine(WatchBoss());
    }

    private IEnumerator WatchBoss()
    {
        while (_activeBoss != null && !_activeBoss._isDead)
            yield return new WaitForSeconds(0.5f);
        _bossAlive = false;
        _activeBoss = null;
    }

    private EnemyEntry GetWeightedRandom(List<EnemyEntry> entries)
    {
        float total = entries.Sum(e => e.weight);
        float roll = Random.Range(0f, total);
        float cumulative = 0f;
        foreach (var entry in entries)
        {
            cumulative += entry.weight;
            if (roll <= cumulative) return entry;
        }
        return entries[^1];
    }

    public void SpawnEnemy(EnemyEntry entry)
    {
        Instantiate(entry.prefab, GetSpawnPosition(), Quaternion.identity);
    }

    private Vector3 GetSpawnPosition()
    {
        var players = FindObjectsByType<CharacterStats>(FindObjectsSortMode.None)
            .Where(p => !p.IsDead).ToList();
        
        if (players.Count == 0) return Vector3.zero;

        Vector3 pos;
        int attempts = 0;

        do
        {
            var randomPlayer = players[Random.Range(0, players.Count)];
            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float distance = Random.Range(spawnMinDistance, spawnMaxDistance);
            pos = randomPlayer.transform.position + new Vector3(Mathf.Cos(angle) * distance, 0, Mathf.Sin(angle) * distance);
            attempts++;
        }
        while (!IsFarEnoughFromPlayers(pos) && attempts < 30);

        return pos;
    }

    private bool IsFarEnoughFromPlayers(Vector3 pos)
    {
        var players = FindObjectsByType<CharacterStats>(FindObjectsSortMode.None);
        foreach (var player in players)
        {
            if (player.IsDead) continue;
            if (Vector3.Distance(pos, player.transform.position) < spawnMinDistance)
                return false;
        }
        return true;
    }
}