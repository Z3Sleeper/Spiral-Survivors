using System.Collections.Generic;
using PurrNet;
using PurrNet.Packing;
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
    // LuckRolledLevelUpStatsAugment's own roll (2026-08-19) — same "captured client-side at pick time, never
    // recomputed here" trust as multiplier, but for a genuinely random roll. Both stats on a card always
    // share ONE roll (not independent per-stat rolls) — these two fields carry the identical value, kept
    // separate only to mirror ApplyStatAugmentPick's existing entryA/entryB split. Manual picks:
    // AugmentCardUI rolls once at card-display time via UnityEngine.Random (not the shared GameRandom
    // stream) and caches it, so what's captured here is exactly what the card showed. Auto-picks: AutoPick
    // rolls once via GameRandom instead, since that runs from Simulate()-context. Both default to 1f
    // (neutral) for a player without the augment.
    public float luckMultiplierA;
    public float luckMultiplierB;
    // Which round (State.levelBeingProcessed) this pick was submitted during — picks are whole-run, never
    // cleared (see State.picks' own doc comment for why), so anything that used to rely on the list being
    // freshly empty each round instead filters by this.
    public int roundLevel;
}

// Whole-run record of which augments a player has picked — never cleared, same lifetime as picks now.
// Used to avoid re-offering an already-picked augment and to evaluate prerequisites/incompatibilities for
// future offers.
public struct PlayerAugmentRecord
{
    public PlayerID player;
    public int augmentIndex;
}

public class AugmentManagerPD : PredictedIdentity<AugmentManagerPD.State>
{
    // (seconds, visible) — same shape as PausePD.OnFallbackTimerChanged. visible is false whenever solo
    // (see IsSinglePlayer), so the UI can hide the countdown text entirely instead of showing a number
    // nobody's actually counting down against.
    public static event System.Action<int, bool> OnPresentingTimerChanged;


    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public int levelBeingProcessed;
        public bool isPresenting;
        public float presentingTimer;
        // Mid-cycle event scaffolding (2026-08-24) — which round (levelBeingProcessed for a real level-up,
        // or a negative nextBonusRoundId-issued value for an event-triggered bonus round) is actually being
        // presented right now. Every pick-bookkeeping site (SubmitPick/AutoPick/HasPlayerPicked/
        // CountPicksThisRound/CountExpectedPickEffects) keys off THIS instead of levelBeingProcessed
        // directly, so a bonus round's own picks can never collide with a real round's — real round ids are
        // always >= 1 (see GetInitialState's own levelBeingProcessed = 1), bonus round ids are always <= -1,
        // disjoint by construction. Deliberately NOT used by the "Level <= levelBeingProcessed" trigger gate
        // in Simulate() or UpdateView's per-level OnLevelUp() stat-bonus loop — those two keep reading
        // levelBeingProcessed directly, completely untouched by bonus rounds. That's the whole point of this
        // split: a bonus round must never advance real progression or grant the passive per-level stat bonus,
        // only the pick itself — see TriggerBonusAugmentRound's own doc comment.
        public int activeRoundId;
        // Next id a bonus round will be issued — starts at -1, decrements every bonus round. Never touched
        // by real rounds.
        public int nextBonusRoundId;
        // Whole-run record of every pick ever submitted (augment AND stat), never cleared — same
        // reasoning as pickedAugments below. Used to be cleared at the start of every round, which broke a
        // freshly-spawned local replica's ability to rebuild historical state (a reconnecting player's own
        // client, or any newly-joining observer): UpdateView()'s _appliedPickCount cursor can only recover
        // picks that are still actually present in this list, and a brand-new replica starts that cursor at
        // 0, so anything already cleared by the time it starts observing was permanently invisible to it.
        // Every per-round check below (SubmitPick/AutoPick's "already picked", Simulate()'s "has everyone
        // picked", CountExpectedPickEffects) now filters by PlayerPick.roundLevel instead of relying on the
        // list being freshly empty.
        public DisposableList<PlayerPick> picks;
        public DisposableList<PlayerAugmentRecord> pickedAugments;
        public void Dispose()
        {
            picks.Dispose();
            pickedAugments.Dispose();
        }

