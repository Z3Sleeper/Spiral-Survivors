using PurrNet;
using PurrNet.Packing;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

// Per-connected-player "how long since they joined without finishing local setup" — see
// GameManagerPD.TickSetupTimers's own doc comment for why this exists. Genuinely player-only (setup is a
// connected-player concept), unlike EnemyBasePD's own ContactCooldown, which used to share this same
// PlayerID + ticking-value shape but got rekeyed to PredictedObjectID (2026-08-30) so it works against any
// IFriendlyPD, not just a connected player.
public struct PlayerSetupTimer
{
    public PlayerID player;
    public float elapsed;
}

public class GameManagerPD : PredictedIdentity<GameManagerPD.State>
{
    public struct State : IPredictedData<State>, IDuplicate<State>
    {
        public bool gameStarted;
        // Ticks once gameStarted, same deterministic-clock reasoning as elapsedRunTime below — drives the
        // loading screen's minimum display time (see loadingScreenRevealed) off the SAME synced simulation
        // clock every machine already agrees on, rather than each machine's own local
        // LoadingScreen.Show()-call wall-clock timestamp (which could differ machine-to-machine by however
        // long Steam lobby data propagation/scene-load happened to take on each one). Deliberately NOT
        // reset/reused for anything else — it only ever needs to count up to minimumLoadingScreenSeconds
        // once, then loadingScreenRevealed latches and this stops mattering.
        public float loadingScreenTimer;
        // Rising edge of this (not gameStarted directly) is what UpdateView reacts to for the loading
        // screen hide/music/enemy-count-reset reveal — see its own comment in Simulate() and UpdateView().
        public bool loadingScreenRevealed;
        public float currentXP;
        public int level;
        public float xpToNextLevel;
        // Deterministic run clock — only accumulates once gameStarted, and stops during any PausePD.IsPaused
        // window (pause menu, level-up presenting, or the game having ended), matching what the old
        // Time.timeSinceLevelLoad-based clock did implicitly (that respects Time.timeScale, which those same
        // states already drive to 0). Tracked in State instead of read from Time directly so every machine
        // displays the exact same value rather than whatever each machine's own scene-load timing happened
        // to be.
        public float elapsedRunTime;
        // Explicit, per-player, deterministic confirmation that this player has finished its own local
        // one-time setup (color/progression bonuses applied) — replaces polling CharacterStatsPD.All.Count,
        // which is a plain non-State dictionary populated by UpdateView() on non-tick-synchronized timing.
        // Reading that from Simulate() risked a replayed tick computing a different gameStarted result
        // than the tick's original live pass, the same category of bug already fixed for AwardXP.
        public DisposableList<PlayerID> readyPlayers;
        // Ticked every tick while !gameStarted — see TickSetupTimers's own doc comment.
        public DisposableList<PlayerSetupTimer> setupTimers;
        // Snapshotted once, the exact tick gameStarted flips true — every mid-run co-op scaling system
        // (enemy toughness, XP penalty, multiplayer-only augment availability, etc.) should read THIS, not
        // NetworkManager/PredictionManager's live playerCount, so a player disconnecting mid-run doesn't
        // suddenly rescale difficulty/rewards for whoever remains. Ready-checks (pause, augment presenting,
        // end-screen) are the deliberate exception — those legitimately need to react to who's still
        // actually connected, since they gate on every CURRENT player actively confirming something.
        public int fixedPlayerCount;
        public bool hasWon;
        public bool isGameOver;
        // Ready-up gate for the win/game-over screen — same shape as readyPlayers above, kept separate
        // since it tracks a different question (everyone's seen the end screen and wants to return to the
        // lobby) that only becomes relevant once hasWon/isGameOver is true.
        public DisposableList<PlayerID> endReadyPlayers;
        // Snapshotted once, the exact tick gameStarted flips true — see Difficulty's own doc comment for why
        // this can't just be each machine's own local Awake()-time read of GamePresets.instance.Difficulty
        // the way it used to be. Same "captured once at gameStarted" shape as fixedPlayerCount above, but
        // gated to the SERVER's own write specifically (see Simulate()) rather than every machine converging
        // on the same value naturally the way fixedPlayerCount does (nm.playerCount is already network-
        // agreed by the time it's read; GamePresets.instance.Difficulty is NOT — it's a purely local,
        // per-machine value, correct only on whichever machine is actually hosting).
        public int difficulty;
        public void Dispose()
        {
            readyPlayers.Dispose();
            endReadyPlayers.Dispose();
            setupTimers.Dispose();
        }

