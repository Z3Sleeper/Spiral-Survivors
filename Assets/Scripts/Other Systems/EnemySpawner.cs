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
    public List<GameObject> boss = new();
    public List<GameObject> pleb = new();
}

public class EnemySpawner : NetworkIdentity
{
    private static readonly WaitForSeconds _waitForSeconds0_1 = new(0.1f);
    private static readonly WaitForSeconds _waitForSeconds0_5 = new(0.5f);
    public static EnemySpawner instance;

    [SerializeField] private TierEnemies tier1;
    [SerializeField] private TierEnemies tier2;
    [SerializeField] private TierEnemies tier3;
    [SerializeField] private TierEnemies tier4;

    [SerializeField] private Transform[] _spawnPoints;
    [SerializeField] private float spawnMinDistance = 25f;
    [SerializeField] private float spawnMaxDistance = 40f;
    [Header("Spawn Scaling")]
    [SerializeField] private float spawnIntervalQ1 = 6f;
    [SerializeField] private float spawnIntervalQ2 = 3f;
    [SerializeField] private float spawnIntervalQ3 = 1.5f;
    [SerializeField] private float spawnIntervalQ4 = 0.75f;
    [SerializeField] private float cycleInterval = 150f;
    [SerializeField] private float eliteChance = 0.05f;
    public enum WinOnBoss { First = 1, Second = 2, Third = 3, Fourth = 4 }
    [SerializeField] private WinOnBoss winOnBoss = WinOnBoss.Second;
    private int _bossesDefeated = 0;
    public float coopSpawnTimeScalar = 0.5f;
    [SerializeField] private float coopBossHealthScalar = 0.5f;
    [SerializeField] private int maxNormalEnemies = 100;
    [SerializeField] private int maxPlebEnemies = 300;
    [SerializeField] private int plebSpawnAmount = 5;
    [SerializeField] private float plebSpawnIntervalQ1 = 1f;
    [SerializeField] private float plebSpawnIntervalQ2 = 0.75f;
    [SerializeField] private float plebSpawnIntervalQ3 = 0.5f;
    [SerializeField] private float plebSpawnIntervalQ4 = 0.25f;

    private List<GameObject> _currentSet = new();
    private List<GameObject> _currentElitePool = new();
    private List<GameObject> _previousSet = new();
    private EnemyBase _activeBoss;

    [NonSerialized] public SyncVar<float> _cycleTimer = new(0f);
    [HideInInspector] public float CycleTimer
    {
        get => _cycleTimer.value;
        set => _cycleTimer.value = value;
    }
    private float _elitePrdChance;
    private int _playerCount;
    private float _currentSpawnInterval = 1f;
    private float _currentPlebSpawnInterval = 1f;
    private int _spawnPhase = 0;
    private bool _bossWasAlive = false;
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

    protected override void OnDespawned(bool asServer)
    {
        base.OnDespawned(asServer);
        StopAllCoroutines();
    }

    protected override void OnDestroy()
    {
        StopAllCoroutines();
        base.OnDestroy();
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
        _elitePrdChance = 0f;
        CycleTimer = cycleInterval;
        _currentSpawnInterval = spawnIntervalQ1;
        _currentPlebSpawnInterval = plebSpawnIntervalQ1;
        _spawnPhase = 0;
        _bossWasAlive = false;
        StartCoroutine(SpawnLoop());
        StartCoroutine(CycleLoop());
        if (tier1.pleb.Count > 0 || tier2.pleb.Count > 0 || tier3.pleb.Count > 0 || tier4.pleb.Count > 0)
            StartCoroutine(PlebSpawnLoop());
    }

    private IEnumerator SpawnLoop()
    {
        while (true)
        {
            float interval = GetSpawnInterval();

            if (_spawnPhase < 1 && CycleTimer < cycleInterval * 0.75f)
            {
                _spawnPhase = 1;
                _currentSpawnInterval = Mathf.Min(_currentSpawnInterval, spawnIntervalQ2);
                _currentPlebSpawnInterval = Mathf.Min(_currentPlebSpawnInterval, plebSpawnIntervalQ2);
                RollNewSet();
            }
            if (_spawnPhase < 2 && CycleTimer < cycleInterval * 0.5f)
            {
                _spawnPhase = 2;
                _currentSpawnInterval = Mathf.Min(_currentSpawnInterval, spawnIntervalQ3);
                _currentPlebSpawnInterval = Mathf.Min(_currentPlebSpawnInterval, plebSpawnIntervalQ3);
                RollNewSet();
            }
            if (_spawnPhase < 3 && CycleTimer < cycleInterval * 0.25f)
            {
                _spawnPhase = 3;
                _currentSpawnInterval = Mathf.Min(_currentSpawnInterval, spawnIntervalQ4);
                _currentPlebSpawnInterval = Mathf.Min(_currentPlebSpawnInterval, plebSpawnIntervalQ4);
                RollNewSet();
            }

            bool bossAlive = IsBossAlive();

            if (!bossAlive)
            {
                bool eliteBlackout = _spawnPhase == 3 && CycleTimer < cycleInterval * 0.125f;
                var tier = GetCurrentTier();
                if (_currentElitePool.Count > 0 && _spawnPhase >= 1 && !eliteBlackout)
                {
                    _elitePrdChance += eliteChance;
                    if (UnityEngine.Random.value < _elitePrdChance)
                    {
                        SpawnThisEnemy(GetRandom(_currentElitePool), false, isUncapped: true);
                        _elitePrdChance = 0f;
                    }
                    else if (_currentSet.Count > 0 && EnemyBase.ActiveNormalCount < maxNormalEnemies)
                    {
                        SpawnThisEnemy(GetRandom(_currentSet), false);
                    }
                }
                else if (_currentSet.Count > 0 && EnemyBase.ActiveNormalCount < maxNormalEnemies)
                {
                    SpawnThisEnemy(GetRandom(_currentSet), false);
                }
            }

            yield return new WaitForSeconds(interval);
        }
    }