        // Was missing entirely — every rollback snapshot fell back to the slow serialize/deserialize
        // round-trip instead of this cheap direct field-copy. See EnemyBaseState.Duplicate's own doc
        // comment for the full reasoning.
        public State Duplicate()
        {
            var copy = this;
            copy.picks = picks.Duplicate();
            copy.pickedAugments = pickedAugments.Duplicate();
            return copy;
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

    // Live spawned effect instances, keyed by (player, augment) — populated the moment SpawnAugmentEffect
    // actually creates one (2026-08-19). Lets AugmentDescriptionFormatter resolve a description token
    // against the real per-player instance instead of always falling back to the dead effectPrefab template
    // — see that class's own Format() doc comment for why that matters (a token method reading its own
    // instance state, e.g. a stacking augment's current stack count, would otherwise always see the
    // prefab's zeroed default, never the real player's). Plain, not predicted State — a cache mirroring
    // hierarchy.Create()'s own already-deterministic/idempotent spawn (see AugmentEffectPD's own doc
    // comment), not new gameplay state itself. Never pruned — augment effects are never despawned mid-match
    // (nothing anywhere calls hierarchy.Delete on one), so this only ever grows, same non-pruning already
    // accepted elsewhere for other whole-match-lifetime caches.
    private readonly Dictionary<(PlayerID, AugmentData), IAugmentEffectDescribable> _spawnedEffects = new();

    [SerializeField] private StatAugmentDatabase statDatabase;
    [SerializeField] private AugmentDatabase augmentDatabase;
    // Scaffolding for the unlockable-augment-sets system — see AugmentData.unlockTag/AugmentUnlockTagDatabase's
    // own doc comments. Public accessor so CharacterStatsPD.ComputeUnlockedAugmentTagMask (a static method,
    // can't reach this Inspector-wired field any other way) can resolve tag->bit at Input-compute time.
    [SerializeField] private AugmentUnlockTagDatabase unlockTagDatabase;
    public AugmentUnlockTagDatabase UnlockTagDatabase => unlockTagDatabase;

    [Header("SFX")]
    [SerializeField] private AudioClip levelUpSfx;

    // Real augments are offered every Nth level; every other level is stat-only. If the augment pool
    // doesn't have enough available augments to fill every option slot (prerequisites unmet, already
    // picked, class-restricted, etc.), remaining slots fall back to stat pairs instead of coming up short.
    [SerializeField] private int augmentLevelInterval = 3;

    // Once this round's screen has been up this long, any player who still hasn't picked gets a random
    // one auto-selected for them (see AutoPick) — same reasoning as PausePD's ready-up fallback: nobody
    // should be able to hold the whole group hostage indefinitely. Exposed (not const) so it can be tuned
    // down in the Inspector for local multiplayer testing.
    [SerializeField] private float presentingDuration = 30f;

    // Predicted, not verified — safe to read from other identities' Simulate() (rolls back correctly).
    // Folded into PausePD.IsPaused so every existing pause check across the codebase automatically also
    // halts gameplay while a level-up needs picks, without touching each of those systems individually.
    public bool IsPresenting => currentState.isPresenting;

    // Same live-playercount convention PausePD's own isSinglePlayer check uses — a solo player should get
    // unlimited time to pick, no countdown, and never get auto-picked for. Live, not GameManagerPD's fixed-
    // at-start count, so a multiplayer match that drops down to one remaining player also stops timing out
    // from that point on, matching how PausePD's own ready-up/toggle logic already treats it.
    private bool IsSinglePlayer => NetworkManager.main == null || NetworkManager.main.playerCount <= 1;

    public bool HasPresentingTimerExpired => currentState.isPresenting && !IsSinglePlayer && currentState.presentingTimer <= 0f;

    // For view/UI purposes only — verified, so the level-up screen doesn't flicker on rollback.
    public bool VerifiedIsPresenting => verifiedState?.isPresenting ?? false;

    // Copies into the caller's own buffer rather than exposing pickedAugments directly — same reasoning
    // as EnemySpawnerPD.GetVerifiedActiveBosses: DisposableList<T>'s backing List<T> is pooled and can be
    // reused/disposed as new verified snapshots arrive, so handing out a live reference would be unsafe to
    // hold across frames. Verified (not predicted) since this only ever backs a UI list a player reads by
    // hovering, not something needing instant reactivity — no harm in it lagging a tick behind a pick.
    public void GetPickedAugmentsForPlayer(PlayerID player, List<AugmentData> buffer)
    {
        buffer.Clear();
        if (!verifiedState.HasValue) return;
        var list = verifiedState.Value.pickedAugments;
        for (int i = 0; i < list.Count; i++)
        {
            var rec = list[i];
            if (rec.player != player) continue;
            if (rec.augmentIndex < 0 || rec.augmentIndex >= augmentDatabase.augments.Count) continue;
            var augment = augmentDatabase.augments[rec.augmentIndex];
            if (augment != null) buffer.Add(augment);
        }
    }

    // Cached the moment each round's options are generated via GameRandom in Simulate(). Every machine
    // calls GameRandom in the exact same deterministic order, so every machine lands on the identical
    // result independently — this cache itself isn't networked, it's just reproducibly-identical local
    // storage for the UI to read. Regenerated fresh each round.
    private readonly Dictionary<PlayerID, List<PendingOption>> _currentOptions = new();
    private readonly List<PlayerID> _sortedPlayers = new();
    private readonly List<int> _availableAugmentsBuffer = new();
    // Local skip-cache only, not predicted state — RegisterClassPassiveAugments() below still checks
    // HasPickedAugment() before ever adding a record, so even if this set ever diverges across machines
    // (it shouldn't — every machine iterates CharacterStatsPD.All in the same sorted order, same
    // determinism reasoning as GenerateOptionsForAllPlayers), the actual state mutation stays idempotent;
    // worst case is a redundant re-scan of an already-fully-registered player.
    private readonly HashSet<PlayerID> _registeredPassivePlayers = new();

    private bool _wasPresenting;
    private int _appliedPickCount;
    private int _prevTimerSecs = -1;
    private bool _prevTimerVisible;

    // Whole-run bookkeeping — the literal running total of every stat-augment delta each player has ever
    // been granted (see CharacterStatsPD.StatBoostTotals' own doc comment). Plain Dictionary, not predicted
    // State: mirrors CharacterStatsPD's own bonus fields, which this exists purely to record alongside —
    // those aren't part of predicted State either (can't roll back on replay, see ApplyPick's own comment),
    // so this is mutated the exact same way: once per verified pick, from UpdateView(), never from
    // Simulate(). Not reset mid-run — a fresh instance (this class's own Awake() singleton guard) already
    // starts empty for every new match.
    private readonly Dictionary<PlayerID, CharacterStatsPD.StatBoostTotals> _statTotals = new();

    // Copy, not a live reference — same "handed to a caller, safe to hold" reasoning as
    // GetPickedAugmentsForPlayer, though here it's just a struct copy rather than a pooled list.
    public CharacterStatsPD.StatBoostTotals GetStatTotals(PlayerID player) =>
        _statTotals.TryGetValue(player, out var t) ? t : default;

    // How many of this round's picked-augment effects have confirmed OnApply() so far — reset to 0 only at
    // round start (state.isPresenting false -> true, itself already rollback-safe), then only ever
    // incremented, never decremented, by NotifyAugmentEffectApplied(). Deliberately NOT "precompute the
    // expected count once, then count down" — that shape had a real race: a fast-confirming effect's
    // UpdateView() can call NotifyAugmentEffectApplied() before AugmentManagerPD's own Simulate() has next
    // run to even notice all picks are in (UpdateView() and Simulate() run on different cadences, and
    // there's no guaranteed ordering between this identity's Simulate() and the identity that just called
    // SubmitPick() within the same tick either) — so a confirmation could arrive while "expected" was still
    // an unset sentinel and get silently dropped, permanently stalling the round below its true target.
    // Always accumulating here instead, and comparing against a freshly re-derived expected count every
    // tick (CountExpectedPickEffects), means there's no ordering dependency and no confirmation can ever be
    // lost.
    private int _confirmedPickEffectsThisRound;

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
        // Matches levelBeingProcessed — meaningless until a round actually starts (nothing reads this while
        // !isPresenting), but kept in sync for tidiness.
        activeRoundId = 1,
        nextBonusRoundId = -1,
        picks = DisposableList<PlayerPick>.Create(4),
        pickedAugments = DisposableList<PlayerAugmentRecord>.Create(8),
    };