        // Was missing entirely — every rollback snapshot fell back to the slow serialize/deserialize
        // round-trip instead of this cheap direct field-copy. See EnemyBaseState.Duplicate's own doc
        // comment for the full reasoning.
        public State Duplicate()
        {
            var copy = this;
            copy.readyPlayers = readyPlayers.Duplicate();
            copy.endReadyPlayers = endReadyPlayers.Duplicate();
            copy.setupTimers = setupTimers.Duplicate();
            return copy;
        }
    }

    public static GameManagerPD instance;
    public static event System.Action<bool> OnGameEnded;
    public static event System.Action<int, int> OnEndReadyCountChanged;

    [Header("Loading Screen")]
    // Minimum time (deterministic simulated seconds, ticked identically on every machine — see
    // State.loadingScreenTimer's own comment) the loading screen stays up after gameStarted before the
    // reveal (hide + music + enemy-count reset) fires, even if setup itself finished faster.
    [SerializeField] private float minimumLoadingScreenSeconds = 5f;

    [Header("XP")]
    [SerializeField] private float baseXPToNextLevel = 20f;
    [SerializeField] private float xpLevelStep = 5f;
    // How many levels the xpLevelStep multiplier stays flat for before increasing by 1 (see AwardXP() —
    // e.g. 5 means levels 0-4 use step*1, 5-9 use step*2, 10-14 use step*3, etc.).
    [SerializeField] private int xpLevelStepInterval = 5;

    [Header("Co-op Difficulty")]
    // The shared "how much harder is co-op" knob for every NON-boss category — read via
    // CoopToughnessScalar below by EnemySpawnerPD (normal/pleb/elite)/EventTargetScalingPD, scales health
    // (every non-boss category) and points (every category, bosses included — see BossCoopToughnessScalar's
    // own doc comment for why points didn't need its own split) up with player count. Deliberately does
    // NOT touch XP anywhere anymore (2026-09-03 fix — see AwardXP's own doc comment for why the old
    // "multiply XP value by this, then divide back out on pickup" pairing was removed): XP-per-orb needs to
    // be genuinely player-count-invariant now that total enemy count no longer scales with player count
    // either (see EnemySpawnerPD's own coopScale doc comment for that history) — multiplying then dividing
    // by the same factor was already a mathematical no-op for XP specifically, just needlessly round-tripped
    // through two separately-tuned formulas that could in principle drift out of sync, so both sides were
    // deleted outright rather than left as dead ceremony.
    [SerializeField] private float coopToughnessScalar = 0.5f;
    // Bosses' own separate co-op health knob (2026-10-17 split — see EnemySpawnerPD.SpawnBosses' own doc
    // comment for why one shared scalar couldn't serve both: 50%/player made normals feel right but let
    // bosses melt, and 75%/player made normals too tanky while bosses still melted, meaning bosses simply
    // need a steeper per-player curve than everything else, not a shared compromise value). Only feeds
    // health — points still goes through the shared CoopToughnessScalar above for every category, bosses
    // included, since points-per-player-count was never the part that felt wrong.
    [SerializeField] private float bossCoopToughnessScalar = 1f;

    [Header("Upgrade Points")]
    // General-purpose manual tuning knob for overall points-per-kill balance, independent of Threat Level —
    // composes with (multiplies alongside) DifficultyPointMultiplier below, doesn't replace it. Points are
    // their own tracked value per enemy now (EnemyBasePD.pointsValue, 2026-08-29), no longer derived from XP
    // at all, so this no longer needs to reason about XP/level-pacing side effects.
    [SerializeField] private float upgradePointMultiplier = 1f;

    [Header("Connection Watchdog")]
    // A connected player whose own local one-time setup (PlayerDataPD's isOwner/verified-gated color +
    // progression bonuses + input-enable chain, culminating in MarkPlayerReady()) hasn't completed within
    // this many seconds gets forcibly kicked — see TickSetupTimers's own doc comment for why this exists.
    [SerializeField] private float maxSetupWaitSeconds = 30f;

