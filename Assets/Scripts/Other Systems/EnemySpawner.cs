using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using PurrNet;
using UnityEngine;

[System.Serializable]
public class TierEnemies
{
    public List<GameObject> normal = new();
    public List<GameObject> elite = new();
    public List<GameObject> miniboss = new();
    public List<GameObject> boss = new();
    public List<GameObject> pleb = new();
}

public class EnemySpawner : NetworkIdentity
{
    private static readonly WaitForSeconds _waitForSeconds0_5 = new(0.5f);
    public static EnemySpawner instance;

    [SerializeField] private TierEnemies tier1;
    [SerializeField] private TierEnemies tier2;
    [SerializeField] private TierEnemies tier3;
    [SerializeField] private TierEnemies tier4;

    [SerializeField] private float spawnMinDistance = 25f;
    [SerializeField] private float spawnMaxDistance = 40f;
    [SerializeField] private float baseSpawnInterval = 1f;
    [SerializeField] private float cycleInterval = 150f;
    [SerializeField] private float baseEliteChance = 0.1f;
    [SerializeField] private int baseEliteCap = 2;
    public float coopSpawnTimeScalar = 0.5f;
    [SerializeField] private float coopBossHealthScalar = 0.5f;
    [SerializeField] private int maxNormalEnemies = 100;
    [SerializeField] private int maxPlebEnemies = 300;
    [SerializeField] private int plebSpawnAmount = 5;
    [SerializeField] private float plebSpawnInterval = 1f;

    private List<GameObject> _currentSet = new();
    private List<GameObject> _previousSet = new();
    private EnemyBase _activeBoss;

