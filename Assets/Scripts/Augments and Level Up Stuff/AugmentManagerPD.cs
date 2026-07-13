using System.Collections.Generic;
using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public struct PlayerPick
{
    public PlayerID player;
    // Resolved to actual StatAugmentDatabase indices at submission time (not a raw card index looked up
    // later) — _currentOptions gets overwritten the moment the next round starts, and verified-state
    // processing in UpdateView() can lag behind the predicted simulation by several ticks, so a stored
    // card index could end up resolved against the wrong round's options. Baking the resolved indices
    // into predicted State instead sidesteps that race entirely.
    public int statIndexA;
    public int statIndexB;
    // The picking player's own meta-progression multiplier, captured client-side into
    // PlayerControllerPD.Input at pick time — never recomputed locally, since a remote machine's own
    // ProgressionManager save data would give a different (wrong) answer for another player's pick.
    public float multiplier;
}

public class AugmentManagerPD : PredictedIdentity<AugmentManagerPD.State>
{
    public struct State : IPredictedData<State>
    {
        public int levelBeingProcessed;
        public bool isPresenting;
        public DisposableList<PlayerPick> picks;
        public void Dispose() => picks.Dispose();
    }

    public static AugmentManagerPD instance;

    [SerializeField] private StatAugmentDatabase statDatabase;

    // Predicted, not verified — safe to read from other identities' Simulate() (rolls back correctly).
    // Folded into PausePD.IsPaused so every existing pause check across the codebase automatically also
    // halts gameplay while a level-up needs picks, without touching each of those systems individually.
    public bool IsPresenting => currentState.isPresenting;

    // For view/UI purposes only — verified, so the level-up screen doesn't flicker on rollback.
    public bool VerifiedIsPresenting => verifiedState?.isPresenting ?? false;

    // Cached the moment each round's options are generated via GameRandom in Simulate(). Every machine
    // calls GameRandom in the exact same deterministic order, so every machine lands on the identical
    // result independently — this cache itself isn't networked, it's just reproducibly-identical local
    // storage for the UI to read. Regenerated fresh each round.
    private readonly Dictionary<PlayerID, List<(int idxA, int idxB)>> _currentOptions = new();
    private readonly List<PlayerID> _sortedPlayers = new();

    private bool _wasPresenting;
    private int _appliedPickCount;

