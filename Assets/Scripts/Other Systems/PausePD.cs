using PurrNet;
using PurrNet.Pooling;
using PurrNet.Prediction;
using UnityEngine;

public class PausePD : PredictedIdentity<PausePD.State>
{
    public static PausePD instance;
    public static event System.Action<bool> OnPauseChanged;
    public static event System.Action<int, int> OnReadyCountChanged;
    public static event System.Action<int> OnCountdownChanged;

    private const float CountdownDuration = 3f;

    public struct State : IPredictedData<State>
    {
        public bool isPaused;
        public float pauseCountdown;
        public DisposableList<PlayerID> readyPlayers;
        public void Dispose() => readyPlayers.Dispose();
    }

    // Also true while a level-up needs picks — folding it in here means every existing IsPaused check
    // across the codebase (enemies, weapons, orbs, etc.) automatically halts gameplay for that too,
    // without touching each of those systems individually.
    public bool IsPaused => currentState.isPaused || (AugmentManagerPD.instance != null && AugmentManagerPD.instance.IsPresenting);

    private bool _pendingToggle;

    private bool _prevIsPaused;
    private int _prevReadyCount = -1;
    private int _prevCountdownSecs = -1;

    protected override void LateAwake() => instance = this;

    protected override State GetInitialState() => new() { readyPlayers = DisposableList<PlayerID>.Create(4) };

    public void RequestToggle() => _pendingToggle = true;

    // Called directly from PlayerControllerPD.Simulate() — mutates our own predicted State immediately,
    // same cross-identity pattern as CharacterStatsPD.StartSurge()/AugmentManagerPD.SubmitPick(). The
    // previous version staged deltas in a plain (non-State) Dictionary and drained it here, which only
    // produced correct results if every player's Simulate() ran before this one in the same tick — an
    // ordering assumption, not a guarantee, and it didn't roll back cleanly on replay since the
    // dictionary wasn't part of State. That caused a brief, client-only mispredicted unpause.
    public void SetPlayerReady(PlayerID player, bool ready)
    {
        for (int i = 0; i < currentState.readyPlayers.Count; i++)
        {
            if (currentState.readyPlayers[i] != player) continue;
            if (!ready) currentState.readyPlayers.RemoveAt(i);
            return;
        }
        if (ready) currentState.readyPlayers.Add(player);
    }

    protected override void Simulate(ref State state, float delta)
    {
        // ── Countdown ──────────────────────────────────────────────────────
        if (state.pauseCountdown > 0f)
        {
            state.pauseCountdown -= delta;
            if (state.pauseCountdown <= 0f)
            {
                state.pauseCountdown = 0f;
                state.isPaused = true;
                state.readyPlayers.Clear();
            }
            return;
        }

        // ── Ready-up (multiplayer unpause) ─────────────────────────────────
        if (state.isPaused)
        {
            int playerCount = NetworkManager.main != null ? NetworkManager.main.playerCount : 1;
            if (state.readyPlayers.Count >= playerCount)
            {
                state.isPaused = false;
                state.readyPlayers.Clear();
                return;
            }
        }

        // ── Toggle ─────────────────────────────────────────────────────────
        if (!_pendingToggle) return;
        _pendingToggle = false;

        bool isSinglePlayer = NetworkManager.main == null || NetworkManager.main.playerCount <= 1;

        if (state.isPaused)
        {
            if (!isSinglePlayer) return;
            state.isPaused = false;
        }
        else
        {
            if (isSinglePlayer)
            {
                state.isPaused = true;
                state.readyPlayers.Clear();
            }
            else
            {
                state.pauseCountdown = CountdownDuration;
            }
        }
    }

    protected override void UpdateView(State viewState, State? verified)
    {
        // Use verified state for all visuals — viewState can flicker during rollback.
        var s = verified ?? viewState;
        int playerCount = NetworkManager.main != null ? NetworkManager.main.playerCount : 1;

        // timeScale is a side-effect that must NOT live in Simulate (rollback would oscillate it).
        // Drive it here from stable verified state instead.
        bool presenting = AugmentManagerPD.instance != null && AugmentManagerPD.instance.VerifiedIsPresenting;
        Time.timeScale = (s.isPaused || presenting) ? 0.0001f : 1f;

        int countdownSecs = s.pauseCountdown > 0f ? Mathf.CeilToInt(s.pauseCountdown) : 0;
        if (countdownSecs != _prevCountdownSecs)
        {
            _prevCountdownSecs = countdownSecs;
            OnCountdownChanged?.Invoke(countdownSecs);
        }

        int readyCount = s.readyPlayers.Count;
        if (readyCount != _prevReadyCount)
        {
            _prevReadyCount = readyCount;
            OnReadyCountChanged?.Invoke(readyCount, playerCount);
        }

        if (s.isPaused == _prevIsPaused) return;
        _prevIsPaused = s.isPaused;
        OnPauseChanged?.Invoke(s.isPaused);
    }
}