    protected override void Simulate(ref State state, float delta)
    {
        if (GameManagerPD.instance == null) return;

        // Win/Loss takes total priority over a level-up round in progress — per the user's own pause-
        // priority ordering (Win/Loss > Level Up > Pause), there's no reason to keep waiting on picks (or
        // generating new options) once the game has already ended. Also what stops isPresenting from
        // getting stuck true forever if the game ends mid-round, which would otherwise leave the level-up
        // screen (VerifiedIsPresenting-driven, in LocalUIManagerPD) shown on top of the end screen forever.
        if (GameManagerPD.instance.HasWon || GameManagerPD.instance.IsGameOver)
        {
            state.isPresenting = false;
            return;
        }

        RegisterClassPassiveAugments();

        if (!state.isPresenting)
        {
            if (GameManagerPD.instance.Level <= state.levelBeingProcessed) return;

            state.levelBeingProcessed++;
            state.activeRoundId = state.levelBeingProcessed;
            state.isPresenting = true;
            state.presentingTimer = presentingDuration;
            _confirmedPickEffectsThisRound = 0;
            GenerateOptionsForAllPlayers();
            return;
        }

        if (CountPicksThisRound(state.picks, state.activeRoundId) < CharacterStatsPD.All.Count)
        {
            // Doesn't decrement at all in single player — HasPresentingTimerExpired also independently
            // refuses to report expired while solo, but leaving the actual number frozen (not just ignored)
            // means it reads as genuinely infinite rather than a value nobody happens to be checking.
            if (!IsSinglePlayer) state.presentingTimer = Mathf.Max(0f, state.presentingTimer - delta);
            return;
        }

        // All picks are in — now make sure every picked augment that actually spawned an effect object has
        // confirmed its OnApply() before ending the round, same "wait for confirmation, not just spawn"
        // reasoning as RegisterClassPassiveAugments/CharacterStatsPD.ClassPassiveAugmentsReady. Without
        // this, PausePD.IsPaused (which folds in isPresenting) could unpause a tick or more before a picked
        // augment's stat bonus is actually live — the same visible "stat pops in after the fact" flash the
        // class-passive fix addressed, just for level-ups instead of game start. Re-derived fresh every
        // tick (cheap — a handful of picks at most) rather than cached, so there's nothing to get out of
        // sync with _confirmedPickEffectsThisRound.
        if (_confirmedPickEffectsThisRound < CountExpectedPickEffects(state.picks, state.activeRoundId)) return;

        state.isPresenting = false;
    }