    [NonSerialized] public SyncVar<float> _cycleTimer = new(0f);
    [HideInInspector] public float CycleTimer
    {
        get => _cycleTimer.value;
        set => _cycleTimer.value = value;
    }
    private int _cycleCount = 0;
    private int _elitesSpawnedThisCycle = 0;
    private int _currentSpawnMultiplier = 1;
    private float _currentEliteChance;
    private int _currentEliteCap;
    private int _playerCount;
    [SerializeField] private bool waitForPlayersLoaded = true;

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
        EnemyBase.ActiveNormalCount = 0;
        EnemyBase.ActivePlebCount = 0;
        _playerCount = networkManager.playerCount;
        _currentEliteChance = baseEliteChance;
        _currentEliteCap = baseEliteCap * _playerCount;
        CycleTimer = cycleInterval;
        StartCoroutine(SpawnLoop());
        StartCoroutine(CycleLoop());
        if (tier1.pleb.Count > 0)
            StartCoroutine(PlebSpawnLoop());
    }

    private IEnumerator SpawnLoop()
    {
        while (true)
        {
            if (!IsBossAlive())
            {
                for (int i = 0; i < _currentSpawnMultiplier; i++)
                {
                    if (tier1.elite.Count > 0 && _elitesSpawnedThisCycle < _currentEliteCap
                        && UnityEngine.Random.value < _currentEliteChance)
                    {
                        SpawnThisEnemy(GetRandom(tier1.elite), false, isUncapped: true);
                        _elitesSpawnedThisCycle++;
                    }
                    else if (_currentSet.Count > 0 && EnemyBase.ActiveNormalCount < maxNormalEnemies)
                    {
                        SpawnThisEnemy(GetRandom(_currentSet), false);
                    }
                }
            }

            yield return new WaitForSeconds(GetSpawnInterval());
        }
    }

    private IEnumerator PlebSpawnLoop()
    {
        float interval = plebSpawnInterval / (1f + (_playerCount - 1) * coopSpawnTimeScalar);
        while (true)
        {
            bool bossAlive = IsBossAlive();
            int mult = bossAlive ? (_cycleCount % 2 == 0 ? 2 : 1) : _currentSpawnMultiplier;
            int count = plebSpawnAmount * mult;
            for (int i = 0; i < count; i++)
            {
                if (EnemyBase.ActivePlebCount < maxPlebEnemies)
                    SpawnThisEnemy(GetRandom(tier1.pleb), false, noXP: bossAlive, isPleb: true);
            }
            yield return new WaitForSeconds(interval);
        }
    }

    private IEnumerator CycleLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(baseSpawnInterval * 0.1f);

            if (!IsBossAlive())
                CycleTimer -= baseSpawnInterval * 0.1f;
            if (CycleTimer > 0f) continue;

            CycleTimer = cycleInterval;
            _cycleCount++;
            _elitesSpawnedThisCycle = 0;

            _currentEliteChance += baseEliteChance * 0.5f;
            _currentEliteCap = (baseEliteCap + _cycleCount) * _playerCount;
            _currentSpawnMultiplier = _cycleCount % 2 != 0 ? 2 : 1;

            if (_cycleCount % 2 == 0 && tier1.boss.Count > 0)
                SpawnThisEnemy(GetRandom(tier1.boss), true, true);
            else if (tier1.miniboss.Count > 0)
                SpawnThisEnemy(GetRandom(tier1.miniboss), true, false);

            RollNewSet();
        }
    }

    private void RollNewSet()
    {
        if (tier1.normal.Count == 0) return;

        List<GameObject> pool = new(tier1.normal);
        List<GameObject> newSet = new();

        int pickCount = Mathf.Min(3, pool.Count);
        for (int i = 0; i < pickCount; i++)
        {
            int idx = UnityEngine.Random.Range(0, pool.Count);
            newSet.Add(pool[idx]);
            pool.RemoveAt(idx);
        }

        if (_previousSet.Count == 3 && newSet.Count == 3)
        {
            bool allSame = !newSet.Except(_previousSet).Any();
            if (allSame)
            {
                var excluded = tier1.normal.Except(newSet).FirstOrDefault();
                if (excluded != null)
                    newSet[UnityEngine.Random.Range(0, newSet.Count)] = excluded;
            }

            /* TODO: When more than 4 enemies are available, use this instead:
            var availablePool = tier1.normal.Except(_previousSet).ToList();
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

        _previousSet = new List<GameObject>(_currentSet);
        _currentSet = newSet;
    }

    private bool IsBossAlive() => _activeBoss != null && !_activeBoss._isDead;

    private float GetSpawnInterval()
    {
        float coopScale = 1f + (_playerCount - 1) * coopSpawnTimeScalar;
        bool isFirstCycleType = _cycleCount == 0 || _cycleCount % 2 != 0;
        bool isSecondHalf = CycleTimer < cycleInterval * 0.5f;
        float multiplier = isFirstCycleType ? (isSecondHalf ? 1.5f : 1f) : (isSecondHalf ? 2.5f : 2f);
        return baseSpawnInterval / multiplier / coopScale;
    }

    private void SpawnThisEnemy(GameObject prefab, bool isBoss, bool isFinalBoss = false, bool noXP = false, bool isPleb = false, bool isUncapped = false)
    {
        if (prefab == null) return;
        var obj = Instantiate(prefab, GetSpawnPosition(), Quaternion.identity);
        var enemy = obj.GetComponent<EnemyBase>();
        enemy.isFinalBoss = isFinalBoss;
        enemy.isPleb = isPleb;
        enemy.isUncapped = isUncapped || isBoss;
        if (noXP) enemy.xpValue = 0f;
        if (isBoss) _activeBoss = enemy;
        if (isBoss)
        {
            enemy.maxHealth *= 1f + (_playerCount - 1) * coopBossHealthScalar;
            enemy.xpValue *= 1f + (_playerCount - 1) * coopSpawnTimeScalar;
        }
    }

    private GameObject GetRandom(List<GameObject> entries)
    {
        return entries[UnityEngine.Random.Range(0, entries.Count)];
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