    public bool  GameStarted        => currentState.gameStarted;
    // True only once the loading screen has ACTUALLY been hidden (see State.loadingScreenRevealed's own
    // doc comment) — strictly later than GameStarted (can never be true while GameStarted is false; see
    // Simulate()'s own early-return ordering), by up to minimumLoadingScreenSeconds. Any system whose own
    // effects the player should never experience before they can actually SEE the game (enemy spawning,
    // the boss-spawn countdown, player input/movement processing) should gate on THIS, not GameStarted
    // directly — gating on GameStarted alone let enemies spawn and the boss timer start counting down
    // while the loading screen was still covering the screen, so by the time it actually cleared, the
    // player found enemies already nearly on top of them with a countdown already several seconds spent
    // (confirmed live, 2026-08-18).
    public bool  LoadingScreenRevealed => currentState.loadingScreenRevealed;
    public float CurrentXP          => currentState.currentXP;
    public int   Level              => currentState.level;
    public float XPToNextLevel      => currentState.xpToNextLevel;
    public bool  HasWon             => currentState.hasWon;
    public bool  IsGameOver         => currentState.isGameOver;
    // Verified-only variant for PausePD's Time.timeScale side effect — same reasoning as
    // AugmentManagerPD.VerifiedIsPresenting: that's a non-rollback-safe global side effect, so it must only
    // react to confirmed state, never raw currentState (an in-flight misprediction correction would
    // oscillate it there too).
    public bool  VerifiedGameEnded  => verifiedState.HasValue && (verifiedState.Value.hasWon || verifiedState.Value.isGameOver);
    public float CoopToughnessScalar => coopToughnessScalar;
    public float BossCoopToughnessScalar => bossCoopToughnessScalar;
    public float ElapsedRunTime      => currentState.elapsedRunTime;
    // See State.fixedPlayerCount's own doc comment — the number every co-op-scaling system should read.
    public int   FixedPlayerCount    => currentState.fixedPlayerCount;

    private bool _wasLoadingScreenRevealedLastFrame;
    private bool _wasEndedLastFrame;
    private int  _prevEndReadyCount = -1;
    private bool _returnTriggered;

    // Plain field, not predicted State — just a read-and-store for now (2026-08-20), same scope EnemySpawnerPD's
    // own GamePresets read has: nothing actually USES this yet, and it isn't networked/reconciled across
    // State-backed (see State.difficulty's own doc comment) — the HOST's own GamePresets.instance.Difficulty
    // is the single source of truth, written ONCE by the server into replicated State at the same moment
    // fixedPlayerCount is (see Simulate()), so every machine (client included) converges on the exact same
    // value instead of each independently reading its own local GamePresets (which, for a client, reflects
    // whatever THEY last happened to host with — completely unrelated to the match they're actually playing
    // in now). Defaults to 1 before gameStarted flips true, same as GetInitialState() below.
    public int Difficulty => currentState.difficulty;

    private void Awake()
    {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;

        // Same reasoning as EnemySpawnerPD.Awake()'s EnemyBasePD.ResetActiveCounts() call: fires once per
        // Game scene load, before any player could possibly be registered yet, so it's unconditionally
        // safe and doesn't depend on domain reload or PlayerDataPD.Destroyed() having fired cleanly for
        // every player from whatever the previous session's exit looked like.
        CharacterStatsPD.All.Clear();
    }

    // Not DontDestroyOnLoad — see LocalUIManagerPD.OnDestroy's own doc comment for the full reasoning
    // (guarded against nulling out a newer instance; without this, `instance` was left dangling on a
    // destroyed object across a scene return to lobby). Destroyed(), not raw OnDestroy() — same PD lifecycle
    // convention WeaponBasePD/EnemyBasePD's own Destroyed() overrides already use.
    protected override void Destroyed()
    {
        if (instance == this) instance = null;
    }

    protected override State GetInitialState() => new()
    {
        level = 1,
        difficulty = 1,
        xpToNextLevel = baseXPToNextLevel,
        readyPlayers = DisposableList<PlayerID>.Create(4),
        endReadyPlayers = DisposableList<PlayerID>.Create(4),
        setupTimers = DisposableList<PlayerSetupTimer>.Create(4),
    };

    // ── Game start ──────────────────────────────────────────────────────────