    // Picks are whole-run now (see State.picks' own doc comment) — every per-round check re-derives its
    // own "this round only" view by filtering on PlayerPick.roundLevel instead of trusting Count directly.
    private int CountPicksThisRound(DisposableList<PlayerPick> picks, int roundLevel)
    {
        int count = 0;
        for (int i = 0; i < picks.Count; i++)
            if (picks[i].roundLevel == roundLevel) count++;
        return count;
    }

    // Pure, side-effect-free re-derivation from already-rolled-back-correct predicted state (state.picks)
    // plus static augmentDatabase data — safe to (re)compute every tick, including under resimulation,
    // since it never accumulates anything itself. Counts only picks that actually spawn a trackable effect
    // object (a pure stat pick, or an augment with no effectPrefab, has nothing to wait on).
    private int CountExpectedPickEffects(DisposableList<PlayerPick> picks, int roundLevel)
    {
        if (augmentDatabase == null) return 0;

        int count = 0;
        for (int i = 0; i < picks.Count; i++)
        {
            var pick = picks[i];
            if (pick.roundLevel != roundLevel) continue;
            if (!pick.isAugment) continue;
            if (pick.augmentIndex < 0 || pick.augmentIndex >= augmentDatabase.augments.Count) continue;

            var augment = augmentDatabase.augments[pick.augmentIndex];
            if (augment != null && augment.effectPrefab != null) count++;
        }
        return count;
    }

    // Called by every AugmentEffectPD once its own OnApply() has actually run (confirmed off verified
    // state) — mirrors CharacterStatsPD.NotifyAugmentApplied, just aggregated across all players for the
    // shared presenting-round gate above instead of scoped per-player. Always increments — a class-passive
    // confirmation (which always happens well before any round starts, and gets wiped by the next round's
    // own reset) or a stray call once a round has already fully resolved just nudges a number nobody's
    // comparing against anymore, harmless either way.
    public void NotifyAugmentEffectApplied()
    {
        _confirmedPickEffectsThisRound++;
    }

