using System.Collections.Generic;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Terminology (2026-08-24, established ahead of the upcoming mid-cycle events system): the span of time from
// one tier's enemies starting to spawn through that tier's boss actually dying (spawn time + the boss's own
// alive time, not just spawn time alone) is a "Tier Cycle" — currently 4 of them, one per tier, each ending
// in that tier's own boss fight. The pre-boss PORTION of a Tier Cycle (everything before the boss spawns) is
// itself split into 4 "Quarters" (Quarter 1 through Quarter 4 — see spawnIntervalQ1-Q4/plebSpawnIntervalQ1-Q4
// below, and the "Quarter escalation" block in Simulate()), each one signaling a new "Wave" of enemy types to
// start spawning (see RollNewSet — the currently-rolled 3 enemy types ARE that Quarter's Wave). Quarters/
// Waves do NOT include the boss's own alive time — a Tier Cycle is Quarters 1-4 (four Waves) PLUS the boss
// phase that follows them.
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

    // One shared color identity per tier (Gloom/Doom/Despair/Dread — red/blue/yellow/purple), applied
    // uniformly across every category/enemy type spawned at that tier — not per-category, unlike TierEnemies
    // above. Covers only the accent parts + Invincible Shell; every enemy's black body renderers
    // (EnemyBasePD._renderers) are deliberately NEVER touched by this, they stay black at every tier and are
    // only ever flash/stun-tinted. Scaffolding for consolidating every enemy type down to a single
    // tier1-authored prefab that gets reused at every tier (see EnemyBasePD.ApplyTierMaterials) instead of
    // each tier needing its own hand-authored, separately-colored prefab copy. Either field left null on a
    // tier is a no-op for that slot (ApplyTierMaterials leaves the renderer's existing material alone rather
    // than assigning null) — lets this be filled in incrementally, tier by tier, material by material.
    [System.Serializable]
    public struct TierMaterials
    {
        public Material accentMaterial;
        public Material shellMaterial;
    }

    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public bool isActive;
        public float spawnInterval;
        public float spawnTimer;
        public float plebSpawnInterval;
        public float plebSpawnTimer;
        // Counts down across a whole Tier Cycle (see this file's own class-level "Tier Cycle"/"Quarter"/
        // "Wave" terminology comment) — resets to cycleInterval the moment a new Tier Cycle begins (a boss
        // spawning), and pauses (stops decrementing) entirely while that boss stays alive, matching a Tier
        // Cycle's own definition as "spawn time + the boss's own alive time until death".
        public float cycleTimer;
        // Which Quarter of the current Tier Cycle's pre-boss spawn window is active — 0=Quarter 1 through
        // 3=Quarter 4 (see the "Quarter escalation" block in Simulate() for the actual thresholds). Reset to
        // 0 the instant a boss spawns, since Quarters only cover the pre-boss portion of a Tier Cycle.
        public int spawnPhase;
        // Mid-cycle event scaffolding (2026-08-24) — see the "Mid-cycle event trigger" block in Simulate()
        // for the full flow. eventStartedThisCycle is a one-shot guard so the trigger only ever fires once
        // per Tier Cycle; eventActive is true from StartEvent() until ResolveEvent() fires;
        // eventResolveTimer counts down while eventActive (flat eventDuration for now — see that field's own
        // doc comment for why this is a placeholder, not real event-driven resolution).
        public bool  eventStartedThisCycle;
        public bool  eventActive;
        public float eventResolveTimer;
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

    [Header("Tier Colors")]
    [SerializeField] private TierMaterials tier1Materials;
    [SerializeField] private TierMaterials tier2Materials;
    [SerializeField] private TierMaterials tier3Materials;
    [SerializeField] private TierMaterials tier4Materials;

    [Header("Tier Name Prefixes")]
    // Prepended (plus a separating space) to enemyName at spawn time — see EnemyBasePD.ApplyTierNamePrefix.
    // Scaffolding for the same tier1-authored-prefab-reused-at-every-tier consolidation TierMaterials above
    // is for: a base prefab is just authored with its plain name ("Blitz"), and the tier it happens to spawn
    // at supplies the rest ("Gloom Blitz" at tier1, "Doom Blitz" at tier2, etc.) instead of each tier needing
    // its own separately-named prefab copy.
    [SerializeField] private string tier1NamePrefix = "Gloom";
    [SerializeField] private string tier2NamePrefix = "Doom";
    [SerializeField] private string tier3NamePrefix = "Despair";
    [SerializeField] private string tier4NamePrefix = "Dread";

    [Header("Spawn Points")]
    // Falls back to a ring around a random living player when fewer than 2 points are assigned.
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private float spawnMinDistance = 25f;
    [SerializeField] private float spawnMaxDistance = 30f;

    [Header("Spawn Scaling")]
    // Q1-Q4 = Quarter 1 through Quarter 4 — see this file's own class-level "Tier Cycle"/"Quarter"/"Wave"
    // terminology comment. cycleInterval is the full length of a Tier Cycle's pre-boss window (all 4
    // Quarters combined), not any single Quarter's own duration.
    [SerializeField] private float spawnIntervalQ1 = 4f;
    [SerializeField] private float spawnIntervalQ2 = 3f;
    [SerializeField] private float spawnIntervalQ3 = 2f;
    [SerializeField] private float spawnIntervalQ4 = 1f;
    [SerializeField] private float cycleInterval = 180f;

    [Header("Mid-cycle Events")]
    // Flat placeholder duration between StartEvent() and ResolveEvent() (see the "Mid-cycle event trigger"
    // block in Simulate()) — a real event will eventually decide its own resolution instead of this fixed 1
    // minute, once actual event TYPES exist (see EventManagerPD's own doc comment). Trigger point itself
    // (60s into a Tier Cycle's pre-boss window, 2026-08-21 — moved back from 90s, see the "Mid-cycle event
    // trigger" block's own doc comment for why) is NOT a separate tunable — deliberately hardcoded against
    // cycleInterval directly in Simulate(), same as the Quarter escalation thresholds right above it.
    [SerializeField] private float eventDuration = 60f;

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
    // see GameManagerPD.CoopToughnessScalar below for how co-op difficulty is handled instead). Tier1/threat-
    // level1-authored baseline, never mutated — no longer scaled by Threat Level 3 (was doubled there; see
    // threatLevel3EliteStatMultiplier below for the 2026-08-26 rework: TL3 now doubles elite Health/Damage
    // instead of elite spawn count).
    [SerializeField] private int baseEliteCount = 10;
    // Threat Level 3's own unique modifier — reworked 2026-08-26 from "Elite Spawns are doubled" (which
    // scaled baseEliteCount in BuildEliteSchedule) to "Elite Health/Damage are doubled" instead, applied per-
    // spawn in SpawnElite. Stacks with every HIGHER difficulty too, same cumulative-not-standalone convention
    // every other threat modifier uses, so this stays active at every difficulty from 3 up. Simple flat
    // multiplier (2x = doubled), not a per-level growth curve like the generic stat scalars — explicit
    // design, this is a standalone tier-3-specific bump, not a formula.
    private float threatLevel3EliteStatMultiplier = 2f;
    // Threat Level 4's own unique modifier ("Spikies are 25% faster", lowered 2026-08-23 from 50%) — Pleb
    // category specifically, see SpawnEnemy's own doc comment for the exact composition (multiplies on top
    // of the generic difficulty move-speed scalar, doesn't replace it). Same cumulative-from-this-level-up
    // convention.
    private float threatLevel4PlebMoveSpeedMultiplier = 1.25f;
    // Threat Level 5's own unique modifier ("Enemies attack twice as often") — a flat halving of every
    // enemy's own attack-cooldown field, passed through ConfigureSpawn's new attackCooldownMultiplier
    // parameter (see IEnemyPD.ConfigureSpawn's own doc comment for why this can't be applied generically on
    // EnemyBasePD itself). Same cumulative-from-this-level-up convention as every other threat modifier.
    private float threatLevel5AttackCooldownMultiplier = 0.5f;
    // Threat Level 6's own unique modifier ("Double Boss spawns") — how many bosses SpawnBosses() spawns
    // per boss cycle once difficulty >= 6, instead of the default 1. Plain int, not itself threat-level-
    // specific — SpawnBosses() just takes a count parameter, same "N-simultaneous-boss design" state.
    // activeBosses' own doc comment already anticipated. Same cumulative-from-this-level-up convention.
    private int threatLevel6BossCount = 2;

    [Header("Tier Stat Scaling")]
    // Applied at spawn time, generically, to every category (normal/pleb/elite/boss) on top of whatever
    // that category's own elite/co-op multiplier already is (see Simulate()'s own comment on ordering) — so
    // an enemy TYPE only ever needs ONE authored (tier1-baseline) stat set on its prefab. Reusing the same
    // prefabs across a different "level" with a different tier progression means retuning these growth
    // curves once here, not re-tuning every individual tier's own hand-authored numbers on every prefab.
    // Move speed, damage, health, AND xp all grow LINEARLY (tier1 + this fraction of tier1 per tier step
    // above 1). Health was ALSO exponential originally (healthExponentialTierBase = 5, i.e. 5/25/125 for a
    // tier1-baseline 5-health enemy) but switched to linear on 2026-08-20 — that curve assumed player damage
    // output scaled roughly as aggressively per tier as it was tuned for, which turned out not to hold up in
    // practice, leaving high-tier enemies dramatically overtanky relative to what players could actually put
    // out. healthTierGrowth originally launched at 4 (tier1/tier2/tier3/tier4 = 1x/5x/9x/13x, matching the
    // old exponential curve's own already-validated tier1/tier2 value before tapering off instead of
    // continuing to 25x/125x) but has since been retuned further, off further live testing, to 8
    // (1x/9x/17x/25x) — retune directly here if that still isn't matching live player damage output. xp was
    // ALSO exponential originally (xpExponentialTierBase = 5, i.e. 5/25/125/625 for a tier1-baseline 5xp
    // enemy) but switched to linear on 2026-08-12 after live T4 testing showed it snowballing the player's
    // level curve — a single T3/T4 kill's xp vastly outstripped GameManagerPD.xpToNextLevel's own much slower
    // (roughly arithmetic-step) growth. xpTierGrowth originally launched at 4 (5/25/45/65 for that same
    // tier1-baseline 5xp enemy) but has since been retuned to 3 (5/20/35/50).
    [SerializeField] private float moveSpeedTierGrowth = 0.25f;
    [SerializeField] private float damageTierGrowth = 0.5f;
    [SerializeField] private float healthTierGrowth = 8f;
    [SerializeField] private float xpTierGrowth = 3f;

    // Public passthroughs (2026-08-21) — lets anything outside this spawner (e.g. DPSCheckPD scaling its own
    // dummy target's stats) replicate the EXACT SAME "1f + (tier - 1) * growth" formula this class applies to
    // every spawned enemy, reading the live, single-source-of-truth value instead of hand-duplicating a
    // magic number that could silently drift out of sync if these are ever retuned.
    public float MoveSpeedTierGrowth => moveSpeedTierGrowth;
    public float DamageTierGrowth    => damageTierGrowth;
    public float HealthTierGrowth    => healthTierGrowth;
    public float XpTierGrowth        => xpTierGrowth;

    [Header("Difficulty Scaling")]
    // Linear per difficulty level off GamePresets.instance.Difficulty (1-6), same "multiply base, no
    // exponential" convention every tier-growth field in this codebase already uses — level 1 = 1f (no-op
    // baseline) for all three, level 6 = 1f + 5*growth each. Applied as a flat multiplier on top of
    // everything else (tier scaling, elite/coop scaling) — this is deliberately the ONLY thing difficulty is
    // meant to touch on the generic side. Per-enemy BEHAVIORAL tier scaling (blitzesPerAttack, Shielder's
    // rotationRateDegPerSecond, etc.) stays purely tier-driven and is never touched by difficulty, by
    // explicit design — difficulty only reshapes how tanky/hard-hitting/fast enemies are, never their attack
    // patterns. XP deliberately has no difficulty scalar at all — the game isn't harder if players are also
    // leveling up faster to match, per explicit design. Three INDEPENDENT growth rates now (2026-08-20,
    // replaced the original single shared 20%-across-the-board rate) — damage/health/move speed each scale
    // at a different pace per difficulty level. Originally launched at 0.5/1/0.2 (level 6 = +250% damage,
    // +500% health, +100% move speed); move speed has since been retuned down to 0.1 (level 6 = +50% move
    // speed instead).
    [SerializeField] private float difficultyDamageGrowthPerLevel    = 0.5f;
    [SerializeField] private float difficultyHealthGrowthPerLevel    = 1f;
    [SerializeField] private float difficultyMoveSpeedGrowthPerLevel = 0.1f;

    // Public passthroughs — same reasoning as MoveSpeedTierGrowth/DamageTierGrowth/HealthTierGrowth/
    // XpTierGrowth above (2026-08-21, same change).
    public float DifficultyDamageGrowthPerLevel    => difficultyDamageGrowthPerLevel;
    public float DifficultyHealthGrowthPerLevel    => difficultyHealthGrowthPerLevel;
    public float DifficultyMoveSpeedGrowthPerLevel => difficultyMoveSpeedGrowthPerLevel;

    // Cached once per Simulate() tick (see that method's own comment for exactly where) — plain fields, not
    // State, since they're fully re-derived from GamePresets.instance.Difficulty/state.bossesDefeated every
    // tick regardless, same "compute once at the top, read by same-tick logic further down" convention
    // _currentDelta already establishes elsewhere in this codebase. SpawnEnemy is the only reader, and only
    // ever runs from within this same Simulate() call (directly or via SpawnElite), so these are always
    // fresh by the time it's called.
    private float _tierMoveSpeedMultiplier = 1f;
    private float _tierDamageMultiplier = 1f;
    private float _tierHealthMultiplier = 1f;
    private float _tierXpMultiplier = 1f;
    private float _difficultyMoveSpeedScalar = 1f;
    private float _difficultyDamageScalar = 1f;
    private float _difficultyHealthScalar = 1f;
    // The raw tier number itself (not a multiplier) — passed through to ConfigureSpawn so each enemy can
    // remember which tier it was actually spawned at (EnemyBasePD.spawnTier), since by the time it dies
    // (possibly much later) the spawner may already be on a later tier — XPOrbManagerPD needs the tier it
    // was SPAWNED at, not whatever's current, to pick the right orb prefab.
    private int _currentTierNumber = 1;
    // The raw difficulty number itself — same "cache once at the top of Simulate(), read by SpawnEnemy
    // further down" convention as _currentTierNumber above. SpawnEnemy uses this directly (not just the
    // pre-multiplied _difficultyMoveSpeedScalar) for Threat Level 4's own Pleb-only speed bump, since that
    // needs the raw number to gate on (>= 4), not a multiplier.
    private int _currentDifficulty = 1;

    [SerializeField] private int bossesToDefeatForWin = 4;

    [Header("Spawn Limits")]
    [SerializeField] private int maxNormalEnemies = 100;
    [SerializeField] private int maxEliteEnemies = 100;
    [SerializeField] private int maxPlebEnemies = 500;
    [SerializeField] private int plebSpawnAmount = 5;
    [SerializeField] private float plebSpawnIntervalQ1 = 1.75f;
    [SerializeField] private float plebSpawnIntervalQ2 = 1.5f;
    [SerializeField] private float plebSpawnIntervalQ3 = 1.25f;
    [SerializeField] private float plebSpawnIntervalQ4 = 1f;

    // Reused scratch buffer for RollNewSet()'s pool-and-remove pick — avoids a per-call allocation.
    private readonly List<int> _pool = new();
    // Reused scratch buffer for SpawnBosses()'s own pool-and-remove pick — same shape as _pool above, kept
    // separate since the two could in principle both be mid-use within the same Simulate() tick.
    private readonly List<int> _bossPool = new();

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
        // No longer built here — see BuildEliteSchedule's own doc comment for why it moved to Simulate()'s
        // own state.isActive activation point instead.
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

        // Was also reading GameManagerPD.Difficulty here to double baseEliteCount at Threat Level 3+ — removed
        // 2026-08-26, TL3's own modifier no longer touches elite COUNT at all (see
        // threatLevel3EliteStatMultiplier's own doc comment for the rework: it doubles elite Health/Damage
        // per-spawn instead, in SpawnElite). baseEliteCount is now used completely unconditionally here.
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
        // !state.isActive, so these sit untouched at 0 until GameManagerPD confirms LoadingScreenRevealed.
        // The very first active tick's countdown then immediately finds spawnTimer/plebSpawnTimer <= 0f, triggering
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
            // LoadingScreenRevealed, not GameStarted (2026-08-18) — see its own doc comment. Spawning (and
            // the boss-spawn countdown display, which just reads this identity's own cycleTimer) used to
            // start the instant GameStarted flipped true, well before the loading screen actually cleared,
            // so a player's first real look at the game already had enemies closing in and the timer
            // already several seconds spent.
            if (GameManagerPD.instance == null || !GameManagerPD.instance.LoadingScreenRevealed) return;
            state.isActive = true;
            // Built here, not Awake() — see BuildEliteSchedule's own doc comment for why it needs
            // GameManagerPD.Difficulty, which isn't meaningfully resolved yet at Awake()-time (GameManagerPD
            // itself hasn't replicated the server's authoritative value that early). By this point
            // (LoadingScreenRevealed already true) it's long since settled on every machine.
            BuildEliteSchedule();
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
            // New Tier Cycle's own pre-boss window truly begins here (cycleTimer starts decrementing again
            // right below) — re-arm the mid-cycle event trigger so it can fire again this cycle. Deliberately
            // NOT touching eventActive/eventResolveTimer here — an event still resolving right as the
            // previous boss died just keeps counting down undisturbed (no defined interaction with a fresh
            // Tier Cycle starting yet — scaffolding only, see EventManagerPD's own doc comment).
            state.eventStartedThisCycle = false;
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
        _tierHealthMultiplier    = 1f + (tierNumber - 1) * healthTierGrowth;
        _tierXpMultiplier        = 1f + (tierNumber - 1) * xpTierGrowth;
        _currentTierNumber       = tierNumber;

        // Read off GameManagerPD.Difficulty, NOT GamePresets directly (2026-08-21 fix) — see
        // BuildEliteSchedule's own doc comment for the full reasoning (GamePresets is local-per-machine,
        // never network-agreed; GameManagerPD.Difficulty is the actual server-authoritative single source
        // of truth). Falls back to difficulty 1 (1f, no-op) if GameManagerPD doesn't exist yet — same
        // "missing optional cross-scene singleton is a harmless no-op" precedent TierAccentMaterial's own
        // EnemySpawnerPD.instance null-check already establishes; harmless here too since this whole block
        // only runs once state.isActive (well after both GameManagerPD and its replicated Difficulty exist).
        int difficulty = GameManagerPD.instance != null ? GameManagerPD.instance.Difficulty : 1;
        _difficultyDamageScalar    = 1f + (difficulty - 1) * difficultyDamageGrowthPerLevel;
        _difficultyHealthScalar    = 1f + (difficulty - 1) * difficultyHealthGrowthPerLevel;
        _difficultyMoveSpeedScalar = 1f + (difficulty - 1) * difficultyMoveSpeedGrowthPerLevel;
        _currentDifficulty         = difficulty;

        // ── Quarter escalation ────────────────────────────────────────────
        // Advances into Quarter 2/3/4 (Quarter 1 needs no threshold — it's just wherever cycleTimer starts,
        // the instant a Tier Cycle's pre-boss window begins) at 75%/50%/25% of the Tier Cycle remaining —
        // same three thresholds as old EnemySpawner.SpawnLoop(). Each transition tightens the interval
        // settings (without cutting short a countdown already in flight — only the NEXT countdown picks up
        // the new interval) and rerolls a fresh Wave via RollNewSet().
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

        // ── Mid-cycle event trigger ──────────────────────────────────────────
        // Fires once per Tier Cycle, 60s into its pre-boss window (2026-08-21 — moved back from 90s/the same
        // 50% threshold Quarter 3 escalates on, which this used to deliberately coincide with). At
        // cycleInterval's own default of 180s, 90s-elapsed put the event's own fixed 60s window running
        // right through Q4's fastest spawn rates and the tail of the elite pack — genuinely too punishing to
        // delay completion on, especially TL3+ where elite spawns are doubled, since the threat of escalating
        // spawns is meant to incentivize finishing SOONER, not make later completion nearly impossible. 60s
        // elapsed (cycleInterval * 2/3 remaining) now sits between Q2 and Q3, squarely in the middle of the
        // elite spawn pack instead, decoupled from Q3's own threshold. Called as a plain direct method call,
        // not through any event/broadcast system — Simulate() already runs identically and deterministically
        // on every machine, so a direct call here is exactly as resimulation-safe as any other cross-identity
        // call in this codebase (e.g. PausePD.SetPlayerReady).
        if (!state.eventStartedThisCycle && state.cycleTimer < cycleInterval * (2f / 3f))
        {
            state.eventStartedThisCycle = true;
            state.eventActive = true;
            state.eventResolveTimer = eventDuration;
            if (EventManagerPD.instance != null) EventManagerPD.instance.StartEvent(_currentTierNumber);
        }
        if (state.eventActive)
        {
            state.eventResolveTimer -= delta;
            if (state.eventResolveTimer <= 0f)
            {
                state.eventActive = false;
                if (EventManagerPD.instance != null) EventManagerPD.instance.ResolveEvent();
            }
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

        // ── Elites + Tier Cycle countdown ───────────────────────────────────
        // cycleTimer only advances while no boss is alive — once a boss spawns it resets to cycleInterval
        // and stays there (never decrementing) until the boss dies, which is what stops this from
        // immediately trying to spawn a second boss on top of the first. This pause is also exactly what
        // makes a Tier Cycle's own definition true by construction: "spawn time + the boss's alive time
        // until death", not just the pre-boss spawn window alone.
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

        // Quarter 4 has fully elapsed — pre-arm cycleTimer/spawnPhase back to Quarter 1 for the NEXT Tier
        // Cycle's own pre-boss window now, even though it won't actually start counting down until the boss
        // about to spawn below dies (see the "Elites + Tier Cycle countdown" block's own bossAlive-pause
        // comment) — this Tier Cycle itself doesn't truly end until then.
        state.cycleTimer = cycleInterval;
        state.spawnPhase = 0;
        // Same pre-arm-ahead-of-time reasoning as spawnPhase above — harmless even though the actual reset
        // that matters for THIS boss fight already happened back when it died.
        state.eventStartedThisCycle = false;
        state.nextEliteIndex = 0;

        if (tier.boss != null && tier.boss.Length > 0)
        {
            // Wipe every currently-alive enemy right before the boss lands — see WipeAllEnemies' own doc
            // comment. Now that the pre-boss spawn blackout is gone, enemies keep spawning right up until
            // this exact moment, so without this the boss would land in the middle of a full, ever-growing
            // swarm instead of a clean arena.
            WipeAllEnemies();

            // Threat Level 6's own unique modifier ("Double Boss spawns") — see threatLevel6BossCount's own
            // doc comment. Stacks at every difficulty from 6 up, same cumulative convention every other
            // threat modifier uses.
            int bossSpawnCount = _currentDifficulty >= 6 ? threatLevel6BossCount : 1;
            SpawnBosses(ref state, tier, coopScale, bossSpawnCount);
        }
    }

    // Spawns `count` bosses from tier.boss, preferring DISTINCT prefabs — same "pool-and-swap-remove,
    // refill only once exhausted" shape RollNewSet() already uses for normal enemies (see its own doc
    // comment), just applied to tier.boss instead of tier.normal. Only ever repeats a prefab once count
    // exceeds how many distinct boss prefabs this tier actually has (e.g. count=2 against a 1-boss tier) —
    // "prefer non-duplicates, duplicate only if forced," per explicit design. Not itself threat-level-
    // specific — count is just a plain parameter, so any future N-simultaneous-boss design reuses this
    // unchanged (see state.activeBosses' own doc comment).
    private void SpawnBosses(ref State state, TierEnemies tier, float coopScale, int count)
    {
        _bossPool.Clear();
        for (int i = 0; i < count; i++)
        {
            if (_bossPool.Count == 0)
                for (int j = 0; j < tier.boss.Length; j++) _bossPool.Add(j);

            int roll = GameRandom.instance.Next(_bossPool.Count);
            var prefab = tier.boss[_bossPool[roll]];
            _bossPool[roll] = _bossPool[_bossPool.Count - 1];
            _bossPool.RemoveAt(_bossPool.Count - 1);

            var id = SpawnEnemy(prefab, EnemySpawnCategory.Boss, coopScale, 1f, coopScale);
            if (id.HasValue) state.activeBosses.Add(id.Value);
        }
    }

    // Shared by both Tier Cycle transitions — right before a boss spawns, and right after one dies (see
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
    // health/XP by the same shared coopScale the caller already computed, which is what keeps total elite
    // XP output consistent between solo and multiplayer instead of just throwing more bodies at a bigger
    // group. Damage is deliberately NOT coop-scaled (2026-08-27 fix — was coopScale * eliteStatMultiplier
    // before this, an oversight; damage output shouldn't climb just because more players joined, matching
    // SpawnBosses' own identical health-and-XP-only, damage-flat convention). Threat Level 3+ additionally
    // doubles Health/Damage only (not XP — see threatLevel3EliteStatMultiplier's own doc comment) — for
    // damage this is now the ONLY multiplier applied, not stacked on top of coopScale anymore.
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

        float eliteStatMultiplier = _currentDifficulty >= 3 ? threatLevel3EliteStatMultiplier : 1f;

        int skip = GameRandom.instance.Next(poolCount);
        for (int j = 0; j < state.currentSetIndices.Count; j++)
        {
            int idx = state.currentSetIndices[j];
            if (idx >= tier.elite.Length || tier.elite[idx] == null) continue;
            if (skip == 0)
            {
                SpawnEnemy(tier.elite[idx], EnemySpawnCategory.Elite,
                    coopScale * eliteStatMultiplier, eliteStatMultiplier, coopScale);
                return;
            }
            skip--;
        }
    }

    // Rolls this Quarter's own Wave — the 3 enemy types (state.currentSetIndices) actually spawning as
    // "normal" enemies until the next Quarter escalation rerolls it. Called once per Quarter transition (see
    // the "Quarter escalation" block in Simulate()), plus once at Tier Cycle start/RollNewSet's own two other
    // call sites.
    private void RollNewSet(ref State state)
    {
        var tier = GetCurrentTier(state.bossesDefeated);
        if (tier.normal == null || tier.normal.Length == 0) return;

        int pickCount = Mathf.Min(3, tier.normal.Length);

        // Prefer an exclusive pick (no repeats from the previous Wave) when there are enough fresh indices
        // to fill pickCount; otherwise fall back to picking from the full pool — same two-step shape as old
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

    // Public — called by EnemyBasePD.ApplyTierMaterials at spawn time (tierNumber there is the same
    // ConfigureSpawn tier argument this class already hands out via _currentTierNumber). tierNumber <= 1
    // falls to tier1Materials, same clamp-to-1-based-indexing shape as GetCurrentTier's own switch.
    public TierMaterials GetTierMaterials(int tierNumber) => tierNumber switch
    {
        <= 1 => tier1Materials,
        2 => tier2Materials,
        3 => tier3Materials,
        _ => tier4Materials
    };

    // Public — called by EnemyBasePD.ApplyTierNamePrefix at spawn time. Same clamp-to-tier4 shape as
    // GetTierMaterials above.
    public string GetTierNamePrefix(int tierNumber) => tierNumber switch
    {
        <= 1 => tier1NamePrefix,
        2 => tier2NamePrefix,
        3 => tier3NamePrefix,
        _ => tier4NamePrefix
    };

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
        {
            // Threat Level 4's own unique modifier ("Spikies are 25% faster") — Pleb category specifically
            // (Spike Gloom is the only pleb enemy type today, but this is a category-wide bump, not a
            // per-prefab one, per explicit design), multiplying ON TOP of the generic difficulty move-speed
            // scalar rather than replacing/being independent of it. Stacks with every HIGHER difficulty too,
            // same cumulative convention every other threat modifier uses.
            float moveSpeedMultiplier = _tierMoveSpeedMultiplier * _difficultyMoveSpeedScalar;
            if (category == EnemySpawnCategory.Pleb && _currentDifficulty >= 4)
                moveSpeedMultiplier *= threatLevel4PlebMoveSpeedMultiplier;

            // Threat Level 5's own unique modifier — see threatLevel5AttackCooldownMultiplier's own doc
            // comment. Not category-gated (applies to every enemy, unlike Threat Level 4's Pleb-only bump).
            float attackCooldownMultiplier = _currentDifficulty >= 5 ? threatLevel5AttackCooldownMultiplier : 1f;

            enemy.ConfigureSpawn(category,
                _tierHealthMultiplier * healthMultiplier * _difficultyHealthScalar,
                _tierDamageMultiplier * damageMultiplier * _difficultyDamageScalar,
                _tierXpMultiplier * xpMultiplier,
                moveSpeedMultiplier,
                attackCooldownMultiplier,
                _currentTierNumber);
        }

        return id;
    }

    // Deliberately does NOT validate against PlayableAreaPD's boundary (2026-08-26 — a prior version of this
    // method rejected/clamped candidates outside it, based on a misdiagnosis of a corner-clustering bug).
    // Enemies are intentionally allowed to spawn beyond the boundary on purpose — so they walk in through
    // the wall instead of visibly popping into existence inside a player's view. The actual fix for that
    // corner-clustering bug lives on EnemyBasePD.boundsRestricted's own trigger instead: it only ever
    // activates on an actual backpedal tick (matches the original design — see that field's own doc
    // comment), so a freshly-spawned enemy that's just approaching/walking in through the wall never
    // triggers strafing at all, regardless of how far outside the boundary it currently is.
    private Vector3 GetSpawnPosition()
    {
        if (spawnPoints != null && spawnPoints.Length >= 2)
        {
            int i = GameRandom.instance.Next(spawnPoints.Length);
            int j = (i + 1) % spawnPoints.Length;
            return Vector3.Lerp(spawnPoints[i].position, spawnPoints[j].position, GameRandom.instance.NextFloat(0f, 1f));
        }

        // Fallback: ring around a random living player, retried until far enough from every player. Reads
        // CharacterStatsPD.All directly — safe from Simulate() the same way EnemyBasePD.FindClosestTarget()
        // reads it, since we're only reading each entry's own predicted currentState, not relying on the
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
