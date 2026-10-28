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
    // Requires at least 2 — see GetSpawnPosition's own doc comment.
    [SerializeField] private Transform[] spawnPoints;

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
    // Flat timeout between StartEvent() and the "ran out of time" ResolveEvent() call (see the "Mid-cycle
    // event trigger" block in Simulate()) — every event still succeeds or fails on its own actual win
    // condition well before this if the players are fast; this is purely the outer failure deadline. Raised
    // 2026-09-02 from 60 to 90 — events are meant to be clearable in ~30s, this just adds extra leeway on
    // top rather than being the expected completion time itself. Trigger point itself (60s into a Tier
    // Cycle's pre-boss window, 2026-08-21 — moved back from 90s, see the "Mid-cycle event trigger" block's
    // own doc comment for why) is NOT a separate tunable — deliberately hardcoded against cycleInterval
    // directly in Simulate(), same as the Quarter escalation thresholds right above it. At cycleInterval's
    // own default (180s), this now leaves a 30s buffer before the boss spawns (was 60s at the old 60s
    // value) — still no overlap, just a tighter margin; retune cycleInterval too if that buffer ever needs
    // to grow back.
    //
    // This is the ONE real source of truth for how long an event lasts — EventBasePD's own "Time Remaining"
    // display (and its white<->red end-of-event flash) reads it live via the public getter below instead of
    // carrying its own separately-authored copy, which used to be able to silently drift out of sync with
    // this actual value.
    [SerializeField] private float eventDuration = 90f;
    public float EventDuration => eventDuration;

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
    // multiplier, not a per-level growth curve like the generic stat scalars — explicit design, this is a
    // standalone tier-3-specific bump, not a formula. Nerfed 2026-10-07 from 2f (double) to 1.5f (+50%).
    private float threatLevel3EliteStatMultiplier = 1.5f;
    // Threat Level 4's own unique modifier ("Spikies are 25% faster", lowered 2026-08-23 from 50%) — Pleb
    // category specifically, see SpawnEnemy's own doc comment for the exact composition (multiplies on top
    // of the generic difficulty move-speed scalar, doesn't replace it). Same cumulative-from-this-level-up
    // convention.
    private float threatLevel4PlebMoveSpeedMultiplier = 1.25f;
    // Threat Level 5's own unique modifier ("Enemies attack 50% more often", nerfed 2026-09-01 from "twice
    // as often"/0.5f) — a flat 25% cut off every enemy's own attack-cooldown field, passed through
    // ConfigureSpawn's new attackCooldownMultiplier parameter (see IEnemyPD.ConfigureSpawn's own doc comment
    // for why this can't be applied generically on EnemyBasePD itself). Same cumulative-from-this-level-up
    // convention as every other threat modifier.
    private float threatLevel5AttackCooldownMultiplier = 0.75f;
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
    // Move speed, damage, AND xp all grow LINEARLY (tier1 + this fraction of tier1 per tier step above 1).
    // Health went through several shapes: pure exponential originally (healthExponentialTierBase = 5, i.e.
    // 5/25/125 for a tier1-baseline 5-health enemy), switched to pure linear on 2026-08-20 (assumed player
    // damage output scaled roughly as aggressively per tier as tuned for — didn't hold up, T4 enemies read as
    // dramatically overtanky), then reworked 2026-08-30 to growth*(tier-1) floored at 1x (1x/5x/10x/15x with
    // growth=5). That pure-linear shape then turned out to undershoot at T4 in live play (T1/T2 felt right,
    // T3 unclear, T4 clearly underpowered) — player damage output itself doesn't scale purely linearly
    // (Strength grows linearly per level, but stat-augments/augments stack exponentially/multiplicatively on
    // top), so a pure-linear health curve permanently falls behind. Reworked AGAIN 2026-09-16 to a hybrid
    // linear+exponential shape combining both: healthTierGrowth*(tier-1) + healthExponentialTierBase^(tier-1)
    // — see _tierHealthMultiplier's own computation for the exact formula. growth=2, exponentialBase=3 lands
    // exactly on 1x/5x/13x/33x for tiers 1-4 (tier1 needs no floor — the formula already lands on exactly 1x
    // there since growth*0 + base^0 = 0+1). Health remains the ONE exception to the shared additive formula
    // every other stat below still uses — retune growth/exponentialBase directly here (or switch shape
    // again) if live testing calls for it. xp was
    // ALSO exponential originally (xpExponentialTierBase = 5, i.e. 5/25/125/625 for a tier1-baseline 5xp
    // enemy) but switched to linear on 2026-08-12 after live T4 testing showed it snowballing the player's
    // level curve — a single T3/T4 kill's xp vastly outstripped GameManagerPD.xpToNextLevel's own much slower
    // (roughly arithmetic-step) growth. xpTierGrowth originally launched at 4 (5/25/45/65 for that same
    // tier1-baseline 5xp enemy) but has since been retuned to 3 (5/20/35/50).
    [SerializeField] private float moveSpeedTierGrowth = 0.25f;
    // Identical "1 + (tier-1)*growth" shape/value as moveSpeedTierGrowth (2026-09-14, added) — tuned to
    // match for now purely as a starting reference point; own independent field/
    // scalar/ConfigureSpawn param throughout (same "coincidentally the same rate, not a true pairing" framing
    // difficultyAttackSpeedGrowthPerLevel's own doc comment already established for the difficulty side), so
    // retune freely without touching moveSpeedTierGrowth. Feeds EnemyBasePD.attackSpeed on top of whatever
    // difficultyAttackSpeedGrowthPerLevel already contributes — see the SpawnEnemy call site's own comment for
    // how the two combine.
    [SerializeField] private float attackSpeedTierGrowth = 0.25f;
    [SerializeField] private float damageTierGrowth = 0.5f;
    // 2026-09-16, was 5f (pure-linear shape) — now the LINEAR half of health's hybrid formula, see this
    // header's own doc comment above. healthExponentialTierBase is the EXPONENTIAL half.
    [SerializeField] private float healthTierGrowth = 2f;
    // Exponential half of health's hybrid formula (2026-09-16, reintroduced) — see this header's own doc
    // comment above for the full derivation. 3f combined with healthTierGrowth=2f lands on 1x/5x/13x/33x.
    [SerializeField] private float healthExponentialTierBase = 3f;
    [SerializeField] private float xpTierGrowth = 3f;
    // Fully independent from xpTierGrowth (2026-08-29 — upgrade points are no longer derived from XP at all,
    // see EnemyBasePD.pointsValue's own doc comment) — no relationship to xpTierGrowth's own value, tune
    // this purely on its own merits. Default 1 — a clean +1x-of-base-per-tier progression (via
    // "1 + (tierNumber-1) * growth"), e.g. a 1-point-base enemy reads 1/2/3/4 across tiers 1-4, matching
    // the worked reference example exactly.
    [SerializeField] private float pointsTierGrowth = 1f;

    // Public formula methods (2026-08-21, was raw-growth-constant passthroughs; reworked 2026-09-16 to
    // return the actual computed multiplier instead) — lets anything outside this spawner (e.g. DPSCheckPD/
    // DestroyTheTargetsPD/PerimeterPatrolEventPD scaling their own event targets via EventTargetScalingPD)
    // replicate the EXACT SAME per-tier scaling this class applies to every spawned enemy for a given tier
    // number, by calling the one real implementation instead of hand-copying its shape. The old passthrough-
    // only version (raw growth constant, shape re-derived by the caller) silently fell out of sync the moment
    // health's own shape diverged from the shared "1 + (tier-1)*growth" formula every other stat still uses
    // (see healthTierGrowth's own doc comment) — EventTargetScalingPD's hand-copied health formula kept using
    // the OLD shared shape for a full rework cycle until caught by hand. These methods are the fix: nothing
    // outside this class ever re-derives the formula itself again, only calls it.
    public float ComputeTierMoveSpeedMultiplier(int tierNumber)   => 1f + (tierNumber - 1) * moveSpeedTierGrowth;
    public float ComputeTierAttackSpeedMultiplier(int tierNumber) => 1f + (tierNumber - 1) * attackSpeedTierGrowth;
    public float ComputeTierDamageMultiplier(int tierNumber)      => 1f + (tierNumber - 1) * damageTierGrowth;
    // Hybrid linear+exponential shape — see healthTierGrowth's own doc comment above for the full derivation.
    // The ONE place this formula is allowed to exist; _tierHealthMultiplier's own Simulate()-tick computation
    // calls this too rather than keeping a second, hand-synced copy.
    public float ComputeTierHealthMultiplier(int tierNumber)      => healthTierGrowth * (tierNumber - 1) + Mathf.Pow(healthExponentialTierBase, tierNumber - 1);
    public float ComputeTierXpMultiplier(int tierNumber)          => 1f + (tierNumber - 1) * xpTierGrowth;
    public float ComputeTierPointsMultiplier(int tierNumber)      => 1f + (tierNumber - 1) * pointsTierGrowth;

    [Header("Difficulty Scaling")]
    // Linear per difficulty level off GamePresets.instance.Difficulty (1-6), same "multiply base, no
    // exponential" convention every tier-growth field in this codebase already uses — level 1 = 1f (no-op
    // baseline) for all four, level 6 = 1f + 5*growth each. Applied as a flat multiplier on top of
    // everything else (tier scaling, elite/coop scaling) — this is deliberately the ONLY thing difficulty is
    // meant to touch on the generic side. Per-enemy BEHAVIORAL tier scaling (blitzesPerAttack, Shielder's
    // rotationRateDegPerSecond, etc.) stays purely tier-driven and is never touched by difficulty, by
    // explicit design — difficulty only reshapes how tanky/hard-hitting/fast enemies are (attack SPEED
    // included — a generic "how fast" scalar, not a behavioral one), never their attack patterns. XP
    // deliberately has no difficulty scalar at all — the game isn't harder if players are also leveling up
    // faster to match, per explicit design. Damage/health/move speed each scale at a different pace per
    // difficulty level — INDEPENDENT growth rates (2026-08-20, replaced the original single shared
    // 20%-across-the-board rate). Originally launched at 0.5/1/0.2 (level 6 = +250% damage, +500% health,
    // +100% move speed); move speed has since been retuned down to 0.1 (level 6 = +50% move speed instead).
    // Damage retuned down again too (2026-09-01), from 0.5 to 0.2 (level 6 = +100% damage instead of +250%)
    // — paired with a coupled increase to enemies' own authored base damage (per-enemy, not this scalar), an
    // explicit attempt to clamp how far damage swings across the full difficulty range rather than leaning
    // almost entirely on this one per-level multiplier.
    //
    // difficultyAttackSpeedGrowthPerLevel (2026-08-31, added) — same 0.1/level rate as move speed, but
    // KEPT DELIBERATELY SEPARATE (own field, own scalar, own ConfigureSpawn param) rather than reusing/
    // deriving from difficultyMoveSpeedGrowthPerLevel — per explicit user framing, the matching numbers are
    // coincidental, not a true pairing; retune independently if they ever diverge. Feeds EnemyBasePD.
    // attackSpeed (the base EffectiveAttackSpeed already reads), which every windup/attack-state timer
    // across the whole roster already scales by (state.stateTimer -= delta * EffectiveAttackSpeed) — added
    // specifically because a player's own move-speed progression had been invalidating certain enemies'
    // fixed-duration windups (Spear/Dagger/Leaper/Mortar most notably: a faster player can now simply
    // outrun/escape a windup that takes exactly as long as it always did), same shape as move speed's own
    // per-difficulty growth so windup completion keeps pace with how fast players are getting.
    [SerializeField] private float difficultyDamageGrowthPerLevel      = 0.2f;
    [SerializeField] private float difficultyHealthGrowthPerLevel      = 1f;
    // Halved 2026-10-12 (10%->5% per level, 50% at TL6->25% at TL6) alongside a preemptive player
    // move speed pass — explicit intent: a fully-upgraded player (base 10 + shop's own +2 cap = 12, +20%)
    // now sits SLIGHTLY below a TL6-scaled enemy's own +25%, rather than comfortably ahead of it as the old
    // 10%/level rate produced (+50% at TL6 vs the player's +20%, an even bigger gap). Deliberate, not a bug
    // — revisit/revert if live testing says this edge feels bad.
    [SerializeField] private float difficultyMoveSpeedGrowthPerLevel   = 0.05f;
    [SerializeField] private float difficultyAttackSpeedGrowthPerLevel = 0.1f;

    // Public passthroughs — same reasoning as MoveSpeedTierGrowth/DamageTierGrowth/HealthTierGrowth/
    // XpTierGrowth above (2026-08-21, same change).
    public float DifficultyDamageGrowthPerLevel      => difficultyDamageGrowthPerLevel;
    public float DifficultyHealthGrowthPerLevel      => difficultyHealthGrowthPerLevel;
    public float DifficultyMoveSpeedGrowthPerLevel   => difficultyMoveSpeedGrowthPerLevel;
    public float DifficultyAttackSpeedGrowthPerLevel => difficultyAttackSpeedGrowthPerLevel;

    // Cached once per Simulate() tick (see that method's own comment for exactly where) — plain fields, not
    // State, since they're fully re-derived from GamePresets.instance.Difficulty/state.bossesDefeated every
    // tick regardless, same "compute once at the top, read by same-tick logic further down" convention
    // _currentDelta already establishes elsewhere in this codebase. SpawnEnemy is the only reader, and only
    // ever runs from within this same Simulate() call (directly or via SpawnElite), so these are always
    // fresh by the time it's called.
    private float _tierMoveSpeedMultiplier = 1f;
    private float _tierAttackSpeedMultiplier = 1f;
    private float _tierDamageMultiplier = 1f;
    private float _tierHealthMultiplier = 1f;
    private float _tierXpMultiplier = 1f;
    private float _tierPointsMultiplier = 1f;
    private float _difficultyMoveSpeedScalar = 1f;
    private float _difficultyAttackSpeedScalar = 1f;
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

    // Flat across every Threat Level (2026-10-17 revert — TL1-3 used to end the run after 1/2/3 boss
    // cycles instead of the full 4, per the old "earlier levels are meant to be quick" design; reverted,
    // every difficulty now runs the full 4-tier cycle).
    [SerializeField] private int bossesToDefeatForWin = 4;
    // EventManagerPD reads this as the real source of truth for "how many Tier Cycles (and therefore
    // mid-cycle event triggers) a run can ever have," since a win fires the moment this many bosses are
    // defeated and a Tier Cycle always ends in a boss spawn.
    public int BossesToDefeatForWin => bossesToDefeatForWin;

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
    private readonly HashSet<IFriendlyPD> _subscribedPlayers = new();

    public void RegisterEnemy(IEnemyPD enemy) => _activeEnemies.Add(enemy);
    public void UnregisterEnemy(IEnemyPD enemy) => _activeEnemies.Remove(enemy);

    // Read-only enumeration for callers that need to scan currently-alive enemies (e.g. OrbiterGloomPD finding
    // nearby Pleb-tier enemies to grab) — IEnumerable, not a collection type, since every known caller only
    // ever foreach's it.
    public IEnumerable<IEnemyPD> ActiveEnemies => _activeEnemies;

    // FriendlyTargetRegistryPD.All (2026-08-30, was CharacterStatsPD.All.Values) — subscribes ANY registered
    // friendly, not just connected players, so a future non-player friendly's Taunt/Invisible drives the
    // exact same enemy-targeting reaction a player's already does (see IFriendlyPD.AddTauntChangedListener's
    // own doc comment). Players don't exist yet in the registry at this object's own LateAwake() (they
    // register from PlayerDataPD.Simulate(), which only starts running once actual per-tick simulation
    // begins) — so this is checked every Simulate() tick instead, cheap since _subscribedPlayers.Add()
    // short-circuits (skipping the listener-registration work) for anyone already subscribed. Once
    // subscribed, a friendly's own two PredictedEvents call back into these forever, so this never
    // re-subscribes the same one twice.
    private void SubscribeNewPlayers()
    {
        foreach (var player in FriendlyTargetRegistryPD.All)
        {
            if (!_subscribedPlayers.Add(player)) continue;
            var p = player;
            p.AddTauntChangedListener(isTaunting => OnPlayerTauntChanged(p, isTaunting));
            p.AddInvisibleChangedListener(isInvisible => OnPlayerInvisibleChanged(p, isInvisible));
        }
    }

    private void OnPlayerTauntChanged(IFriendlyPD player, bool isTaunting)
    {
        foreach (var enemy in _activeEnemies) enemy.OnPlayerTauntChanged(player, isTaunting);
    }

    private void OnPlayerInvisibleChanged(IFriendlyPD player, bool isInvisible)
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

    // Not DontDestroyOnLoad — see LocalUIManagerPD.OnDestroy's own doc comment for the full reasoning
    // (guarded against nulling out a newer instance; without this, `instance` was left dangling on a
    // destroyed object across a scene return to lobby). Destroyed(), not raw OnDestroy() — same PD lifecycle
    // convention WeaponBasePD/EnemyBasePD's own Destroyed() overrides already use.
    protected override void Destroyed()
    {
        if (instance == this) instance = null;
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
        // Single shared "how much harder is co-op" multiplier. Uniform across every category now
        // (2026-09-02, was spawn-rate acceleration for Normal/Pleb, health/XP for Elite/Boss only — see
        // SpawnElite's own doc comment for why that split existed and why it's gone): health and points
        // scale by this for Normal/Pleb/Elite/Boss alike, damage never does (matching the Elite/Boss
        // convention this was extended FROM — "tougher, more valuable enemies," not "harder-hitting" or
        // "more frequent" ones). Spawn timers (spawnTimer/plebSpawnTimer below) no longer read this at all
        // — spawn cadence is now identical regardless of player count, only each individual enemy's own
        // toughness/value scales with it.
        //
        // XP deliberately does NOT scale by this anymore (2026-09-03 fix) — it used to (reusing this same
        // value as SpawnEnemy's own xpMultiplier param), paired with an equal-and-opposite division back
        // out in GameManagerPD.AwardXP, on the reasoning that total enemy COUNT also scaled with player
        // count back then, so inflating-then-normalizing per-kill value was what kept overall XP gained
        // roughly the same across solo/co-op. Enemy count no longer scales with player count at all (see
        // this comment's own history above) — so with a fixed total number of kills either way, XP now has
        // to stay flat at the SOURCE for total XP gained to actually stay equal; see AwardXP's own doc
        // comment for the full reasoning. Points keeps the coopScale bump deliberately (see
        // pointsTierGrowth's own doc comment) — it's the intended replacement for co-op's old "more
        // enemies (used to) mean more total points" effect, and AwardUpgradePoints was never paired with
        // any offsetting division, so this is genuinely additive there, not a no-op like it was for XP.
        float coopScale = 1f + (playerCount - 1) * (GameManagerPD.instance != null ? GameManagerPD.instance.CoopToughnessScalar : 0f);
        // Bosses get their own, steeper per-player health curve (2026-10-17 split) — see
        // GameManagerPD.bossCoopToughnessScalar's own doc comment for why one shared scalar couldn't serve
        // both normals and bosses. Only used for health below (SpawnBosses still takes the shared coopScale
        // for its points argument — points-per-player-count was never the part that felt wrong).
        float bossCoopScale = 1f + (playerCount - 1) * (GameManagerPD.instance != null ? GameManagerPD.instance.BossCoopToughnessScalar : 0f);

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
        // Routed through the Compute* methods below (2026-09-16, was inline duplicates of the same math) so
        // this cached-per-tick computation and any outside caller (EventTargetScalingPD) share the exact same
        // single implementation — see those methods' own doc comment for why.
        _tierMoveSpeedMultiplier   = ComputeTierMoveSpeedMultiplier(tierNumber);
        _tierAttackSpeedMultiplier = ComputeTierAttackSpeedMultiplier(tierNumber);
        _tierDamageMultiplier    = ComputeTierDamageMultiplier(tierNumber);
        _tierHealthMultiplier    = ComputeTierHealthMultiplier(tierNumber);
        _tierXpMultiplier        = ComputeTierXpMultiplier(tierNumber);
        _tierPointsMultiplier    = ComputeTierPointsMultiplier(tierNumber);
        _currentTierNumber       = tierNumber;

        // Read off GameManagerPD.Difficulty, NOT GamePresets directly (2026-08-21 fix) — see
        // BuildEliteSchedule's own doc comment for the full reasoning (GamePresets is local-per-machine,
        // never network-agreed; GameManagerPD.Difficulty is the actual server-authoritative single source
        // of truth). Falls back to difficulty 1 (1f, no-op) if GameManagerPD doesn't exist yet — same
        // "missing optional cross-scene singleton is a harmless no-op" precedent TierAccentMaterial's own
        // EnemySpawnerPD.instance null-check already establishes; harmless here too since this whole block
        // only runs once state.isActive (well after both GameManagerPD and its replicated Difficulty exist).
        int difficulty = GameManagerPD.instance != null ? GameManagerPD.instance.Difficulty : 1;
        _difficultyDamageScalar      = 1f + (difficulty - 1) * difficultyDamageGrowthPerLevel;
        _difficultyHealthScalar      = 1f + (difficulty - 1) * difficultyHealthGrowthPerLevel;
        _difficultyMoveSpeedScalar   = 1f + (difficulty - 1) * difficultyMoveSpeedGrowthPerLevel;
        _difficultyAttackSpeedScalar = 1f + (difficulty - 1) * difficultyAttackSpeedGrowthPerLevel;
        _currentDifficulty           = difficulty;

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
        // Spawn cadence no longer coop-scaled (2026-09-02, was `delta * coopScale`) — see coopScale's own
        // doc comment for why. Health/points scale by coopScale, XP stays flat (1f, 2026-09-03) — see
        // coopScale's own doc comment for why those diverge — damage stays flat too (1f).
        if (!bossAlive)
        {
            state.spawnTimer -= delta;
            if (state.spawnTimer <= 0f)
            {
                state.spawnTimer += state.spawnInterval;
                if (state.currentSetIndices.Count > 0 && EnemyBasePD.ActiveNormalCount < maxNormalEnemies)
                {
                    int pick = state.currentSetIndices[GameRandom.instance.Next(state.currentSetIndices.Count)];
                    if (tier.normal != null && pick < tier.normal.Length)
                        SpawnEnemy(tier.normal[pick], EnemySpawnCategory.Normal, coopScale, 1f, 1f, coopScale);
                }
            }
        }

        // ── Plebs ──────────────────────────────────────────────────────────
        // Keep spawning through a boss fight (at 0 XP, matching old's noXP), and — since the pre-boss
        // spawn blackout was removed — right up until the boss actually spawns too. Nothing left alive
        // when it does spawn either way, since every currently-alive enemy (plebs included) gets wiped
        // right before that happens below.
        //
        // Spawn cadence no longer coop-scaled (2026-09-02, was `delta * coopScale`) — see coopScale's own
        // doc comment for why. Health/points scale by coopScale (points still zeroed during a boss fight
        // regardless, per the comment above — coopScale only applies when it isn't) — XP stays flat
        // (2026-09-03, was coopScale) — damage stays flat too (1f).
        state.plebSpawnTimer -= delta;
        if (state.plebSpawnTimer <= 0f)
        {
            state.plebSpawnTimer += state.plebSpawnInterval;
            if (tier.pleb != null && tier.pleb.Length > 0)
            {
                for (int i = 0; i < plebSpawnAmount; i++)
                {
                    if (EnemyBasePD.ActivePlebCount >= maxPlebEnemies) break;
                    var prefab = tier.pleb[GameRandom.instance.Next(tier.pleb.Length)];
                    SpawnEnemy(prefab, EnemySpawnCategory.Pleb, coopScale, 1f, bossAlive ? 0f : 1f, bossAlive ? 0f : coopScale);
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
            SpawnBosses(ref state, tier, bossCoopScale, coopScale, bossSpawnCount);
        }
    }

    // Spawns `count` bosses from tier.boss, preferring DISTINCT prefabs — same "pool-and-swap-remove,
    // refill only once exhausted" shape RollNewSet() already uses for normal enemies (see its own doc
    // comment), just applied to tier.boss instead of tier.normal. Only ever repeats a prefab once count
    // exceeds how many distinct boss prefabs this tier actually has (e.g. count=2 against a 1-boss tier) —
    // "prefer non-duplicates, duplicate only if forced," per explicit design. Not itself threat-level-
    // specific — count is just a plain parameter, so any future N-simultaneous-boss design reuses this
    // unchanged (see state.activeBosses' own doc comment).
    //
    // Health and points deliberately take SEPARATE co-op scales now (2026-10-17 split) — bosses need a
    // steeper per-player health curve than every other category (see GameManagerPD.bossCoopToughnessScalar's
    // own doc comment), but points-per-player-count was never the part that felt wrong, so points stays on
    // the same shared coopScale every other category uses.
    private void SpawnBosses(ref State state, TierEnemies tier, float healthCoopScale, float pointsCoopScale, int count)
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

            var id = SpawnEnemy(prefab, EnemySpawnCategory.Boss, healthCoopScale, 1f, 1f, pointsCoopScale);
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
    // health/points by the same shared coopScale the caller already computed. XP is deliberately flat
    // (2026-09-03, was also coopScale — see coopScale's own doc comment for why XP no longer scales at
    // all: count is fixed here too, same as it's always been for elites, so flat XP is what keeps total
    // elite XP output equal between solo and co-op, not an inflate-then-cancel round-trip). Damage is
    // deliberately NOT coop-scaled either (2026-08-27 fix — was coopScale * eliteStatMultiplier before
    // this, an oversight; damage output shouldn't climb just because more players joined, matching
    // SpawnBosses' own identical health-and-points-only, damage-flat convention). Threat Level 3+
    // additionally doubles Health/Damage only (not XP/points — see threatLevel3EliteStatMultiplier's own
    // doc comment) — for damage this is now the ONLY multiplier applied, not stacked on top of coopScale
    // anymore.
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
                    coopScale * eliteStatMultiplier, eliteStatMultiplier, 1f, coopScale);
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

    // healthMultiplier/damageMultiplier/xpMultiplier/pointsMultiplier arriving here are each category's own
    // existing elite/co-op scalar (e.g. coopScale, or 1f where that category doesn't get one — see each
    // call site's own comment) — tier scaling multiplies on top of that, not the other way around (per
    // explicit design: tier is the base transformation every category shares, elite/co-op is layered on top
    // of whatever tier already produced). Folded in here, the one choke point every spawn call already goes
    // through, rather than touching each of the 4 call sites above individually.
    //
    // xpMultiplier and pointsMultiplier are genuinely separate parameters now (2026-09-03, was one shared
    // xpMultiplier reused for both) — every call site passes 1f for xpMultiplier (XP no longer scales with
    // co-op at all, see coopScale's own doc comment for why) and coopScale for pointsMultiplier (points
    // still does). Kept as two params rather than collapsed back down, even though several call sites still
    // pass the same category-gating value (e.g. Pleb's bossAlive-during-boss-fight zero) into both — that
    // shared gating is a coincidence of what each category needs today, not a reason to re-couple XP and
    // points at this choke point again.
    private PredictedObjectID? SpawnEnemy(GameObject prefab, EnemySpawnCategory category, float healthMultiplier, float damageMultiplier, float xpMultiplier, float pointsMultiplier)
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
                _tierPointsMultiplier * pointsMultiplier,
                moveSpeedMultiplier,
                attackCooldownMultiplier,
                // Tier * difficulty (2026-09-14 — tier component added; was difficulty-only). No per-category
                // bump, unlike move speed's own Threat Level 4 Pleb exception above — see attackSpeedTierGrowth/
                // difficultyAttackSpeedGrowthPerLevel's own doc comments.
                _tierAttackSpeedMultiplier * _difficultyAttackSpeedScalar,
                _currentTierNumber);
        }

        return id;
    }

    // Deliberately does NOT validate against PlayableAreaPD's boundary (2026-08-26 — a prior version of this
    // method rejected/clamped candidates outside it, based on a misdiagnosis of a corner-clustering bug).
    // Enemies are intentionally allowed to spawn beyond the boundary on purpose — so they walk in through
    // the wall instead of visibly popping into existence inside a player's view. The bounds-restriction/
    // strafe system this comment used to reference is gone entirely as of 2026-08-28 (see EnemyBasePD.
    // HandleMovement's own doc comment) — a spawned-outside enemy now just walks straight in like any other
    // movement, nothing left to trigger regardless of how far outside the boundary it currently is.
    // Requires >= 2 spawnPoints, always assigned in every current level (2026-09-22 — the old "ring around a
    // random living player" fallback for < 2 points was removed entirely, not fixed in place: it picked its
    // anchor player via CharacterStatsPD.All.Values enumeration order, a plain Dictionary with no ordering
    // guarantee across machines — the exact same bug CLASS already confirmed live once in this codebase (see
    // OrbiterGloomPD.TryStartGrab's own doc comment on why it sorts by ObjectId instead of trusting raw
    // HashSet enumeration order). A divergent anchor pick here means a divergent spawn position, which means
    // a divergent predicted enemy — precisely the kind of misprediction that forces PredictedHierarchy to
    // reconcile, per project_spikeshield_instance_id_collision_bug. Since every real level already assigns
    // spawnPoints, this was dead-in-practice risk, not a needed feature — simplest fix is removing it, not
    // trying to make it deterministic. transform.position is still a defensive last resort if spawnPoints
    // somehow ends up unassigned, so this can never throw.
    private Vector3 GetSpawnPosition()
    {
        if (spawnPoints == null || spawnPoints.Length < 2) return transform.position;

        int i = GameRandom.instance.Next(spawnPoints.Length);
        int j = (i + 1) % spawnPoints.Length;
        return Vector3.Lerp(spawnPoints[i].position, spawnPoints[j].position, GameRandom.instance.NextFloat(0f, 1f));
    }
}
