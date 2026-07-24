using System.Collections.Generic;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class EnemySpawnerPD : PredictedIdentity<EnemySpawnerPD.State>
{
    public static EnemySpawnerPD instance;

    [System.Serializable]
    public struct TierEnemies
    {
        public GameObject[] normal;
        public GameObject[] elite;
        public GameObject[] boss;
        public GameObject[] pleb;
    }

    public struct State : IPredictedData<State>
    {
        public bool isActive;
        public float spawnInterval;
        public float spawnTimer;
        public float plebSpawnInterval;
        public float plebSpawnTimer;
        public float cycleTimer;
        public int spawnPhase;
        public int nextEliteIndex;
        public int bossesDefeated;
        // Indices into the current tier's normal[] — GameObject references can't live in predicted State,
        // so the "rolled set" old EnemySpawner tracked as List<GameObject> is tracked by index here.
        public DisposableList<int> currentSetIndices;
        public DisposableList<int> previousSetIndices;
        // Boss tracking: deliberately a list, not a single reference — this is what lets a future dual-
        // boss fight (or any N-simultaneous-boss design) work without redesigning anything, since "a boss
        // is alive" just means this list is non-empty.
        public DisposableList<PredictedObjectID> activeBosses;

        public void Dispose()
        {
            currentSetIndices.Dispose();
            previousSetIndices.Dispose();
            activeBosses.Dispose();
        }
    }

    [Header("Tiers")]
    [SerializeField] private TierEnemies tier1;
    [SerializeField] private TierEnemies tier2;
    [SerializeField] private TierEnemies tier3;
    [SerializeField] private TierEnemies tier4;

    [Header("Spawn Points")]
    // Falls back to a ring around a random living player when fewer than 2 points are assigned.
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private float spawnMinDistance = 25f;
    [SerializeField] private float spawnMaxDistance = 40f;

    [Header("Spawn Scaling")]
    [SerializeField] private float spawnIntervalQ1 = 6f;
    [SerializeField] private float spawnIntervalQ2 = 3f;
    [SerializeField] private float spawnIntervalQ3 = 1.5f;
    [SerializeField] private float spawnIntervalQ4 = 0.75f;
    [SerializeField] private float cycleInterval = 240f;

    [Header("Elite Scaling")]
    // First/last elite of the cycle fire at these cycleTimer values (seconds remaining, matching how
    // cycleTimer already counts down) — the rest are auto-scheduled between them, see BuildEliteSchedule().
    [SerializeField] private float eliteSpawnTimeFirst = 150f;
    [SerializeField] private float eliteSpawnTimeLast = 30f;
    // Each gap between consecutive elite spawns shrinks by this ratio from the previous gap (0.8 = each
    // gap 80% the size of the one before it), so elites arrive more and more frequently as the cycle
    // approaches its boss spawn.
    [SerializeField] private float eliteSpawnRampRatio = 0.8f;
    // Total elites spawned across the whole cycle (not per-burst — deliberately NOT player-count-scaled;
    // see GameManagerPD.CoopToughnessScalar below for how co-op difficulty is handled instead).
    [SerializeField] private int baseEliteCount = 10;

    [SerializeField] private int bossesToDefeatForWin = 1;
    [SerializeField] private int maxNormalEnemies = 100;
    [SerializeField] private int maxEliteEnemies = 100;
    [SerializeField] private int maxPlebEnemies = 300;
    [SerializeField] private int plebSpawnAmount = 5;
    [SerializeField] private float plebSpawnIntervalQ1 = 1f;
    [SerializeField] private float plebSpawnIntervalQ2 = 0.75f;
    [SerializeField] private float plebSpawnIntervalQ3 = 0.5f;
    [SerializeField] private float plebSpawnIntervalQ4 = 0.25f;

    // Reused scratch buffer for RollNewSet()'s pool-and-remove pick — avoids a per-call allocation.
    private readonly List<int> _pool = new();

    // Computed once from eliteSpawnTimeFirst/Last/RampRatio/baseEliteCount — pure config-derived math, no
    // RNG or per-client state involved, so it's identical on every machine without needing to live in
    // predicted State (same category as spawnIntervalQ1 etc. being plain fields read directly in Simulate()).
    private float[] _eliteSchedule;

    // The most robust of the several reset points (see EnemyBasePD.ResetActiveCounts()) — fires exactly
    // once per Game scene load, on every machine, with zero network dependency and before any enemy could
    // possibly exist yet, so it can't ever zero out a real in-progress count.
    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;

        EnemyBasePD.ResetActiveCounts();
        BuildEliteSchedule();
    }

    // Predicted, not verified — matches the XP bar and other continuously-updating numeric displays
    // elsewhere in LocalUIManagerPD, none of which need verified-state's extra network-round-trip lag to
    // avoid a visible flicker (that concern is specific to discrete boolean transitions like isPaused).
    public float CycleTimer => currentState.cycleTimer;

    // baseEliteCount points spread between eliteSpawnTimeFirst and eliteSpawnTimeLast (both measured in
    // cycleTimer-remaining seconds, so "first" > "last" since cycleTimer counts down), with the gaps
    // between consecutive points shrinking geometrically by eliteSpawnRampRatio each step — elites arrive
    // increasingly often as the cycle approaches its boss spawn instead of at fixed evenly-spaced marks.
    private void BuildEliteSchedule()
    {
        int n = Mathf.Max(1, baseEliteCount);
        _eliteSchedule = new float[n];
        if (n == 1) { _eliteSchedule[0] = eliteSpawnTimeLast; return; }

        int gapCount = n - 1;
        float ratioSum = 0f;
        float r = 1f;
        for (int i = 0; i < gapCount; i++)
        {
            ratioSum += r;
            r *= eliteSpawnRampRatio;
        }

        float totalSpan = eliteSpawnTimeFirst - eliteSpawnTimeLast;
        float firstGap = ratioSum > 0f ? totalSpan / ratioSum : 0f;

        _eliteSchedule[0] = eliteSpawnTimeFirst;
        float gap = firstGap;
        for (int i = 1; i < n; i++)
        {
            _eliteSchedule[i] = _eliteSchedule[i - 1] - gap;
            gap *= eliteSpawnRampRatio;
        }
        // Force the exact configured endpoint rather than whatever the accumulated geometric sum lands on
        // — floating point drift over several steps would otherwise leave it a hair off eliteSpawnTimeLast.
        _eliteSchedule[n - 1] = eliteSpawnTimeLast;
    }

    protected override State GetInitialState() => new()
    {
        spawnInterval = spawnIntervalQ1,
        // 0, not spawnIntervalQ1/plebSpawnIntervalQ1 — Simulate() early-returns entirely while
        // !state.isActive, so these sit untouched at 0 until GameManagerPD confirms GameStarted. The very
        // first active tick's countdown then immediately finds spawnTimer/plebSpawnTimer <= 0f, triggering
        // the first normal enemy + pleb batch right away instead of waiting out a full interval first.
        // Every SUBSEQUENT spawn still waits the full interval as normal, since it gets re-added
        // (state.spawnTimer += state.spawnInterval) after firing.
        spawnTimer = 0f,
        plebSpawnInterval = plebSpawnIntervalQ1,
        plebSpawnTimer = 0f,
        cycleTimer = cycleInterval,
        currentSetIndices = DisposableList<int>.Create(3),
        previousSetIndices = DisposableList<int>.Create(3),
        activeBosses = DisposableList<PredictedObjectID>.Create(2),
    };

    protected override void Simulate(ref State state, float delta)
    {
        if (PausePD.instance != null && PausePD.instance.IsPaused) return;

        if (!state.isActive)
        {
            if (GameManagerPD.instance == null || !GameManagerPD.instance.GameStarted) return;
            state.isActive = true;
            RollNewSet(ref state);
            return;
        }

        if (GameManagerPD.instance != null && (GameManagerPD.instance.HasWon || GameManagerPD.instance.IsGameOver)) return;

        // Fixed at game start, not NetworkManager's live playerCount — see
        // GameManagerPD.State.fixedPlayerCount's own doc comment. A player disconnecting mid-run shouldn't
        // suddenly de-scale spawns/enemy toughness for whoever remains.
        int playerCount = GameManagerPD.instance != null ? GameManagerPD.instance.FixedPlayerCount : 1;
        if (playerCount <= 0) playerCount = 1;
        // Single shared "how much harder is co-op" multiplier — same value GameManagerPD divides earned
        // XP by. Applied here to spawn-rate acceleration and boss/elite health/damage/XP alike, so "more
        // spawns" and "tougher, more valuable enemies" always move together as one number.
        float coopScale = 1f + (playerCount - 1) * (GameManagerPD.instance != null ? GameManagerPD.instance.CoopToughnessScalar : 0f);

        // ── Boss tracking ──────────────────────────────────────────────────
        // Pruned every tick so a defeat this tick is caught immediately, regardless of which of possibly
        // several tracked bosses died.
        bool wasBossAlive = state.activeBosses.Count > 0;
        PruneDeadBosses(ref state);
        bool bossAlive = state.activeBosses.Count > 0;

        if (wasBossAlive && !bossAlive)
        {
            state.bossesDefeated++;
            if (state.bossesDefeated >= bossesToDefeatForWin)
            {
                if (GameManagerPD.instance != null) GameManagerPD.instance.TriggerWin();
                return;
            }
            state.spawnPhase = 0;
            state.nextEliteIndex = 0;
            state.spawnInterval = spawnIntervalQ1;
            state.plebSpawnInterval = plebSpawnIntervalQ1;
            RollNewSet(ref state);
        }

        var tier = GetCurrentTier(state.bossesDefeated);

        // ── Phase escalation ───────────────────────────────────────────────
        // Tightens the interval settings (without cutting short a countdown already in flight — only the
        // NEXT countdown picks up the new interval) and rerolls the active set, at 75%/50%/25% of the
        // cycle — same three thresholds as old EnemySpawner.SpawnLoop().
        if (state.spawnPhase < 1 && state.cycleTimer < cycleInterval * 0.75f)
        {
            state.spawnPhase = 1;
            state.spawnInterval = Mathf.Min(state.spawnInterval, spawnIntervalQ2);
            state.plebSpawnInterval = Mathf.Min(state.plebSpawnInterval, plebSpawnIntervalQ2);
            RollNewSet(ref state);
        }
        if (state.spawnPhase < 2 && state.cycleTimer < cycleInterval * 0.5f)
        {
            state.spawnPhase = 2;
            state.spawnInterval = Mathf.Min(state.spawnInterval, spawnIntervalQ3);
            state.plebSpawnInterval = Mathf.Min(state.plebSpawnInterval, plebSpawnIntervalQ3);
            RollNewSet(ref state);
        }
        if (state.spawnPhase < 3 && state.cycleTimer < cycleInterval * 0.25f)
        {
            state.spawnPhase = 3;
            state.spawnInterval = Mathf.Min(state.spawnInterval, spawnIntervalQ4);
            state.plebSpawnInterval = Mathf.Min(state.plebSpawnInterval, plebSpawnIntervalQ4);
            RollNewSet(ref state);
        }

        bool spawnBlackout = state.spawnPhase == 3 && state.cycleTimer < 15f;

        // ── Normal enemies ─────────────────────────────────────────────────
        if (!bossAlive && !spawnBlackout)
        {
            state.spawnTimer -= delta * coopScale;
            if (state.spawnTimer <= 0f)
            {
                state.spawnTimer += state.spawnInterval;
                if (state.currentSetIndices.Count > 0 && EnemyBasePD.ActiveNormalCount < maxNormalEnemies)
                {
                    int pick = state.currentSetIndices[GameRandom.instance.Next(state.currentSetIndices.Count)];
                    if (tier.normal != null && pick < tier.normal.Length)
                        SpawnEnemy(tier.normal[pick], EnemySpawnCategory.Normal, 1f, 1f, 1f);
                }
            }
        }

        // ── Plebs ──────────────────────────────────────────────────────────
        // Keep spawning through a boss fight (at 0 XP, matching old's noXP) — only the blackout window
        // before a boss arrives stops them.
        if (!spawnBlackout)
        {
            state.plebSpawnTimer -= delta * coopScale;
            if (state.plebSpawnTimer <= 0f)
            {
                state.plebSpawnTimer += state.plebSpawnInterval;
                if (tier.pleb != null && tier.pleb.Length > 0)
                {
                    for (int i = 0; i < plebSpawnAmount; i++)
                    {
                        if (EnemyBasePD.ActivePlebCount >= maxPlebEnemies) break;
                        var prefab = tier.pleb[GameRandom.instance.Next(tier.pleb.Length)];
                        SpawnEnemy(prefab, EnemySpawnCategory.Pleb, 1f, 1f, bossAlive ? 0f : 1f);
                    }
                }
            }
        }

        // ── Elites + cycle countdown ───────────────────────────────────────
        // Cycle timer only advances while no boss is alive — once a boss spawns it resets to
        // cycleInterval and stays there (never decrementing) until the boss dies, which is what stops
        // this from immediately trying to spawn a second boss on top of the first.
        if (!bossAlive)
        {
            state.cycleTimer -= delta;
            while (state.nextEliteIndex < _eliteSchedule.Length && state.cycleTimer <= _eliteSchedule[state.nextEliteIndex])
            {
                SpawnElite(ref state, tier, coopScale);
                state.nextEliteIndex++;
            }
        }

        if (state.cycleTimer > 0f) return;

        state.cycleTimer = cycleInterval;
        state.spawnPhase = 0;
        state.nextEliteIndex = 0;

        if (tier.boss != null && tier.boss.Length > 0)
        {
            var prefab = tier.boss[GameRandom.instance.Next(tier.boss.Length)];
            var id = SpawnEnemy(prefab, EnemySpawnCategory.Boss, coopScale, 1f, coopScale);
            if (id.HasValue) state.activeBosses.Add(id.Value);
        }
    }

    private void PruneDeadBosses(ref State state)
    {
        for (int i = state.activeBosses.Count - 1; i >= 0; i--)
        {
            var go = hierarchy.GetGameObject(state.activeBosses[i]);
            bool alive = go != null && go.TryGetComponent(out IEnemyPD enemy) && !enemy._isDead;
            if (!alive) state.activeBosses.RemoveAt(i);
        }
    }

    // ── UI-facing accessors ───────────────────────────────────────────────
    // Verified (not predicted) — a boss's health bar popping in/out is a discrete, visually jarring
    // transition (unlike a smoothly-changing value), so it uses the same "only react to confirmed state"
    // reasoning as VerifiedIsPresenting/VerifiedGameEnded elsewhere, rather than currentState's XP-bar-style
    // instant reactivity.
    //
    // Copies into the caller's own buffer rather than exposing the underlying list directly —
    // DisposableList<T>'s backing List<T> is pooled and can be reused/disposed as new verified snapshots
    // arrive, so handing out a live reference would be unsafe to hold across frames.
    public void GetVerifiedActiveBosses(List<PredictedObjectID> buffer)
    {
        buffer.Clear();
        if (!verifiedState.HasValue) return;
        var list = verifiedState.Value.activeBosses;
        for (int i = 0; i < list.Count; i++)
            buffer.Add(list[i]);
    }

    // Resolves one id to a live IEnemyPD reference — intended to be called fresh every frame for whichever
    // ids are currently active, never cached across frames. Enemies are pooled and reused for new spawns,
    // so a reference held from an earlier frame could silently end up pointing at a completely different
    // (and differently-identified) enemy.
    public IEnemyPD ResolveBoss(PredictedObjectID id)
    {
        var go = hierarchy.GetGameObject(id);
        return go != null && go.TryGetComponent(out IEnemyPD enemy) ? enemy : null;
    }

    // Spawns exactly one elite per scheduled slot — count is fixed by baseEliteCount regardless of player
    // count (see field comment); co-op difficulty instead comes entirely from scaling each elite's own
    // health/damage/XP by the same shared coopScale the caller already computed, which is what keeps
    // total elite XP output consistent between solo and multiplayer instead of just throwing more bodies
    // at a bigger group.
    private void SpawnElite(ref State state, in TierEnemies tier, float coopScale)
    {
        if (tier.elite == null || tier.elite.Length == 0 || state.currentSetIndices.Count == 0) return;
        if (EnemyBasePD.ActiveEliteCount >= maxEliteEnemies) return;

        // Elite pool mirrors the currently rolled normal set by index (tier.elite[i] is the elite variant
        // of tier.normal[i]) — same pairing convention as old EnemySpawner._currentElitePool, just derived
        // on demand instead of cached, since it's a handful of entries at most.
        int poolCount = 0;
        for (int i = 0; i < state.currentSetIndices.Count; i++)
        {
            int idx = state.currentSetIndices[i];
            if (idx < tier.elite.Length && tier.elite[idx] != null) poolCount++;
        }
        if (poolCount == 0) return;

        int skip = GameRandom.instance.Next(poolCount);
        for (int j = 0; j < state.currentSetIndices.Count; j++)
        {
            int idx = state.currentSetIndices[j];
            if (idx >= tier.elite.Length || tier.elite[idx] == null) continue;
            if (skip == 0)
            {
                SpawnEnemy(tier.elite[idx], EnemySpawnCategory.Elite, coopScale, coopScale, coopScale);
                return;
            }
            skip--;
        }
    }

    private void RollNewSet(ref State state)
    {
        var tier = GetCurrentTier(state.bossesDefeated);
        if (tier.normal == null || tier.normal.Length == 0) return;

        int pickCount = Mathf.Min(3, tier.normal.Length);

        // Prefer an exclusive pick (no repeats from last cycle) when there are enough fresh indices to
        // fill pickCount; otherwise fall back to picking from the full pool — same two-step shape as old
        // EnemySpawner.RollNewSet(), just index-based (State can't hold GameObject references) and
        // without LINQ.
        _pool.Clear();
        for (int i = 0; i < tier.normal.Length; i++)
            if (!state.previousSetIndices.Contains(i)) _pool.Add(i);

        if (_pool.Count < pickCount)
        {
            _pool.Clear();
            for (int i = 0; i < tier.normal.Length; i++) _pool.Add(i);
        }

        state.currentSetIndices.Clear();
        for (int i = 0; i < pickCount; i++)
        {
            int roll = GameRandom.instance.Next(_pool.Count);
            state.currentSetIndices.Add(_pool[roll]);
            _pool[roll] = _pool[_pool.Count - 1];
            _pool.RemoveAt(_pool.Count - 1);
        }

        state.previousSetIndices.Clear();
        for (int i = 0; i < state.currentSetIndices.Count; i++)
            state.previousSetIndices.Add(state.currentSetIndices[i]);
    }

    private TierEnemies GetCurrentTier(int bossesDefeated) => bossesDefeated switch
    {
        0 => tier1,
        1 => tier2,
        2 => tier3,
        _ => tier4
    };

    private PredictedObjectID? SpawnEnemy(GameObject prefab, EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier)
    {
        if (prefab == null) return null;
        var id = hierarchy.Create(prefab, GetSpawnPosition(), Quaternion.identity);
        if (!id.HasValue) return null;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out IEnemyPD enemy))
            enemy.ConfigureSpawn(category, healthMultiplier, damageMultiplier, xpMultiplier);

        return id;
    }

    private Vector3 GetSpawnPosition()
    {
        if (spawnPoints != null && spawnPoints.Length >= 2)
        {
            int i = GameRandom.instance.Next(spawnPoints.Length);
            int j = (i + 1) % spawnPoints.Length;
            return Vector3.Lerp(spawnPoints[i].position, spawnPoints[j].position, GameRandom.instance.NextFloat(0f, 1f));
        }

        // Fallback: ring around a random living player, retried until far enough from every player. Reads
        // CharacterStatsPD.All directly — safe from Simulate() the same way EnemyBasePD.FindClosestTarget() reads
        // it, since we're only reading each entry's own predicted currentState, not relying on the
        // dictionary's population timing.
        int aliveCount = 0;
        foreach (var p in CharacterStatsPD.All.Values)
            if (!p.IsDead) aliveCount++;
        if (aliveCount == 0) return transform.position;

        Vector3 pos = transform.position;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var anchor = GetNthAlivePlayer(GameRandom.instance.Next(aliveCount));
            if (anchor == null) continue;

            float angle = GameRandom.instance.NextFloat(0f, 360f) * Mathf.Deg2Rad;
            float distance = GameRandom.instance.NextFloat(spawnMinDistance, spawnMaxDistance);
            pos = anchor.position + new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
            if (IsFarEnoughFromPlayers(pos)) return pos;
        }
        return pos;
    }

    private Transform GetNthAlivePlayer(int n)
    {
        int seen = 0;
        foreach (var p in CharacterStatsPD.All.Values)
        {
            if (p.IsDead) continue;
            if (seen == n) return p.transform;
            seen++;
        }
        return null;
    }

    private bool IsFarEnoughFromPlayers(Vector3 pos)
    {
        foreach (var p in CharacterStatsPD.All.Values)
            if (Vector3.Distance(pos, p.transform.position) < spawnMinDistance) return false;
        return true;
    }
}