    // Registers each player's class passive augment(s) (ClassPassiveData.passiveAugments) as real
    // PlayerAugmentRecords the moment their CharacterStatsPD becomes available — same
    // add-record-then-spawn-effect atomicity SubmitPick() uses for a manual pick, just triggered by "this
    // player now exists with a resolved class passive" instead of "this player picked a card". This is
    // what makes the pick-pool machinery (HasPickedAugment/IsIncompatibleWithPicked) treat a class passive
    // exactly like a real pick for free — it won't be re-offered, and anything incompatible with it (or
    // that it's incompatible with) gets excluded too.
    //
    // Sorted iteration, same determinism reasoning as GenerateOptionsForAllPlayers — every machine must
    // process players in the same order since this calls hierarchy.Create() (via SpawnAugmentEffect).
    // CharacterStatsPD.All is populated from PlayerDataPD.UpdateView(), which runs AFTER this tick's
    // Simulate() pass — so a just-registered player is picked up starting next tick, a harmless one-tick
    // lag (the exact same lag GenerateOptionsForAllPlayers already tolerates reading the same dictionary).
    private void RegisterClassPassiveAugments()
    {
        if (augmentDatabase == null) return;

        _sortedPlayers.Clear();
        _sortedPlayers.AddRange(CharacterStatsPD.All.Keys);
        _sortedPlayers.Sort((a, b) => a.id.value.CompareTo(b.id.value));

        foreach (var player in _sortedPlayers)
        {
            if (_registeredPassivePlayers.Contains(player)) continue;

            var stats = CharacterStatsPD.All[player];
            var passives = stats.ClassPassiveAugments;
            int spawnedCount = 0;
            if (passives != null)
            {
                foreach (var augment in passives)
                {
                    if (augment == null) continue;
                    int idx = augmentDatabase.augments.IndexOf(augment);
                    if (idx < 0 || HasPickedAugment(player, idx)) continue;

                    currentState.pickedAugments.Add(new PlayerAugmentRecord { player = player, augmentIndex = idx });
                    if (SpawnAugmentEffect(player, idx)) spawnedCount++;
                }
            }
            // Tells CharacterStatsPD exactly how many OnApply() confirmations to wait for before it
            // reports ClassPassiveAugmentsReady — 0 immediately marks it ready if this class has none (or
            // none actually spawned an effect object).
            stats.BeginWaitingForClassPassiveAugments(spawnedCount);
            _registeredPassivePlayers.Add(player);
        }
    }