    // Matches GetInitialState's levelBeingProcessed = 1, so this doesn't read as a 0 -> 1 "level up"
    // the moment the game starts.
    private int _lastAppliedLevel = 1;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
    }

    protected override State GetInitialState() => new()
    {
        levelBeingProcessed = 1,
        picks = DisposableList<PlayerPick>.Create(4),
    };

    // Barebones — every level-up is a stat-augment pick for now, no real augment pool yet.
    protected override void Simulate(ref State state, float delta)
    {
        if (GameManagerPD.instance == null) return;

        if (!state.isPresenting)
        {
            if (GameManagerPD.instance.Level <= state.levelBeingProcessed) return;

            state.levelBeingProcessed++;
            state.isPresenting = true;
            state.picks.Clear();
            GenerateOptionsForAllPlayers();
            return;
        }

        if (state.picks.Count < CharacterStatsPD.All.Count) return;

        state.isPresenting = false;
    }

    // Called from PlayerControllerPD.Simulate() when that player's pickPressed input fires.
    public void SubmitPick(PlayerID player, int cardIndex, float multiplier)
    {
        if (!currentState.isPresenting) return;

        for (int i = 0; i < currentState.picks.Count; i++)
            if (currentState.picks[i].player == player) return;

        if (!_currentOptions.TryGetValue(player, out var options)) return;
        if (cardIndex < 0 || cardIndex >= options.Count) return;

        var (idxA, idxB) = options[cardIndex];
        currentState.picks.Add(new PlayerPick { player = player, statIndexA = idxA, statIndexB = idxB, multiplier = multiplier });
    }

    public bool HasPlayerPicked(PlayerID player)
    {
        var v = verifiedState;
        if (!v.HasValue) return false;

        for (int i = 0; i < v.Value.picks.Count; i++)
            if (v.Value.picks[i].player == player) return true;
        return false;
    }

    // Materializes the cached indices into real StatAugmentPair objects for display — only meaningful
    // for the local machine's own player, but harmless to call for anyone.
    public List<StatAugmentPair> GetOptionsForPlayer(PlayerID player)
    {
        var result = new List<StatAugmentPair>();
        if (statDatabase == null || !_currentOptions.TryGetValue(player, out var opts)) return result;

        foreach (var (idxA, idxB) in opts)
            result.Add(new StatAugmentPair(statDatabase.stats[idxA], statDatabase.stats[idxB]));
        return result;
    }

    private void GenerateOptionsForAllPlayers()
    {
        _currentOptions.Clear();
        if (statDatabase == null || statDatabase.stats.Count < 2) return;

        // Sorted rather than iterating CharacterStatsPD.All directly — Dictionary iteration order isn't
        // guaranteed identical across machines, and GameRandom draws are order-sensitive (a different
        // iteration order hands different players different random numbers, which would desync).
        _sortedPlayers.Clear();
        _sortedPlayers.AddRange(CharacterStatsPD.All.Keys);
        _sortedPlayers.Sort((a, b) => a.id.value.CompareTo(b.id.value));

        foreach (var player in _sortedPlayers)
            _currentOptions[player] = GenerateOptionsForPlayer(CharacterStatsPD.All[player]);
    }

    private List<(int, int)> GenerateOptionsForPlayer(CharacterStatsPD stats)
    {
        var options = new List<(int, int)>();

        var validIndices = new List<int>();
        for (int i = 0; i < statDatabase.stats.Count; i++)
        {
            var entry = statDatabase.stats[i];
            if (entry.allowedClasses == null || entry.allowedClasses.Length == 0 || IsClassAllowed(entry, stats.characterClass))
                validIndices.Add(i);
        }
        if (validIndices.Count < 2) return options;

        int optionCount = Mathf.Max(1, stats.levelUpOptions);
        var usedPairs = new HashSet<(int, int)>();

        for (int i = 0; i < optionCount; i++)
        {
            int idxA = validIndices[GameRandom.instance.Next(validIndices.Count)];
            int idxB = idxA;
            int attempts = 0;
            while ((idxB == idxA || usedPairs.Contains((idxA, idxB)) || usedPairs.Contains((idxB, idxA))) && attempts < 20)
            {
                idxB = validIndices[GameRandom.instance.Next(validIndices.Count)];
                attempts++;
            }

            usedPairs.Add((idxA, idxB));
            options.Add((idxA, idxB));
        }

        return options;
    }

    // StatAugmentEntry.allowedClasses is typed as the shared CharacterClassID (not CharacterStatsPD's own
    // nested CharacterClass) so this data file doesn't drag either the old or new system in as a compile
    // dependency of the other — compared by ordinal since both declare the same five classes in order.
    private static bool IsClassAllowed(StatAugmentEntry entry, CharacterStatsPD.CharacterClass playerClass)
    {
        foreach (var c in entry.allowedClasses)
            if ((int)c == (int)playerClass) return true;
        return false;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!verified.HasValue) return;
        var v = verified.Value;

        // Unconditional per-level bonus, independent of picks — looped in case a big XP orb crosses
        // multiple level thresholds in one tick.
        while (v.levelBeingProcessed > _lastAppliedLevel)
        {
            _lastAppliedLevel++;
            foreach (var stats in CharacterStatsPD.All.Values)
                stats.OnLevelUp();
        }

        if (v.isPresenting && !_wasPresenting)
            _appliedPickCount = 0;

        // Applies each newly-verified pick's stat bonuses exactly once, off confirmed state only — bonus
        // fields on CharacterStatsPD aren't part of predicted State and can't roll back on replay.
        for (int i = _appliedPickCount; i < v.picks.Count; i++)
            ApplyPick(v.picks[i]);
        _appliedPickCount = v.picks.Count;

        _wasPresenting = v.isPresenting;
    }

    private void ApplyPick(PlayerPick pick)
    {
        if (!CharacterStatsPD.All.TryGetValue(pick.player, out var stats)) return;
        if (statDatabase == null) return;
        if (pick.statIndexA < 0 || pick.statIndexA >= statDatabase.stats.Count) return;
        if (pick.statIndexB < 0 || pick.statIndexB >= statDatabase.stats.Count) return;

        stats.ApplyStatAugmentPick(statDatabase.stats[pick.statIndexA], statDatabase.stats[pick.statIndexB], pick.multiplier);
    }
}