    protected override void Simulate(ref State state, float delta)
    {
        if (!state.gameStarted)
        {
            var nm = NetworkManager.main;
            TickSetupTimers(ref state, nm, delta);

            if (nm == null || nm.playerCount == 0) return;
            if (state.readyPlayers.Count < nm.playerCount) return;

            state.fixedPlayerCount = nm.playerCount;
            // Server-gated, unlike fixedPlayerCount right above — nm.playerCount is already network-agreed
            // by this point (every machine computes the same value), but GamePresets.instance.Difficulty is
            // NOT: it's purely local per-machine, correct only on whichever machine is actually hosting. A
            // client's own Simulate() simply skips this write and keeps predicting the State default (0)
            // until the server's own verified State (carrying the real value) replicates in and corrects
            // it — the standard PurrDiction client-correction flow, same as any other server-decided value.
            if (nm.isServer)
                state.difficulty = GamePresets.instance != null ? GamePresets.instance.Difficulty : 1;
            state.gameStarted = true;
            return;
        }

        // Latches once — see State.loadingScreenTimer/loadingScreenRevealed's own comments. Unconditional
        // (not PausePD-gated like elapsedRunTime below): nothing could legitimately pause the game before
        // its own reveal has even fired yet.
        if (!state.loadingScreenRevealed)
        {
            state.loadingScreenTimer += delta;
            if (state.loadingScreenTimer >= minimumLoadingScreenSeconds)
                state.loadingScreenRevealed = true;
        }

        // PausePD.IsPaused already folds in the pause menu, level-up presenting, AND hasWon/isGameOver —
        // one check covers every window the clock should freeze during.
        if (PausePD.instance == null || !PausePD.instance.IsPaused)
            state.elapsedRunTime += delta;

        if (state.hasWon || state.isGameOver) return;

        // Game-over: every present player simultaneously dead. Safe to read CharacterStatsPD.All's own
        // currentState.isDead per-entry from Simulate() (same cross-identity read EnemyBasePD.FindClosestTarget()
        // already does) — ReviveZonePD requires a nearby *alive* ally to charge a revive, so this really
        // is unrecoverable, not just "everyone's currently downed."
        int deadCount = 0, playerCount = 0;
        foreach (var kv in CharacterStatsPD.All)
        {
            playerCount++;
            if (kv.Value.IsDead) deadCount++;
        }
        if (playerCount > 0 && deadCount >= playerCount)
            state.isGameOver = true;
    }

    // ── Win / game over ────────────────────────────────────────────────────

    // Called from EnemySpawnerPD.Simulate() once enough bosses have been defeated — same cross-identity,
    // mutate-my-own-state-directly pattern as PlayerDataPD.Simulate() calling MarkPlayerReady().
    public void TriggerWin()
    {
        if (currentState.hasWon || currentState.isGameOver) return;
        currentState.hasWon = true;
    }

    // Called from PlayerControllerPD.Simulate() off the end-screen ready button. Idempotent, matching
    // SetPlayerReady()/MarkPlayerReady() — safe to call every tick once latched.
    public void SetEndScreenReady(PlayerID player)
    {
        if (!currentState.hasWon && !currentState.isGameOver) return;
        for (int i = 0; i < currentState.endReadyPlayers.Count; i++)
            if (currentState.endReadyPlayers[i] == player) return;
        currentState.endReadyPlayers.Add(player);
    }

    // Called from PlayerDataPD.Simulate() once that player's own client confirms (via input.setupComplete)
    // that its local one-time setup is done — same cross-identity, mutate-my-own-state-directly pattern
    // as PausePD.SetPlayerReady()/AugmentManagerPD.SubmitPick(). Idempotent, so it's fine to call this
    // every tick once setupComplete latches true rather than needing a separate one-shot guard.
    public void MarkPlayerReady(PlayerID player)
    {
        for (int i = 0; i < currentState.readyPlayers.Count; i++)
            if (currentState.readyPlayers[i] == player) return;

        currentState.readyPlayers.Add(player);
    }

    // ── Connection watchdog ────────────────────────────────────────────────

