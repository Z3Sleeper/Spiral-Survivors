using System.Collections.Generic;
using PurrNet.Packing;
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

    public struct State : IPredictedData<State>, IDuplicate<State>
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

        // Was missing entirely — every rollback snapshot fell back to the slow serialize/deserialize
        // round-trip instead of this cheap direct field-copy. See EnemyBaseState.Duplicate's own doc
        // comment for the full reasoning.
        public State Duplicate()
        {
            var copy = this;
            copy.currentSetIndices = currentSetIndices.Duplicate();
            copy.previousSetIndices = previousSetIndices.Duplicate();
            copy.activeBosses = activeBosses.Duplicate();
            return copy;
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
    [SerializeField] private float cycleInterval = 180f;

    [Header("Elite Scaling")]
    // First/last elite of the cycle fire this far INTO the cycle, as a percent of cycleInterval (0.25 =
    // 25% elapsed, i.e. 75% of cycleInterval still remaining on cycleTimer) — the rest are auto-scheduled
    // between them, see BuildEliteSchedule(). Percent-based (not raw seconds, like the old
    // eliteSpawnTimeFirst/Last fields these replaced) specifically so retuning cycleInterval alone doesn't
    // also require re-deriving these by hand — they scale automatically with whatever cycleInterval is set
    // to. BuildEliteSchedule() converts these to actual cycleTimer (remaining-seconds) thresholds once, at
    // Awake() — see its own comment.
    [SerializeField] private float eliteSpawnTimeFirstPercent = 0.25f;
    [SerializeField] private float eliteSpawnTimeLastPercent = 0.75f;
    // Each gap between consecutive elite spawns shrinks by this ratio from the previous gap (0.8 = each
    // gap 80% the size of the one before it), so elites arrive more and more frequently as the cycle
    // approaches its boss spawn.
    [SerializeField] private float eliteSpawnRampRatio = 0.8f;
    // Total elites spawned across the whole cycle (not per-burst — deliberately NOT player-count-scaled;
    // see GameManagerPD.CoopToughnessScalar below for how co-op difficulty is handled instead).
    [SerializeField] private int baseEliteCount = 10;

    [Header("Tier Stat Scaling")]
    // Applied at spawn time, generically, to every category (normal/pleb/elite/boss) on top of whatever
    // that category's own elite/co-op multiplier already is (see Simulate()'s own comment on ordering) — so
    // an enemy TYPE only ever needs ONE authored (tier1-baseline) stat set on its prefab. Reusing the same
    // prefabs across a different "level" with a different tier progression means retuning these 3 growth
    // curves once here, not re-tuning every individual tier's own hand-authored numbers on every prefab.
    // Move speed and damage grow LINEARLY (tier1 + this fraction of tier1 per tier step above 1); health and
    // XP each grow on their OWN independent EXPONENTIAL curve (this many multiplier steps compounded per
    // tier step above 1, separately tunable so one can be retuned without affecting the other) — shape
    // confirmed against the project's own current T1/T2/T3 numbers before this was built.
    [SerializeField] private float moveSpeedTierGrowth = 0.25f;
    [SerializeField] private float damageTierGrowth = 0.5f;
    [SerializeField] private float healthExponentialTierBase = 5f;
    [SerializeField] private float xpExponentialTierBase = 5f;

    // Cached once per Simulate() tick (see that method's own comment for exactly where) — plain fields, not
    // State, since they're fully re-derived from state.bossesDefeated every tick regardless, same "compute
    // once at the top, read by same-tick logic further down" convention _currentDelta already establishes
    // elsewhere in this codebase. SpawnEnemy is the only reader, and only ever runs from within this same
    // Simulate() call (directly or via SpawnElite), so these are always fresh by the time it's called.
    private float _tierMoveSpeedMultiplier = 1f;
    private float _tierDamageMultiplier = 1f;
    private float _tierHealthMultiplier = 1f;
    private float _tierXpMultiplier = 1f;

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

    // ── Enemy-targeting hub (taunt/invisible immediate reaction) ────────────────
    // Every currently-alive enemy registers itself here (LateAwake) and unregisters on death/despawn
    // (Destroyed) — a plain HashSet add/remove, not an event subscription, so enemies never need to know
    // about players at all. Subscribing to up to 4 players' own taunt/invisible PredictedEvents here once,
    // then fanning out to this set directly, means the fan-out runs as plain synchronous C# inside that
    // ALREADY-verified player-side event callback — it inherits that event's resimulation-safety for free,
    // no extra PredictedEvent layer needed on the enemy side. See project memory for the full reasoning.
    private readonly HashSet<IEnemyPD> _activeEnemies = new();
    private readonly HashSet<CharacterStatsPD> _subscribedPlayers = new();

    public void RegisterEnemy(IEnemyPD enemy) => _activeEnemies.Add(enemy);
    public void UnregisterEnemy(IEnemyPD enemy) => _activeEnemies.Remove(enemy);

    // Read-only enumeration for callers that need to scan currently-alive enemies (e.g. OrbiterGloomPD finding
    // nearby Pleb-tier enemies to grab) — IEnumerable, not a collection type, since every known caller only
    // ever foreach's it.
    public IEnumerable<IEnemyPD> ActiveEnemies => _activeEnemies;

    // Players don't exist yet in CharacterStatsPD.All at this object's own LateAwake() (they register from
    // PlayerDataPD.Simulate(), which only starts running once actual per-tick simulation begins) — so this
    // is checked every Simulate() tick instead, cheap since CharacterStatsPD.All is capped at 4 players and
    // _subscribedPlayers.Add() short-circuits (skipping the listener-registration work) for anyone already
    // subscribed. Once subscribed, a player's own two PredictedEvents call back into these forever, so this
    // never re-subscribes the same player twice.
    private void SubscribeNewPlayers()
    {
        foreach (var player in CharacterStatsPD.All.Values)
        {
            if (!_subscribedPlayers.Add(player)) continue;
            var p = player;
            p.AddTauntChangedListener(isTaunting => OnPlayerTauntChanged(p, isTaunting));
            p.AddInvisibleChangedListener(isInvisible => OnPlayerInvisibleChanged(p, isInvisible));
        }
    }

    private void OnPlayerTauntChanged(CharacterStatsPD player, bool isTaunting)
    {
        foreach (var enemy in _activeEnemies) enemy.OnPlayerTauntChanged(player, isTaunting);
    }

    private void OnPlayerInvisibleChanged(CharacterStatsPD player, bool isInvisible)
    {
        foreach (var enemy in _activeEnemies) enemy.OnPlayerInvisibleChanged(player, isInvisible);
    }

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

    // baseEliteCount points spread between eliteSpawnTimeFirstPercent and eliteSpawnTimeLastPercent, with
    // the gaps between consecutive points shrinking geometrically by eliteSpawnRampRatio each step —
    // elites arrive increasingly often as the cycle approaches its boss spawn instead of at fixed
    // evenly-spaced marks. The two percent fields are converted to actual cycleTimer-remaining-seconds
    // thresholds right here, once, since cycleInterval is fixed for the whole match — everything below this
    // conversion is unchanged from before eliteSpawnTimeFirst/Last became percent-based, still working
    // entirely in remaining-seconds terms (firstRemaining > lastRemaining, since cycleTimer counts down).
    private void BuildEliteSchedule()
    {
        float firstRemaining = cycleInterval * (1f - eliteSpawnTimeFirstPercent);
        float lastRemaining  = cycleInterval * (1f - eliteSpawnTimeLastPercent);

        int n = Mathf.Max(1, baseEliteCount);
        _eliteSchedule = new float[n];
        if (n == 1) { _eliteSchedule[0] = lastRemaining; return; }

        int gapCount = n - 1;
        float ratioSum = 0f;
        float r = 1f;
        for (int i = 0; i < gapCount; i++)
        {
            ratioSum += r;
            r *= eliteSpawnRampRatio;
        }

        float totalSpan = firstRemaining - lastRemaining;
        float firstGap = ratioSum > 0f ? totalSpan / ratioSum : 0f;

        _eliteSchedule[0] = firstRemaining;
        float gap = firstGap;
        for (int i = 1; i < n; i++)
        {
            _eliteSchedule[i] = _eliteSchedule[i - 1] - gap;
            gap *= eliteSpawnRampRatio;
        }
        // Force the exact configured endpoint rather than whatever the accumulated geometric sum lands on
        // — floating point drift over several steps would otherwise leave it a hair off lastRemaining.
        _eliteSchedule[n - 1] = lastRemaining;
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
        SubscribeNewPlayers();

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
            // Same clean-arena wipe as right before a boss spawns (see its own call site's doc comment) —
            // clears out whatever pleb/normal/elite swarm accumulated during the boss fight (which keep
            // spawning through it, at 0 XP — see the pleb comment above) the instant the boss itself falls,
            // rather than leaving them to wander around XP-less after the fact. Fires on every boss defeat,
            // win-triggering or not, so the win screen doesn't have a stray swarm still visible either.
            WipeAllEnemies();
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

        // Recomputed fresh every tick from state.bossesDefeated — see these 3 fields' own doc comment for
        // why this is the one place that needs to run (SpawnEnemy, the only reader, only ever gets called
        // later in this same Simulate() call, directly or via SpawnElite).
        int tierNumber = GetCurrentTierNumber(state.bossesDefeated);
        _tierMoveSpeedMultiplier = 1f + (tierNumber - 1) * moveSpeedTierGrowth;
        _tierDamageMultiplier    = 1f + (tierNumber - 1) * damageTierGrowth;
        _tierHealthMultiplier    = Mathf.Pow(healthExponentialTierBase, tierNumber - 1);
        _tierXpMultiplier        = Mathf.Pow(xpExponentialTierBase, tierNumber - 1);

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

        // ── Normal enemies ─────────────────────────────────────────────────
        if (!bossAlive)
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
        // Keep spawning through a boss fight (at 0 XP, matching old's noXP), and — since the pre-boss
        // spawn blackout was removed — right up until the boss actually spawns too. Nothing left alive
        // when it does spawn either way, since every currently-alive enemy (plebs included) gets wiped
        // right before that happens below.
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
            // Wipe every currently-alive enemy right before the boss lands — see WipeAllEnemies' own doc
            // comment. Now that the pre-boss spawn blackout is gone, enemies keep spawning right up until
            // this exact moment, so without this the boss would land in the middle of a full, ever-growing
            // swarm instead of a clean arena.
            WipeAllEnemies();

            var prefab = tier.boss[GameRandom.instance.Next(tier.boss.Length)];
            var id = SpawnEnemy(prefab, EnemySpawnCategory.Boss, coopScale, 1f, coopScale);
            if (id.HasValue) state.activeBosses.Add(id.Value);
        }
    }

    // Shared by both boss-cycle transitions — right before a boss spawns, and right after one dies (see
    // each call site's own comment for why). See IEnemyPD.SilentDespawn's own doc comment for why this is a
    // clean vanish, not a real kill: no XP, no on-kill augment reactions, no death-flash. _activeEnemies
    // (the same set OnPlayerTauntChanged/OnPlayerInvisibleChanged already fan out through) is every enemy
    // alive right now — safe to include a boss too on the pre-spawn call (should never actually be one
    // there, since bossAlive being false is what let control reach either call site at all), since a
    // SilentDespawn() on an already-dead/already-wiped enemy is a harmless no-op either way.
    private void WipeAllEnemies()
    {
        foreach (var enemy in _activeEnemies)
            enemy.SilentDespawn();
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

    // Mirrors GetCurrentTier's own bossesDefeated->tier mapping exactly (tier1=1, tier2=2, tier3=3, tier4=4+)
    // — the two must never drift apart, since one picks the prefab pool and the other picks how hard
    // whatever comes out of it hits.
    private static int GetCurrentTierNumber(int bossesDefeated) => Mathf.Min(bossesDefeated, 3) + 1;

    // healthMultiplier/damageMultiplier/xpMultiplier arriving here are each category's own existing elite/
    // co-op scalar (e.g. coopScale) — tier scaling multiplies on top of that, not the other way around (per
    // explicit design: tier is the base transformation every category shares, elite/co-op is layered on top
    // of whatever tier already produced). Folded in here, the one choke point every spawn call already goes
    // through, rather than touching each of the 4 call sites above individually.
    private PredictedObjectID? SpawnEnemy(GameObject prefab, EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier)
    {
        if (prefab == null) return null;
        var id = hierarchy.Create(prefab, GetSpawnPosition(), Quaternion.identity);
        if (!id.HasValue) return null;

        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out IEnemyPD enemy))
            enemy.ConfigureSpawn(category, _tierHealthMultiplier * healthMultiplier, _tierDamageMultiplier * damageMultiplier, _tierXpMultiplier * xpMultiplier, _tierMoveSpeedMultiplier);

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
