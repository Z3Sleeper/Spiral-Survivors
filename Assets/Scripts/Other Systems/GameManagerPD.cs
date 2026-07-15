using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class GameManagerPD : PredictedIdentity<GameManagerPD.State>
{
    public struct State : IPredictedData<State>
    {
        public bool gameStarted;
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
        public bool hasWon;
        public bool isGameOver;
        // Ready-up gate for the win/game-over screen — same shape as readyPlayers above, kept separate
        // since it tracks a different question (everyone's seen the end screen and wants to return to the
        // lobby) that only becomes relevant once hasWon/isGameOver is true.
        public DisposableList<PlayerID> endReadyPlayers;
        public void Dispose()
        {
            readyPlayers.Dispose();
            endReadyPlayers.Dispose();
        }
    }

    public static GameManagerPD instance;
    public static event System.Action<bool> OnGameEnded;
    public static event System.Action<int, int> OnEndReadyCountChanged;

    [Header("XP")]
    [SerializeField] private float baseXPToNextLevel = 20f;
    [SerializeField] private float xpLevelStep = 5f;

    [Header("Co-op Difficulty")]
    // The single shared "how much harder is co-op" knob: divides earned XP per extra player here, AND
    // (via CoopToughnessScalar, read by EnemySpawnerPD) scales boss health and elite health/damage there.
    // One value on purpose — tougher enemies should mean less XP per enemy, not two independently-tuned
    // numbers that could drift out of balance with each other.
    [SerializeField] private float coopToughnessScalar = 0.5f;

    [Header("Upgrade Points")]
    // Lets a harder level award more points per kill without having to also inflate enemy XP values
    // (which would throw off the level-up pacing) for balance purposes.
    [SerializeField] private float upgradePointMultiplier = 1f;

    public bool  GameStarted        => currentState.gameStarted;
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
    public float ElapsedRunTime      => currentState.elapsedRunTime;

    private bool _wasGameStartedLastFrame;
    private bool _wasEndedLastFrame;
    private int  _prevEndReadyCount = -1;
    private bool _returnTriggered;

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

    protected override State GetInitialState() => new()
    {
        level = 1,
        xpToNextLevel = baseXPToNextLevel,
        readyPlayers = DisposableList<PlayerID>.Create(4),
        endReadyPlayers = DisposableList<PlayerID>.Create(4),
    };

    // ── Game start ──────────────────────────────────────────────────────────

    protected override void Simulate(ref State state, float delta)
    {
        if (!state.gameStarted)
        {
            var nm = NetworkManager.main;
            if (nm == null || nm.playerCount == 0) return;
            if (state.readyPlayers.Count < nm.playerCount) return;

            state.gameStarted = true;
            return;
        }

        // PausePD.IsPaused already folds in the pause menu, level-up presenting, AND hasWon/isGameOver —
        // one check covers every window the clock should freeze during.
        if (PausePD.instance == null || !PausePD.instance.IsPaused)
            state.elapsedRunTime += delta;

        if (state.hasWon || state.isGameOver) return;

        // Game-over: every present player simultaneously dead. Safe to read CharacterStatsPD.All's own
        // currentState.isDead per-entry from Simulate() (same cross-identity read EnemyBasePD.FindTarget()
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

    // ── Upgrade points ─────────────────────────────────────────────────────

    // Called from EnemyBasePD.UpdateView() off a verified death transition. Forwards straight to the
    // local, per-client, non-predicted tracker rather than touching predicted State here — how many
    // points this machine's save file has earned is a purely local concern with nothing to gain from
    // being part of the simulation (see LocalUpgradePointsTracker for the full reasoning).
    public void AwardUpgradePoints(float baseAmount) => LocalUpgradePointsTracker.instance.AddPoints(baseAmount * upgradePointMultiplier);

    // ── XP ─────────────────────────────────────────────────────────────────

    // Called from XPOrbPD.UpdateView() off a verified collection transition — deterministic, so every
    // machine applies the exact same XP/level result.
    public void AwardXP(float value)
    {
        int playerCount = CharacterStatsPD.All.Count;
        if (playerCount <= 0) return;

        float totalMod = 0f;
        foreach (var stats in CharacterStatsPD.All.Values)
            totalMod += stats.XPModifier;
        float avgXPMod = totalMod / playerCount;

        float coopXPPenalty = 1f + (playerCount - 1) * coopToughnessScalar;
        if (coopXPPenalty <= 0f) coopXPPenalty = 1f;

        currentState.currentXP += value * avgXPMod / coopXPPenalty;

        while (currentState.currentXP >= currentState.xpToNextLevel)
        {
            currentState.currentXP -= currentState.xpToNextLevel;
            currentState.xpToNextLevel += xpLevelStep * ((currentState.level / 5) + 1);
            currentState.level++;
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        if (!verified.HasValue) return;
        var v = verified.Value;

        if (v.gameStarted && !_wasGameStartedLastFrame)
        {
            if (LoadingScreen.instance != null) LoadingScreen.instance.Hide();
            if (MusicPlayer.instance != null) MusicPlayer.instance.Play();
            // Redundant with EnemySpawnerPD.Awake() (which already ran before this could ever fire) — see
            // EnemyBasePD.ResetActiveCounts() for why the redundancy is deliberate.
            EnemyBasePD.ResetActiveCounts();
        }
        _wasGameStartedLastFrame = v.gameStarted;

        bool ended = v.hasWon || v.isGameOver;
        if (ended && !_wasEndedLastFrame)
        {
            OnGameEnded?.Invoke(v.hasWon);
            EnemyBasePD.ResetActiveCounts();
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