    // A marginal/flaky connection can register as a genuinely distinct joined player (nm.playerCount
    // includes them, so this isn't the phantom-join/counter-inflation bug LobbySceneController already
    // guards against) yet never stabilize enough for PlayerDataPD's isOwner/verified-gated setup chain to
    // finish — which, before this existed, would silently block state.readyPlayers.Count from ever
    // reaching nm.playerCount, stalling the ENTIRE lobby's game start for every other player too, with no
    // recovery path. Ticked identically on every machine (deterministic, off predicted State), so every
    // machine agrees on the exact same elapsed time and therefore the exact same tick a kick threshold is
    // crossed — only the actual KickPlayer() call is gated to the server, since severing a connection is
    // inherently a server-authoritative action; every other machine just observes the resulting
    // nm.players/nm.playerCount change on a later tick like any other disconnect.
    private void TickSetupTimers(ref State state, NetworkManager nm, float delta)
    {
        if (nm == null) return;

        // Drop tracking for anyone no longer connected (they left on their own — nothing left to kick) or
        // who has since finished setup (readyPlayers already covers them) — keeps setupTimers from
        // accumulating stale entries across a long lobby-wait.
        for (int i = state.setupTimers.Count - 1; i >= 0; i--)
        {
            var player = state.setupTimers[i].player;

            bool stillConnected = false;
            for (int j = 0; j < nm.players.Count; j++)
                if (nm.players[j] == player) { stillConnected = true; break; }

            bool alreadyReady = false;
            for (int j = 0; j < state.readyPlayers.Count; j++)
                if (state.readyPlayers[j] == player) { alreadyReady = true; break; }

            if (!stillConnected || alreadyReady)
                state.setupTimers.RemoveAt(i);
        }

        for (int i = 0; i < nm.players.Count; i++)
        {
            var player = nm.players[i];

            bool alreadyReady = false;
            for (int j = 0; j < state.readyPlayers.Count; j++)
                if (state.readyPlayers[j] == player) { alreadyReady = true; break; }
            if (alreadyReady) continue;

            int idx = -1;
            for (int j = 0; j < state.setupTimers.Count; j++)
                if (state.setupTimers[j].player == player) { idx = j; break; }

            if (idx < 0)
            {
                state.setupTimers.Add(new PlayerSetupTimer { player = player, elapsed = 0f });
                continue;
            }

            var timer = state.setupTimers[idx];
            timer.elapsed += delta;

            if (timer.elapsed >= maxSetupWaitSeconds)
            {
                state.setupTimers.RemoveAt(idx);
                if (nm.isServer)
                    nm.playerModule?.KickPlayer(player);
            }
            else
            {
                state.setupTimers[idx] = timer;
            }
        }
    }

    // ── Upgrade points ─────────────────────────────────────────────────────

    // Called from EnemyBasePD.UpdateView() off a verified death transition. Forwards straight to the
    // local, per-client, non-predicted tracker rather than touching predicted State here — how many
    // points this machine's save file has earned is a purely local concern with nothing to gain from
    // being part of the simulation (see LocalUpgradePointsTracker for the full reasoning).
    // DifficultyPointMultiplier folded in here (not into upgradePointMultiplier directly) so the two stay
    // independently visible/tunable — one's a manual balance knob, the other's fully derived from Difficulty.
    public void AwardUpgradePoints(float baseAmount) => LocalUpgradePointsTracker.instance.AddPoints(baseAmount * upgradePointMultiplier * DifficultyPointMultiplier);

    // Linear +100% per Threat Level (2026-08-29, reworked from an exponential 1.5^(Difficulty-1) curve that
    // reached ~7.59x at difficulty 6 — explicit design simplification, too much math for both the designer
    // and players to reason about). Difficulty is already 1-indexed and matches Threat Level directly, so
    // this needs no further transform: TL1 -> 1x (100%), TL2 -> 2x (200%), TL3 -> 3x (300%), etc. Now matches
    // the "multiply base, no exponential" convention every other difficulty/tier growth field in this
    // codebase already uses (see project_health_tier_growth_linear memory) — no longer an exception.
    private float DifficultyPointMultiplier => Difficulty;

    // ── XP ─────────────────────────────────────────────────────────────────

