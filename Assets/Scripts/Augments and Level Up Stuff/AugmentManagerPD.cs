using System.Collections.Generic;
using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public struct PlayerPick
{
    public PlayerID player;
    public bool isAugment;
    // Meaningful when isAugment is true — index into AugmentManagerPD's augmentDatabase.
    public int augmentIndex;
    // Meaningful when isAugment is false.
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

// Whole-run record of which augments a player has picked — never cleared (unlike picks, which only
// tracks the current round). Used to avoid re-offering an already-picked augment and to evaluate
// prerequisites/incompatibilities for future offers.
public struct PlayerAugmentRecord
{
    public PlayerID player;
    public int augmentIndex;
}

public class AugmentManagerPD : PredictedIdentity<AugmentManagerPD.State>
{
    public static event System.Action<int> OnPresentingTimerChanged;


    public struct State : IPredictedData<State>
    {
        public int levelBeingProcessed;
        public bool isPresenting;
        public float presentingTimer;
        public DisposableList<PlayerPick> picks;
        public DisposableList<PlayerAugmentRecord> pickedAugments;
        public void Dispose()
        {
            picks.Dispose();
            pickedAugments.Dispose();
        }
    }

    // A single option slot generated for a player this round — resolved once, cached, never mutated.
    private readonly struct PendingOption
    {
        public readonly bool isAugment;
        public readonly int augmentIndex;
        public readonly int statIdxA;
        public readonly int statIdxB;

        public PendingOption(int augmentIndex)
        {
            isAugment = true;
            this.augmentIndex = augmentIndex;
            statIdxA = -1;
            statIdxB = -1;
        }

        public PendingOption(int statIdxA, int statIdxB)
        {
            isAugment = false;
            augmentIndex = -1;
            this.statIdxA = statIdxA;
            this.statIdxB = statIdxB;
        }
    }

    public static AugmentManagerPD instance;

    [SerializeField] private StatAugmentDatabase statDatabase;
    [SerializeField] private AugmentDatabase augmentDatabase;

    [Header("SFX")]
    [SerializeField] private AudioClip levelUpSfx;

    // Real augments are offered every Nth level; every other level is stat-only. If the augment pool
    // doesn't have enough available augments to fill every option slot (prerequisites unmet, already
    // picked, class-restricted, etc.), remaining slots fall back to stat pairs instead of coming up short.
    [SerializeField] private int augmentLevelInterval = 5;

    // Once this round's screen has been up this long, any player who still hasn't picked gets a random
    // one auto-selected for them (see AutoPick) — same reasoning as PausePD's ready-up fallback: nobody
    // should be able to hold the whole group hostage indefinitely.
    private const float PresentingDuration = 30f;

    // Predicted, not verified — safe to read from other identities' Simulate() (rolls back correctly).
    // Folded into PausePD.IsPaused so every existing pause check across the codebase automatically also
    // halts gameplay while a level-up needs picks, without touching each of those systems individually.
    public bool IsPresenting => currentState.isPresenting;

    public bool HasPresentingTimerExpired => currentState.isPresenting && currentState.presentingTimer <= 0f;

    // For view/UI purposes only — verified, so the level-up screen doesn't flicker on rollback.
    public bool VerifiedIsPresenting => verifiedState?.isPresenting ?? false;

    // Cached the moment each round's options are generated via GameRandom in Simulate(). Every machine
    // calls GameRandom in the exact same deterministic order, so every machine lands on the identical
    // result independently — this cache itself isn't networked, it's just reproducibly-identical local
    // storage for the UI to read. Regenerated fresh each round.
    private readonly Dictionary<PlayerID, List<PendingOption>> _currentOptions = new();
    private readonly List<PlayerID> _sortedPlayers = new();
    private readonly List<int> _availableAugmentsBuffer = new();

    private bool _wasPresenting;
    private int _appliedPickCount;
    private int _prevTimerSecs = -1;

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
        pickedAugments = DisposableList<PlayerAugmentRecord>.Create(8),
    };

    protected override void Simulate(ref State state, float delta)
    {
        if (GameManagerPD.instance == null) return;

        if (!state.isPresenting)
        {
            if (GameManagerPD.instance.Level <= state.levelBeingProcessed) return;

            state.levelBeingProcessed++;
            state.isPresenting = true;
            state.presentingTimer = PresentingDuration;
            state.picks.Clear();
            GenerateOptionsForAllPlayers();
            return;
        }

        if (state.picks.Count < CharacterStatsPD.All.Count)
        {
            state.presentingTimer = Mathf.Max(0f, state.presentingTimer - delta);
            return;
        }

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

        var option = options[cardIndex];
        if (option.isAugment)
        {
            currentState.picks.Add(new PlayerPick { player = player, isAugment = true, augmentIndex = option.augmentIndex });
            // Recorded here, not in ApplyPick()/UpdateView() — this runs from Simulate(), the only place
            // predicted State can be safely mutated, atomically with the pick itself.
            currentState.pickedAugments.Add(new PlayerAugmentRecord { player = player, augmentIndex = option.augmentIndex });
            SpawnAugmentEffect(player, option.augmentIndex);
        }
        else
        {
            currentState.picks.Add(new PlayerPick
            {
                player = player,
                isAugment = false,
                statIndexA = option.statIdxA,
                statIndexB = option.statIdxB,
                multiplier = multiplier
            });
        }
    }

    // Called from PlayerControllerPD.Simulate() once the presenting timer has expired and this player
    // still hasn't picked. Rolls a random index against THIS player's own already-generated options
    // (_currentOptions), same set SubmitPick validates cardIndex against — never against the full stat/
    // augment pool, so an auto-pick can only ever land on something this player was actually shown.
    // Re-checks "already picked" itself (SubmitPick does too) so a expired-timer tick that keeps calling
    // this every frame doesn't keep burning GameRandom rolls after the first one already landed.
    public void AutoPick(PlayerID player, float multiplier)
    {
        if (!currentState.isPresenting) return;
        if (!_currentOptions.TryGetValue(player, out var options) || options.Count == 0) return;

        for (int i = 0; i < currentState.picks.Count; i++)
            if (currentState.picks[i].player == player) return;

        int randomIndex = GameRandom.instance.Next(options.Count);
        SubmitPick(player, randomIndex, multiplier);
    }

    // hierarchy.Create() (not plain Instantiate()) — deterministic and idempotent under replay (resolves
    // to the same physical instance if this tick ever gets re-simulated), the same reason enemies/orbs/
    // weapons all spawn this way. This is what actually makes it safe to call from Simulate()-context —
    // the effect's own one-shot bonus (AugmentEffectPD.OnApply) still only fires off its verified state.
    private void SpawnAugmentEffect(PlayerID player, int augmentIndex)
    {
        if (augmentDatabase == null || augmentIndex < 0 || augmentIndex >= augmentDatabase.augments.Count) return;
        var augment = augmentDatabase.augments[augmentIndex];
        if (augment == null || augment.effectPrefab == null) return;
        if (!CharacterStatsPD.All.TryGetValue(player, out var stats)) return;

        hierarchy.Create(augment.effectPrefab, stats.transform.position, stats.transform.rotation, player);
    }

    public bool HasPlayerPicked(PlayerID player)
    {
        var v = verifiedState;
        if (!v.HasValue) return false;

        for (int i = 0; i < v.Value.picks.Count; i++)
            if (v.Value.picks[i].player == player) return true;
        return false;
    }

    // Materializes the cached options into display-ready objects — only meaningful for the local
    // machine's own player, but harmless to call for anyone.
    public List<LevelUpOption> GetOptionsForPlayer(PlayerID player)
    {
        var result = new List<LevelUpOption>();
        if (!_currentOptions.TryGetValue(player, out var opts)) return result;

        foreach (var option in opts)
        {
            if (option.isAugment)
            {
                if (augmentDatabase != null && option.augmentIndex >= 0 && option.augmentIndex < augmentDatabase.augments.Count)
                    result.Add(LevelUpOption.FromAugment(augmentDatabase.augments[option.augmentIndex]));
            }
            else if (statDatabase != null)
            {
                result.Add(LevelUpOption.FromStatPair(new StatAugmentPair(statDatabase.stats[option.statIdxA], statDatabase.stats[option.statIdxB])));
            }
        }
        return result;
    }

    private void GenerateOptionsForAllPlayers()
    {
        _currentOptions.Clear();

        bool isAugmentLevel = currentState.levelBeingProcessed % augmentLevelInterval == 0;

        // Sorted rather than iterating CharacterStatsPD.All directly — Dictionary iteration order isn't
        // guaranteed identical across machines, and GameRandom draws are order-sensitive (a different
        // iteration order hands different players different random numbers, which would desync).
        _sortedPlayers.Clear();
        _sortedPlayers.AddRange(CharacterStatsPD.All.Keys);
        _sortedPlayers.Sort((a, b) => a.id.value.CompareTo(b.id.value));

        foreach (var player in _sortedPlayers)
            _currentOptions[player] = GenerateOptionsForPlayer(CharacterStatsPD.All[player], player, isAugmentLevel);
    }

    private List<PendingOption> GenerateOptionsForPlayer(CharacterStatsPD stats, PlayerID player, bool isAugmentLevel)
    {
        var options = new List<PendingOption>();
        int optionCount = Mathf.Max(1, stats.levelUpOptions);

        if (isAugmentLevel && augmentDatabase != null)
        {
            GetAvailableAugmentIndices(stats.characterClass, player, _availableAugmentsBuffer);

            int drawCount = Mathf.Min(optionCount, _availableAugmentsBuffer.Count);
            var drawnSlots = new HashSet<int>();
            for (int i = 0; i < drawCount; i++)
            {
                int slot, attempts = 0;
                do { slot = GameRandom.instance.Next(_availableAugmentsBuffer.Count); attempts++; }
                while (drawnSlots.Contains(slot) && attempts < 20);
                drawnSlots.Add(slot);
                options.Add(new PendingOption(_availableAugmentsBuffer[slot]));
            }
        }

        // Fills remaining slots with stat pairs — covers both a non-augment level and an augment level
        // where the pool didn't have enough available augments to fill every option. No per-class
        // filtering here — every stat is available to every class.
        if (options.Count < optionCount && statDatabase != null && statDatabase.stats.Count >= 2)
        {
            int statCount = statDatabase.stats.Count;
            var usedPairs = new HashSet<(int, int)>();
            while (options.Count < optionCount)
            {
                int idxA = GameRandom.instance.Next(statCount);
                int idxB = idxA;
                int attempts = 0;
                while ((idxB == idxA || usedPairs.Contains((idxA, idxB)) || usedPairs.Contains((idxB, idxA))) && attempts < 20)
                {
                    idxB = GameRandom.instance.Next(statCount);
                    attempts++;
                }

                usedPairs.Add((idxA, idxB));
                options.Add(new PendingOption(idxA, idxB));
            }
        }

        return options;
    }

    private void GetAvailableAugmentIndices(CharacterStatsPD.CharacterClass playerClass, PlayerID player, List<int> result)
    {
        result.Clear();
        bool isMultiplayer = NetworkManager.main != null && NetworkManager.main.playerCount > 1;
        var classId = (CharacterClassID)(int)playerClass;

        for (int i = 0; i < augmentDatabase.augments.Count; i++)
        {
            var augment = augmentDatabase.augments[i];
            if (augment == null) continue;
            if (augment.multiplayerOnly && !isMultiplayer) continue;
            if (!augment.IsAvailableForClass(classId)) continue;
            if (HasPickedAugment(player, i)) continue;
            if (!ArePrerequisitesMet(augment, player)) continue;
            if (IsIncompatibleWithPicked(augment, player)) continue;

            result.Add(i);
        }
    }

    private bool HasPickedAugment(PlayerID player, int augmentIndex)
    {
        for (int i = 0; i < currentState.pickedAugments.Count; i++)
        {
            var rec = currentState.pickedAugments[i];
            if (rec.player == player && rec.augmentIndex == augmentIndex) return true;
        }
        return false;
    }

    // requiredCount of augment.requiredAugments must already be picked — not necessarily all of them.
    // Clamped here (not just trusted from the data asset) so an unset requiredCount (defaults to
    // int.MaxValue) naturally resolves to "all required" rather than needing a separate sentinel check.
    private bool ArePrerequisitesMet(AugmentData augment, PlayerID player)
    {
        if (augment.requiredAugments == null || augment.requiredAugments.Length == 0) return true;

        int metCount = 0;
        foreach (var req in augment.requiredAugments)
        {
            int reqIdx = augmentDatabase.augments.IndexOf(req);
            if (reqIdx >= 0 && HasPickedAugment(player, reqIdx)) metCount++;
        }

        int needed = Mathf.Clamp(augment.requiredCount, 0, augment.requiredAugments.Length);
        return metCount >= needed;
    }

    private bool IsIncompatibleWithPicked(AugmentData augment, PlayerID player)
    {
        if (augment.incompatibleAugments != null)
        {
            foreach (var incompatible in augment.incompatibleAugments)
            {
                int idx = augmentDatabase.augments.IndexOf(incompatible);
                if (idx >= 0 && HasPickedAugment(player, idx)) return true;
            }
        }

        for (int i = 0; i < currentState.pickedAugments.Count; i++)
        {
            var rec = currentState.pickedAugments[i];
            if (rec.player != player) continue;

            var pickedAugment = augmentDatabase.augments[rec.augmentIndex];
            if (pickedAugment == null || pickedAugment.incompatibleAugments == null) continue;
            if (System.Array.IndexOf(pickedAugment.incompatibleAugments, augment) >= 0) return true;
        }

        return false;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!verified.HasValue) return;
        var v = verified.Value;

        // Verified, not predicted — matches the level-up screen's own visibility (VerifiedIsPresenting),
        // so the countdown doesn't visually disagree with the cards/background it's displayed alongside.
        int timerSecs = v.isPresenting ? Mathf.CeilToInt(v.presentingTimer) : 0;
        if (timerSecs != _prevTimerSecs)
        {
            _prevTimerSecs = timerSecs;
            OnPresentingTimerChanged?.Invoke(timerSecs);
        }

        // Unconditional per-level bonus, independent of picks — looped in case a big XP orb crosses
        // multiple level thresholds in one tick.
        while (v.levelBeingProcessed > _lastAppliedLevel)
        {
            _lastAppliedLevel++;
            foreach (var stats in CharacterStatsPD.All.Values)
                stats.OnLevelUp();
        }

        if (v.isPresenting && !_wasPresenting)
        {
            _appliedPickCount = 0;
            // Fires once per round, right as the cards start appearing — this is the same verified-only
            // "just started presenting" transition VerifiedIsPresenting/the level-up screen itself already
            // key off, so the timing matches what's on screen. Played at the camera/listener position
            // rather than any player's transform since every machine's own UpdateView() call here is
            // already local to that machine — no positional/multiplayer-visibility concern for a UI cue.
            if (levelUpSfx != null && SFXManager.instance != null)
            {
                var listenerPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                SFXManager.instance.Play(levelUpSfx, listenerPos);
            }
        }

        // Applies each newly-verified stat pick exactly once, off confirmed state only — bonus fields on
        // CharacterStatsPD aren't part of predicted State and can't roll back on replay. Augment picks
        // need no handling here: their effect object was already spawned deterministically in
        // SubmitPick() via hierarchy.Create(), and it applies its own bonus off its own verified state.
        for (int i = _appliedPickCount; i < v.picks.Count; i++)
            ApplyPick(v.picks[i]);
        _appliedPickCount = v.picks.Count;

        _wasPresenting = v.isPresenting;
    }

    private void ApplyPick(PlayerPick pick)
    {
        if (pick.isAugment) return;

        if (!CharacterStatsPD.All.TryGetValue(pick.player, out var stats)) return;
        if (statDatabase == null) return;
        if (pick.statIndexA < 0 || pick.statIndexA >= statDatabase.stats.Count) return;
        if (pick.statIndexB < 0 || pick.statIndexB >= statDatabase.stats.Count) return;

        stats.ApplyStatAugmentPick(statDatabase.stats[pick.statIndexA], statDatabase.stats[pick.statIndexB], pick.multiplier);
    }
}