    // Called from PlayerControllerPD.Simulate() when that player's pickPressed input fires.
    public void SubmitPick(PlayerID player, int cardIndex, float multiplier, float luckMultiplierA = 1f, float luckMultiplierB = 1f)
    {
        if (!currentState.isPresenting) return;

        for (int i = 0; i < currentState.picks.Count; i++)
            if (currentState.picks[i].player == player && currentState.picks[i].roundLevel == currentState.activeRoundId) return;

        if (!_currentOptions.TryGetValue(player, out var options)) return;
        if (cardIndex < 0 || cardIndex >= options.Count) return;

        var option = options[cardIndex];
        if (option.isAugment)
        {
            currentState.picks.Add(new PlayerPick { player = player, isAugment = true, augmentIndex = option.augmentIndex, roundLevel = currentState.activeRoundId });
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
                multiplier = multiplier,
                luckMultiplierA = luckMultiplierA,
                luckMultiplierB = luckMultiplierB,
                roundLevel = currentState.activeRoundId
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
            if (currentState.picks[i].player == player && currentState.picks[i].roundLevel == currentState.activeRoundId) return;

        int randomIndex = GameRandom.instance.Next(options.Count);

        // Same "one shared roll per card" luck-roll shape as AugmentCardUI's manual-pick path, but sourced
        // from GameRandom instead of UnityEngine.Random — this runs from Simulate()-context (called from
        // PlayerControllerPD.Simulate()), so it must draw from the shared, deterministic, order-sensitive PD
        // stream like any other Simulate()-context random roll, not the local-only stream a client-side UI
        // preview needs.
        // HighrollBonus is scaled down (not passed raw) — see AugmentCardUI's own identical comment on why.
        var option = options[randomIndex];
        float luckMultiplier = (!option.isAugment && CharacterStatsPD.All.TryGetValue(player, out var stats) && stats.luckRolledLevelUpStats)
            ? WeaponDamageMath.RollLuckMultiplier(stats.LuckChance, stats.LuckFloor, stats.LuckCeiling, stats.Highroll, stats.HighrollBonus * stats.luckRolledLevelUpStatsHighrollScale, GameRandom.instance.NextFloat, out _)
            : 1f;

        SubmitPick(player, randomIndex, multiplier, luckMultiplier, luckMultiplier);
    }

    // Mid-cycle event scaffolding (2026-08-24) — called by EventManagerPD.ResolveEvent() when an event's
    // own Succeeded flag reads true. Reuses the exact same isPresenting/presentingTimer/picks-list/UI
    // machinery a real augment-level level-up round uses, but through activeRoundId's own separate
    // negative-id space (see its own doc comment) instead of levelBeingProcessed — genuinely additive, not a
    // real level-up: doesn't touch GameManagerPD.Level, doesn't pre-empt a future real round, doesn't grant
    // UpdateView's per-level OnLevelUp() stat bonus. forceAugmentLevel: true guarantees this always offers
    // real augment picks (never falls back to a stat-only round), matching "shows augment options like a
    // normal level up on an augment level would" per explicit design.
    //
    // Skips entirely (no queueing) if a round — real or bonus — is already presenting. Under current tuning
    // this can only happen from an astronomically unlikely same-tick collision (event resolution is timed
    // off EnemySpawnerPD's own Tier Cycle clock, real level-ups off XP — no structural reason they'd ever
    // land on the same tick), so a dropped bonus round in that case is an accepted gap, not something this
    // scaffolding builds a queue to prevent.
    public void TriggerBonusAugmentRound()
    {
        if (currentState.isPresenting) return;

        currentState.activeRoundId = currentState.nextBonusRoundId;
        currentState.nextBonusRoundId--;
        currentState.isPresenting = true;
        currentState.presentingTimer = presentingDuration;
        _confirmedPickEffectsThisRound = 0;
        GenerateOptionsForAllPlayers(forceAugmentLevel: true);
    }

    // hierarchy.Create() (not plain Instantiate()) — deterministic and idempotent under replay (resolves
    // to the same physical instance if this tick ever gets re-simulated), the same reason enemies/orbs/
    // weapons all spawn this way. This is what actually makes it safe to call from Simulate()-context —
    // the effect's own one-shot bonus (AugmentEffectPD.OnApply) still only fires off its verified state.
    // Returns whether a real effect object was actually spawned — RegisterClassPassiveAugments needs this
    // to know exactly how many OnApply() confirmations to wait for (some augments have no effectPrefab at
    // all, e.g. pure stat picks, and silently spawn nothing here).
    private bool SpawnAugmentEffect(PlayerID player, int augmentIndex)
    {
        if (augmentDatabase == null || augmentIndex < 0 || augmentIndex >= augmentDatabase.augments.Count) return false;
        var augment = augmentDatabase.augments[augmentIndex];
        if (augment == null || augment.effectPrefab == null) return false;
        if (!CharacterStatsPD.All.TryGetValue(player, out var stats)) return false;

        var id = hierarchy.Create(augment.effectPrefab, stats.transform.position, stats.transform.rotation, player);
        if (!id.HasValue) return false;

        // Registers the real spawned instance for AugmentDescriptionFormatter — see _spawnedEffects' own
        // doc comment. Same "mutate right after Create()" synchronous pattern every other spawned effect in
        // this codebase already uses.
        var go = hierarchy.GetGameObject(id.Value);
        if (go != null && go.TryGetComponent(out IAugmentEffectDescribable describable))
            _spawnedEffects[(player, augment)] = describable;

        return true;
    }

    // Read by AugmentDescriptionFormatter — see _spawnedEffects' own doc comment. Returns null if this
    // player hasn't actually picked/spawned this augment yet (a not-yet-picked level-up preview card), which
    // the formatter treats as "fall back to the prefab template" — exactly the right behavior for a preview.
    public IAugmentEffectDescribable GetSpawnedEffect(PlayerID player, AugmentData augment) =>
        _spawnedEffects.TryGetValue((player, augment), out var effect) ? effect : null;

    public bool HasPlayerPicked(PlayerID player)
    {
        var v = verifiedState;
        if (!v.HasValue) return false;

        // picks is whole-run now (see State.picks' own doc comment) — has to filter by the CURRENT round,
        // otherwise this would report "already picked" forever after a player's very first pick ever.
        for (int i = 0; i < v.Value.picks.Count; i++)
            if (v.Value.picks[i].player == player && v.Value.picks[i].roundLevel == v.Value.activeRoundId) return true;
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

    // forceAugmentLevel (2026-08-24) — TriggerBonusAugmentRound's own way of guaranteeing a bonus round
    // always offers real augment picks, bypassing the % augmentLevelInterval gate entirely (which only makes
    // sense for real, levelBeingProcessed-driven rounds).
    private void GenerateOptionsForAllPlayers(bool forceAugmentLevel = false)
    {
        _currentOptions.Clear();

        bool isAugmentLevel = forceAugmentLevel || currentState.levelBeingProcessed % augmentLevelInterval == 0;

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
        // Fixed at game start, not NetworkManager's live playerCount — see
        // GameManagerPD.State.fixedPlayerCount's own doc comment. A player disconnecting mid-run shouldn't
        // suddenly hide multiplayerOnly augments from (or reshuffle the pool for) whoever remains.
        bool isMultiplayer = GameManagerPD.instance != null && GameManagerPD.instance.FixedPlayerCount > 1;
        var classId = (CharacterClassID)(int)playerClass;

        for (int i = 0; i < augmentDatabase.augments.Count; i++)
        {
            var augment = augmentDatabase.augments[i];
            if (augment == null) continue;
            if (augment.multiplayerOnly && !isMultiplayer) continue;
            if (!augment.IsAvailableForClass(classId)) continue;
            if (!IsAugmentTagUnlocked(augment.unlockTag, player)) continue;
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

    // Both the specific-augment requirement (requiredCount/requiredAugments — use when THIS SPECIFIC
    // augment is required) and the tag-based requirement (requiredTags — use when "any augment with this
    // general capability" is required, e.g. "any OnHit-effect augment") must independently pass.
    private bool ArePrerequisitesMet(AugmentData augment, PlayerID player)
    {
        if (!IsRequirementGroupSatisfied(augment.requiredAugments, augment.requiredCount, player)) return false;
        if (!HasPickedAugmentForEachTag(augment.requiredTags, player)) return false;
        return true;
    }

    // requiredCount of requiredAugments must already be picked — not necessarily all of them. Clamped here
    // (not just trusted from the data asset) so an unset requiredCount (defaults to int.MaxValue) naturally
    // resolves to "all required" rather than needing a separate sentinel check.
    private bool IsRequirementGroupSatisfied(AugmentData[] requiredAugments, int requiredCount, PlayerID player)
    {
        if (requiredAugments == null || requiredAugments.Length == 0) return true;

        int metCount = 0;
        foreach (var req in requiredAugments)
        {
            int reqIdx = augmentDatabase.augments.IndexOf(req);
            if (reqIdx >= 0 && HasPickedAugment(player, reqIdx)) metCount++;
        }

        int needed = Mathf.Clamp(requiredCount, 0, requiredAugments.Length);
        return metCount >= needed;
    }

    // Scaffolding for the unlockable-augment-sets system — see AugmentData.unlockTag's own doc comment.
    // Empty/null tag (every augment today) is always eligible. A set tag requires the resolved bit to
    // actually be present in that player's own CharacterStatsPD.UnlockedAugmentTagMask — fails CLOSED
    // (ineligible) if the player's Stats aren't found, the tag isn't registered in unlockTagDatabase, or the
    // database itself isn't wired up, rather than risking a plumbing gap silently exposing deliberately-
    // gated content to everyone.
    private bool IsAugmentTagUnlocked(string tag, PlayerID player)
    {
        if (string.IsNullOrEmpty(tag)) return true;
        if (unlockTagDatabase == null) return false;
        if (!CharacterStatsPD.All.TryGetValue(player, out var stats)) return false;

        int bit = unlockTagDatabase.GetTagBit(tag);
        if (bit < 0 || bit >= 64) return false;
        return (stats.UnlockedAugmentTagMask & (1L << bit)) != 0L;
    }

    // Every tag must be carried by either an already-picked augment, or the player's own class ability
    // (abilities are "always picked" from match start — no augment card needed) — matched by exact string
    // equality against AugmentData.tags/IAbilityPD.Tags, not tied to any specific augment by name, so a new
    // augment (or ability) carrying an existing tag automatically satisfies any requirement for that tag
    // without needing to be individually added anywhere.
    private bool HasPickedAugmentForEachTag(string[] requiredTags, PlayerID player)
    {
        if (requiredTags == null || requiredTags.Length == 0) return true;

        // GetComponents, not GetComponent — a character can carry both an Ability1-slot and an Ability2-slot
        // component (e.g. Rogue's RogueSpinAbilityPD + RogueVanishAbilityPD), and a plain single
        // GetComponent would only ever surface one of them, silently missing whichever one actually carries
        // the tag.
        IAbilityPD[] abilities = null;
        if (CharacterStatsPD.All.TryGetValue(player, out var stats))
            abilities = stats.GetComponents<IAbilityPD>();

        bool HasAbilityTag(string tag)
        {
            if (abilities == null) return false;
            foreach (var ability in abilities)
                if (ability?.Tags != null && System.Array.IndexOf(ability.Tags, tag) >= 0) return true;
            return false;
        }

        foreach (var tag in requiredTags)
        {
            if (HasAbilityTag(tag)) continue;

            bool found = false;
            for (int i = 0; i < currentState.pickedAugments.Count && !found; i++)
            {
                var rec = currentState.pickedAugments[i];
                if (rec.player != player) continue;
                if (rec.augmentIndex < 0 || rec.augmentIndex >= augmentDatabase.augments.Count) continue;

                var picked = augmentDatabase.augments[rec.augmentIndex];
                if (picked.tags != null && System.Array.IndexOf(picked.tags, tag) >= 0) found = true;
            }
            if (!found) return false;
        }

        return true;
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
            if (pickedAugment == null) continue;

            if (pickedAugment.incompatibleAugments != null && System.Array.IndexOf(pickedAugment.incompatibleAugments, augment) >= 0) return true;

            if (HasSharedTag(augment.incompatibleTags, pickedAugment.tags)) return true;
            if (HasSharedTag(pickedAugment.incompatibleTags, augment.tags)) return true;
        }

        return false;
    }

    // True if any entry of incompatibleTags exact-matches any entry of tags — the tag-based counterpart to
    // the direct incompatibleAugments reference check above.
    private bool HasSharedTag(string[] incompatibleTags, string[] tags)
    {
        if (incompatibleTags == null || tags == null) return false;
        foreach (var t in incompatibleTags)
            if (System.Array.IndexOf(tags, t) >= 0) return true;
        return false;
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!verified.HasValue) return;
        var v = verified.Value;

        // Verified, not predicted — matches the level-up screen's own visibility (VerifiedIsPresenting),
        // so the countdown doesn't visually disagree with the cards/background it's displayed alongside.
        // Hidden entirely while solo (IsSinglePlayer) — no countdown to show when picking never expires.
        bool timerVisible = v.isPresenting && !IsSinglePlayer;
        int timerSecs = timerVisible ? Mathf.CeilToInt(v.presentingTimer) : 0;
        if (timerSecs != _prevTimerSecs || timerVisible != _prevTimerVisible)
        {
            _prevTimerSecs = timerSecs;
            _prevTimerVisible = timerVisible;
            OnPresentingTimerChanged?.Invoke(timerSecs, timerVisible);
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
        // _appliedPickCount is NEVER reset (picks is whole-run, see State.picks' own doc comment) — it's a
        // monotonic cursor into a monotonically-growing list, same shape as pickedAugments' own handling,
        // so it stays correct across any number of rounds without needing a per-round reset.
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

        var delta = stats.ApplyStatAugmentPick(statDatabase.stats[pick.statIndexA], statDatabase.stats[pick.statIndexB], pick.multiplier, pick.luckMultiplierA, pick.luckMultiplierB);

        _statTotals.TryGetValue(pick.player, out var totals);
        _statTotals[pick.player] = totals + delta;
    }
}