    // Called from XPOrbPD.UpdateView() off a verified collection transition — deterministic, so every
    // machine applies the exact same XP/level result.
    //
    // No coop division anymore (2026-09-03 fix, was `value * avgXPMod / coopXPPenalty` where coopXPPenalty
    // used the exact same 1f+(playerCount-1)*coopToughnessScalar formula EnemySpawnerPD's own coopScale
    // does) — that division existed to cancel out an equal coopScale multiplier baked into `value` at
    // spawn time, which made sense back when total enemy COUNT also scaled with player count: cancelling
    // the per-kill value inflation was what kept "more, individually-cheaper kills" summing to the
    // same-ish total XP over a run regardless of team size. Enemy count is no longer player-count-scaled
    // for ANY category (see EnemySpawnerPD's own coopScale doc comment, 2026-09-02) — so with a fixed
    // total number of kills/orbs over a run either way, the only way to keep total XP gained equal between
    // solo and co-op is for `value` to never carry a coopScale factor in the first place (confirmed —
    // EnemySpawnerPD/EventTargetScalingPD's own xpMultiplier no longer multiplies by coopScale either).
    // Per-player pacing in co-op is now simply that same fixed total pool split across more players, an
    // unavoidable consequence of more people sharing the same amount of content, not something an XP
    // formula can or should try to compensate for.
    public void AwardXP(float value)
    {
        int liveCount = CharacterStatsPD.All.Count;
        if (liveCount <= 0) return;

        // Live count on purpose — this is averaging currently-connected players' own XPModifier stats, not
        // a co-op difficulty scale, so a disconnected player correctly stops dragging the average down.
        float totalMod = 0f;
        foreach (var stats in CharacterStatsPD.All.Values)
            totalMod += stats.XPModifier;
        float avgXPMod = totalMod / liveCount;

        currentState.currentXP += value * avgXPMod;

        while (currentState.currentXP >= currentState.xpToNextLevel)
        {
            currentState.currentXP -= currentState.xpToNextLevel;
            currentState.xpToNextLevel += xpLevelStep * ((currentState.level / xpLevelStepInterval) + 1);
            currentState.level++;
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!verified.HasValue) return;
        var v = verified.Value;

        // Reacts to loadingScreenRevealed, NOT gameStarted directly — that flag already latched true only
        // once minimumLoadingScreenSeconds of SYNCED simulation time have passed since gameStarted (see
        // Simulate()'s own comment), so every machine reveals at the exact same simulated tick regardless
        // of when each one's own local LoadingScreen.Show() call happened to fire in real time. This is
        // what keeps one machine's loading screen from clearing (and the game becoming visible/audible)
        // while another machine's hasn't caught up yet.
        if (v.loadingScreenRevealed && !_wasLoadingScreenRevealedLastFrame)
        {
            if (LoadingScreen.instance != null) LoadingScreen.instance.Hide();
            if (MusicPlayer.instance != null) MusicPlayer.instance.Play();
            // Redundant with EnemySpawnerPD.Awake() (which already ran before this could ever fire) — see
            // EnemyBasePD.ResetActiveCounts() for why the redundancy is deliberate.
            EnemyBasePD.ResetActiveCounts();
        }
        _wasLoadingScreenRevealedLastFrame = v.loadingScreenRevealed;

        bool ended = v.hasWon || v.isGameOver;
        if (ended && !_wasEndedLastFrame)
        {
            OnGameEnded?.Invoke(v.hasWon);
            EnemyBasePD.ResetActiveCounts();
            // Local, per-client save-file concern (same reasoning as LocalUpgradePointsTracker/
            // ProgressionManager elsewhere) — every machine in the match independently unlocks its OWN next
            // difficulty on a win, same "everyone earns from a win, not just the host" convention points
            // already follow. Verified-state edge-detected (this whole block only runs once per real ended
            // transition, off UpdateView()'s verified param), not raw Simulate() — a predicted-context call
            // here would re-fire the save-file write on every resimulated replay of the winning tick.
            // UnlockManager.Queue is scene-independent (plain static queue, no MonoBehaviour) — safe to call
            // from here in the Game scene; LobbySceneController already drains it and shows the popup once
            // we're back in the Lobby scene, same path ProgressionManager.CommitPoints already uses for shop
            // unlocks. Message becomes "Threat Level {unlocked} Unlocked!" via ShowNextUnlock's own format.
            if (v.hasWon)
            {
                int unlocked = ProgressionManager.UnlockNextDifficulty(Difficulty);
                if (unlocked > 0) UnlockManager.Queue($"Threat Level {unlocked}");
            }
        }
        _wasEndedLastFrame = ended;

        // Instant local feedback on your own ready-click, same reasoning as PausePD's ready count —
        // currentState instead of verified, since verified lags a network round trip behind the press.
        int readyCount = currentState.endReadyPlayers.Count;
        if (readyCount != _prevEndReadyCount)
        {
            _prevEndReadyCount = readyCount;
            int total = NetworkManager.main != null ? NetworkManager.main.playerCount : 1;
            OnEndReadyCountChanged?.Invoke(readyCount, total);
        }

        // The actual return-to-lobby trigger must come off verified (irreversible side effect — an
        // RPC/scene-load kick-off, not a state mutation), fired once. ReturnToLobbyManager.ReturnToLobby()
        // already no-ops on non-server, so every machine reaching this call is safe.
        if (ended && !_returnTriggered)
        {
            int total = NetworkManager.main != null ? NetworkManager.main.playerCount : 1;
            if (total > 0 && v.endReadyPlayers.Count >= total)
            {
                _returnTriggered = true;
                if (ReturnToLobbyManager.instance != null) ReturnToLobbyManager.instance.ReturnToLobby();
            }
        }
    }
}