    private IEnumerator PlebSpawnLoop()
    {
        float coopScale = 1f + (_playerCount - 1) * coopSpawnTimeScalar;
        while (true)
        {
            bool bossAlive = IsBossAlive();
            int count = plebSpawnAmount;
            for (int i = 0; i < count; i++)
            {
                if (EnemyBase.ActivePlebCount < maxPlebEnemies)
                    SpawnThisEnemy(GetRandom(GetCurrentTier().pleb), false, noXP: bossAlive, isPleb: true);
            }
            yield return new WaitForSeconds(_currentPlebSpawnInterval / coopScale);
        }
    }

    private IEnumerator CycleLoop()
    {
        while (true)
        {
            yield return _waitForSeconds0_1;

            bool bossAlive = IsBossAlive();

            if (_bossWasAlive && !bossAlive)
            {
                _bossesDefeated++;
                if (_bossesDefeated == (int)winOnBoss)
                {
                    GameManager.instance.TriggerWin();
                    yield break;
                }
                _currentSpawnInterval = spawnIntervalQ1;
                _currentPlebSpawnInterval = plebSpawnIntervalQ1;
                _spawnPhase = 0;
                _elitePrdChance = 0f;
                RollNewSet();
            }
            _bossWasAlive = bossAlive;

            if (!bossAlive)
                CycleTimer -= 0.1f;
            if (CycleTimer > 0f) continue;

            CycleTimer = cycleInterval;
            _spawnPhase = 0;

            var bossTier = GetCurrentTier();
            if (bossTier.boss.Count > 0)
                SpawnThisEnemy(GetRandom(bossTier.boss), true);
        }
    }

    private void RollNewSet()
    {
        var currentTier = GetCurrentTier();
        if (currentTier.normal.Count == 0) return;

        // Step 1: pick up to 3 enemies randomly from the full pool (the fallback set)
        List<GameObject> pool = new(currentTier.normal);
        List<GameObject> newSet = new();

        int pickCount = Mathf.Min(3, pool.Count);
        for (int i = 0; i < pickCount; i++)
        {
            int idx = UnityEngine.Random.Range(0, pool.Count);
            newSet.Add(pool[idx]);
            pool.RemoveAt(idx);
        }

        // Step 2: try to replace newSet with a fully exclusive pick (no enemies from last cycle)
        // This only runs if there was a previous cycle to compare against
        if (_previousSet.Count > 0 && newSet.Count > 0)
        {
            var availablePool = currentTier.normal.Except(_previousSet).ToList();

            if (availablePool.Count >= pickCount)
            {
                // Enough fresh enemies exist → overwrite newSet with exclusive picks
                // 6+ enemies: always reaches here
                newSet.Clear();
                for (int i = 0; i < pickCount; i++)
                {
                    int idx = UnityEngine.Random.Range(0, availablePool.Count);
                    newSet.Add(availablePool[idx]);
                    availablePool.RemoveAt(idx);
                }
            }
            // Not enough fresh enemies → keep the random newSet from Step 1
            // 4-5 enemies: some repeats from last cycle are possible
            // 3 or fewer enemies: repetition is unavoidable
        }

        // Step 3: slide the window — current becomes previous, new becomes current
        _previousSet = new List<GameObject>(_currentSet);
        _currentSet = newSet;

        // Build matching elite pool by index
        _currentElitePool.Clear();
        foreach (var normal in _currentSet)
        {
            int idx = currentTier.normal.IndexOf(normal);
            if (idx >= 0 && idx < currentTier.elite.Count && currentTier.elite[idx] != null)
                _currentElitePool.Add(currentTier.elite[idx]);
        }
    }

    private TierEnemies GetCurrentTier() => _bossesDefeated switch
    {
        0 => tier1,
        1 => tier2,
        2 => tier3,
        _ => tier4
    };

    private bool IsBossAlive() => _activeBoss != null && !_activeBoss._isDead;

    private float GetSpawnInterval()
    {
        float coopScale = 1f + (_playerCount - 1) * coopSpawnTimeScalar;
        return _currentSpawnInterval / coopScale;
    }

    private void SpawnThisEnemy(GameObject prefab, bool isBoss, bool noXP = false, bool isPleb = false, bool isUncapped = false)
    {
        if (prefab == null || !isSpawned) return;
        if (networkManager == null || !networkManager.sceneModule.TryGetSceneID(gameObject.scene, out _)) return;
        var obj = Instantiate(prefab, GetSpawnPosition(), Quaternion.identity);
        if (obj == null) return;
        var enemy = obj.GetComponent<EnemyBase>();
        enemy.isPleb = isPleb;
        enemy.isUncapped = isUncapped || isBoss;
        if (noXP) { enemy.xpValue = 0f; enemy.pointValue = 0f; }
        if (isBoss) _activeBoss = enemy;
        if (isBoss)
        {
            enemy.maxHealth *= 1f + (_playerCount - 1) * coopBossHealthScalar;
            enemy.xpValue *= 1f + (_playerCount - 1) * coopSpawnTimeScalar;
            enemy.pointValue *= 1f + (_playerCount - 1) * coopSpawnTimeScalar;
        }
    }

    private GameObject GetRandom(List<GameObject> entries)
    {
        return entries[UnityEngine.Random.Range(0, entries.Count)];
    }

    private Vector3 GetSpawnPosition()
    {
        if (_spawnPoints != null && _spawnPoints.Length > 0)
            return _spawnPoints[UnityEngine.Random.Range(0, _spawnPoints.Length)].position;

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
