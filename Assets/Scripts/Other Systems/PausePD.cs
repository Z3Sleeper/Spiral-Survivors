using System.Collections.Generic;
using PurrNet;
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
        public int readyCount;
        public void Dispose() { }
    }

    public bool IsPaused => currentState.isPaused;

    private bool _pendingToggle;
    private readonly Dictionary<PlayerID, int> _pendingReadyDeltas = new();

    private bool _prevIsPaused;
    private int _prevReadyCount = -1;
    private int _prevCountdownSecs = -1;

    protected override void LateAwake() => instance = this;

    public void RequestToggle() => _pendingToggle = true;

    public void AddPendingReady(PlayerID playerId, int delta)
    {
        _pendingReadyDeltas[playerId] = delta;
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
                state.readyCount = 0;
            }
            _pendingReadyDeltas.Clear();
            return;
        }

        // ── Ready-up (multiplayer unpause) ─────────────────────────────────
        if (state.isPaused && _pendingReadyDeltas.Count > 0)
        {
            int totalDelta = 0;
            foreach (var kv in _pendingReadyDeltas) totalDelta += kv.Value;
            _pendingReadyDeltas.Clear();

            int playerCount = NetworkManager.main != null ? NetworkManager.main.playerCount : 1;
            state.readyCount = Mathf.Clamp(state.readyCount + totalDelta, 0, playerCount);

            if (state.readyCount >= playerCount)
            {
                state.isPaused = false;
                state.readyCount = 0;
                return;
            }
        }
        _pendingReadyDeltas.Clear();

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
                state.readyCount = 0;
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
        Time.timeScale = s.isPaused ? 0.0001f : 1f;

        int countdownSecs = s.pauseCountdown > 0f ? Mathf.CeilToInt(s.pauseCountdown) : 0;
        if (countdownSecs != _prevCountdownSecs)
        {
            _prevCountdownSecs = countdownSecs;
            OnCountdownChanged?.Invoke(countdownSecs);
        }

        if (s.readyCount != _prevReadyCount)
        {
            _prevReadyCount = s.readyCount;
            OnReadyCountChanged?.Invoke(s.readyCount, playerCount);
        }

        if (s.isPaused == _prevIsPaused) return;
        _prevIsPaused = s.isPaused;
        OnPauseChanged?.Invoke(s.isPaused);
    }
}
